using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using BayiOS.Api.Platform.Observability;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>
/// #55 end to end: the per-request log line carries the allowlisted fields only (data-policy §8), 5xx responses are
/// counted and reported through the error-tracking hook, and business counters fire only after commit.
/// </summary>
public class ObservabilityTests(ApiFixture fixture)
{
    private const string Email = "ali.veli@example.com";
    private const string CookieSecret = "zzzCOOKIEsecretzzz";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_log_line_has_route_template_status_duration_and_ids_but_no_personal_data()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var capture = new CapturingLoggerProvider();
        await using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(capture)));
        var client = factory.CreateClient();
        var token = TestTokens.Create(owner.UserId, Email);
        var correlation = Guid.NewGuid().ToString("N");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/products?q=" + Uri.EscapeDataString(Email));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
        request.Headers.Add("X-Correlation-Id", correlation);
        request.Headers.Add("Cookie", "sb-access-token=" + CookieSecret);
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var requestLogs = capture.Entries.Where(e => Equals(e.State.GetValueOrDefault("CorrelationId"), correlation)).ToList();
        var line = Assert.Single(requestLogs, e => e.Category == typeof(RequestLoggingMiddleware).FullName);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("GET", line.State["Method"]);
        Assert.Equal("/api/v1/products", line.State["Route"]);
        Assert.Equal(200, line.State["StatusCode"]);
        Assert.IsType<double>(line.State["DurationMs"]);
        Assert.Equal(owner.TenantId, line.State["TenantId"]);
        Assert.Equal(owner.UserId, line.State["UserId"]);
        Assert.Contains(owner.TenantId.ToString(), line.JsonLine, StringComparison.Ordinal);

        foreach (var entry in requestLogs)
        {
            foreach (var forbidden in new[] { token, Email, Uri.EscapeDataString(Email), CookieSecret, "?q=" })
            {
                Assert.DoesNotContain(forbidden, entry.JsonLine, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, entry.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(entry.State.Values, v => v?.ToString()?.Contains(forbidden, StringComparison.Ordinal) == true);
            }
        }
    }

    [Fact]
    public async Task Unhandled_server_error_is_logged_counted_and_reported_without_connection_details()
    {
        const string password = "Pw-NotReal-123";
        var capture = new CapturingLoggerProvider();
        await using var broken = fixture.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", $"Host=127.0.0.1;Port=1;Database=x;Username=bayios_app;Password={password};Timeout=2")
            .ConfigureLogging(logging => logging.AddProvider(capture)));
        var client = broken.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.NewGuid()));
        var correlation = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation);

        var measurements = new ConcurrentBag<(string Name, Dictionary<string, object?> Tags)>();
        using var listener = Listen(measurements);

        var response = await client.GetAsync("/api/v1/me", Ct);
        var ready = await client.GetAsync("/health/ready", Ct);

        // The readiness 503 is a state report: not a 5xx for the error-rate metric.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.DoesNotContain(measurements, m => m.Name == "bayios.http.server_errors" && Equals(m.Tags.GetValueOrDefault("http.route"), "/health/ready"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("127.0.0.1", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Contains(measurements, m => m.Name == "bayios.http.server_errors"
            && Equals(m.Tags.GetValueOrDefault("http.route"), "/api/v1/me")
            && Equals(m.Tags.GetValueOrDefault("http.response.status_code"), 500));
        Assert.Contains(measurements, m => m.Name == "bayios.errors.unhandled" && Equals(m.Tags.GetValueOrDefault("source"), "http"));

        var requestLogs = capture.Entries.Where(e => Equals(e.State.GetValueOrDefault("CorrelationId"), correlation)).ToList();
        var line = Assert.Single(requestLogs, e => e.Category == typeof(RequestLoggingMiddleware).FullName && Equals(e.State["Route"], "/api/v1/me"));
        Assert.Equal(LogLevel.Error, line.Level);
        var probeLine = Assert.Single(requestLogs, e => e.Category == typeof(RequestLoggingMiddleware).FullName && Equals(e.State["Route"], "/health/ready"));
        Assert.Equal(LogLevel.Warning, probeLine.Level);
        Assert.Equal(500, line.State["StatusCode"]);
        Assert.All(requestLogs, e => Assert.DoesNotContain(password, e.JsonLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_callbacks_run_after_commit_and_never_after_rollback()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.AppConnectionString);
        var committed = 0;
        var rolledBack = 0;

        await using (var unitOfWork = new UnitOfWork(dataSource, new RequestContext()))
        {
            await unitOfWork.BeginAsync(readOnly: true, Ct);
            unitOfWork.OnCommitted(() => committed++);
            Assert.Equal(0, committed);
            await unitOfWork.CommitAsync(Ct);
        }

        await using (var unitOfWork = new UnitOfWork(dataSource, new RequestContext()))
        {
            await unitOfWork.BeginAsync(readOnly: true, Ct);
            unitOfWork.OnCommitted(() => rolledBack++);
            await unitOfWork.RollbackAsync(Ct);
            await unitOfWork.CommitAsync(Ct);
        }

        await using (var unitOfWork = new UnitOfWork(dataSource, new RequestContext()))
        {
            await unitOfWork.BeginAsync(readOnly: true, Ct);
            unitOfWork.OnCommitted(() => rolledBack++);
            // Disposed without commit (request failed): rolled back, callback dropped.
        }

        Assert.Equal(1, committed);
        Assert.Equal(0, rolledBack);
    }

    internal static MeterListener Listen(ConcurrentBag<(string Name, Dictionary<string, object?> Tags)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BayiOsMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                values[tag.Key] = tag.Value;
            }

            measurements.Add((instrument.Name, values));
        });
        listener.Start();
        return listener;
    }
}

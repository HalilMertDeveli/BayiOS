using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BayiOS.Api.Platform.Jobs;
using BayiOS.Api.Platform.Observability;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Platform.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BayiOS.UnitTests.Platform;

/// <summary>#55: metric registration, OTLP opt-in, production logging pipeline and readiness thresholds.</summary>
public class ObservabilityTests
{
    [Fact]
    public void Every_business_instrument_is_published_on_the_BayiOS_meter()
    {
        var published = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == BayiOsMetrics.MeterName)
                {
                    published.Add(instrument.Name);
                }
            },
        };
        listener.Start();

        string[] expected =
        [
            "bayios.orders.approved", "bayios.inventory.reservation_shortages", "bayios.inventory.negative_stock_confirmed",
            "bayios.jobs.dead_lettered", "bayios.jobs.retried", "bayios.imports.failed", "bayios.ai.fallbacks",
            "bayios.http.server_errors", "bayios.errors.unhandled", "bayios.audit.access_denials", "bayios.jobs.queue_lag",
            "bayios.jobs.queue_depth",
        ];
        Assert.Equal(expected.Order(), BayiOsMetrics.InstrumentNames.Order());
        Assert.All(expected, name => Assert.Contains(name, published));
    }

    [Theory]
    [InlineData(AuditActions.OrderApproved, "bayios.orders.approved", null)]
    [InlineData(AuditActions.InventoryNegativeStockConfirmed, "bayios.inventory.negative_stock_confirmed", null)]
    [InlineData("import.failed", "bayios.imports.failed", "failed")]
    [InlineData("import.rollback_failed", "bayios.imports.failed", "rollback_failed")]
    public void Committed_audit_actions_feed_their_business_counter(string action, string instrument, string? outcome)
    {
        var measurements = new ConcurrentBag<(string Name, long Value, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == BayiOsMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(i);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) =>
        {
            string? tagOutcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    tagOutcome = tag.Value as string;
                }

                // No tenant/user ids on metrics (cardinality, data-policy §8).
                Assert.DoesNotContain("tenant", tag.Key, StringComparison.OrdinalIgnoreCase);
            }

            measurements.Add((i.Name, value, tagOutcome));
        });
        listener.Start();

        BayiOsMetrics.RecordCommittedAudit(action);
        BayiOsMetrics.RecordCommittedAudit("tenant.settings_changed");

        Assert.Contains(measurements, m => m.Name == instrument && m.Value == 1 && m.Outcome == outcome);
    }

    [Fact]
    public void OpenTelemetry_is_off_unless_an_OTLP_endpoint_is_configured()
    {
        using var app = Build("Production", otlpEndpoint: null);

        Assert.False(ObservabilitySetup.IsOtlpEnabled(app.Configuration));
        Assert.Null(app.Services.GetService<MeterProvider>());
        Assert.Null(app.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void OpenTelemetry_registers_traces_and_metrics_when_the_OTLP_endpoint_is_set()
    {
        using var app = Build("Production", otlpEndpoint: "http://127.0.0.1:4317");

        Assert.True(ObservabilitySetup.IsOtlpEnabled(app.Configuration));
        Assert.NotNull(app.Services.GetService<MeterProvider>());
        Assert.NotNull(app.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void Production_logs_only_to_the_redacting_json_console()
    {
        using var app = Build("Production", otlpEndpoint: null);

        var provider = Assert.Single(app.Services.GetServices<ILoggerProvider>());
        Assert.IsType<ConsoleLoggerProvider>(provider);
        Assert.Equal(RedactingJsonConsoleFormatter.FormatterName, app.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        Assert.Contains(app.Services.GetServices<ConsoleFormatter>(), f => f is RedactingJsonConsoleFormatter);
        Assert.IsType<CountingErrorReporter>(app.Services.GetRequiredService<IErrorReporter>());
    }

    [Fact]
    public void Error_tracking_provider_otel_is_opt_in()
    {
        using var app = Build("Production", otlpEndpoint: null, errorProvider: "otel");

        Assert.IsType<OpenTelemetryErrorReporter>(app.Services.GetRequiredService<IErrorReporter>());
    }

    [Theory]
    [InlineData(0, false, HealthStatus.Healthy)]
    [InlineData(299, false, HealthStatus.Healthy)]
    [InlineData(300, false, HealthStatus.Degraded)]
    [InlineData(7200, false, HealthStatus.Degraded)] // API-only instance: never unready for a worker problem
    [InlineData(1799, true, HealthStatus.Degraded)]
    [InlineData(1800, true, HealthStatus.Unhealthy)]
    public void Queue_lag_thresholds_follow_the_documented_defaults(int lagSeconds, bool workerRunsHere, HealthStatus expected)
    {
        Assert.Equal(expected, QueueLagHealthCheck.Classify(TimeSpan.FromSeconds(lagSeconds), new HealthOptions(), workerRunsHere));
    }

    [Fact]
    public void Queue_lag_unhealthy_threshold_zero_disables_the_503()
    {
        var options = new HealthOptions { QueueLagUnhealthyAfter = TimeSpan.Zero };

        Assert.Equal(HealthStatus.Degraded, QueueLagHealthCheck.Classify(TimeSpan.FromDays(1), options, workerRunsHere: true));
    }

    private static WebApplication Build(string environment, string? otlpEndpoint, string? errorProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint;
        if (errorProvider is not null)
        {
            builder.Configuration["ErrorTracking:Provider"] = errorProvider;
        }

        builder.AddBayiOsObservability();
        return builder.Build();
    }
}

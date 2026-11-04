using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>#10: readiness reaches PostgreSQL (as bayios_app); liveness stays dependency-free.</summary>
public class ReadinessTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ready_returns_200_when_database_is_reachable()
    {
        var response = await fixture.CreateClient().GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["status"]);
    }

    [Fact]
    public async Task Ready_returns_503_and_liveness_stays_200_when_database_is_down()
    {
        await using var broken = fixture.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2"));
        var client = broken.CreateClient();

        var ready = await client.GetAsync("/health/ready", Ct);
        var live = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("unavailable", (await ready.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["status"]);
        Assert.DoesNotContain("127.0.0.1", await ready.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Unconfigured_instance_starts_but_is_not_ready_and_rejects_tokens()
    {
        // Mirrors the container smoke test (no env): process must start, yet fail closed.
        await using var unconfigured = fixture.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", string.Empty)
            .UseSetting("Auth:Authority", string.Empty)
            .UseSetting("Auth:Audience", string.Empty));
        var client = unconfigured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid()));

        var live = await client.GetAsync("/health", Ct);
        var ready = await client.GetAsync("/health/ready", Ct);
        var me = await client.GetAsync("/api/v1/me", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        await HttpAssert.ProblemAsync(me, HttpStatusCode.Unauthorized, "auth.invalid_token");
    }
}

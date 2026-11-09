using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using BayiOS.Api.Platform.RateLimiting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// Verification tests of the security audit 2026-11 fixes (#160, docs/security/audit-2026-11.md):
/// M-3 general rate limit, L-1 response headers, L-2 anonymous sessions, L-4 coded 400 for malformed values.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuditFixesTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> LimitedHost(int reads, int writes, string? environment = null) =>
        fixture.Derive(builder =>
        {
            builder.UseSetting("RateLimiting:ReadsPerMinute", reads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("RateLimiting:WritesPerMinute", writes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }
        });

    private static CatalogClient On(WebApplicationFactory<Program> host, CatalogClient user)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = user.Http.DefaultRequestHeaders.Authorization;
        return new CatalogClient(http, user.TenantId, user.UserId);
    }

    private static Task<HttpResponseMessage> WriteAsync(CatalogClient client) =>
        client.PostAsync("/api/v1/brands", new { name = "Hız " + Guid.NewGuid().ToString("N")[..10] });

    private static async Task AssertLimitedAsync(HttpResponseMessage response)
    {
        var problem = await HttpAssert.ProblemJsonAsync(response, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
        Assert.True(problem.TryGetProperty("correlationId", out _));
        var retryAfter = response.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter!.Value.TotalSeconds, 1, 60);
    }

    /// <summary>M-3 / threat-model §6.5: the (N+1)th write in a minute is 429 + Retry-After; reads, another user and another tenant are not affected.</summary>
    [Fact]
    public async Task Writes_over_the_per_minute_limit_get_429_with_retry_after_per_user_and_tenant()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        var colleague = await owner.AddMemberAsync(fixture, "admin");
        await using var host = LimitedHost(reads: 100, writes: 5);
        var limited = On(host, owner);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await WriteAsync(limited)).StatusCode);
        }

        await AssertLimitedAsync(await WriteAsync(limited));
        // Nothing was written by the rejected call.
        var brands = JsonNode.Parse(await (await limited.GetAsync("/api/v1/brands")).Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(5, brands["items"]!.AsArray().Count(b => ((string?)b?["name"])?.StartsWith("Hız ", StringComparison.Ordinal) == true));

        // Separate buckets: reads of the same caller, another user of the same tenant, another tenant's user.
        Assert.Equal(HttpStatusCode.OK, (await limited.GetAsync("/api/v1/brands")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(On(host, colleague))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(On(host, other))).StatusCode);

        // A forged tenant header is not a fresh bucket: the membership check answers first.
        await HttpAssert.ProblemAsync(await WriteAsync(limited.Targeting(Guid.NewGuid())), HttpStatusCode.Forbidden, "tenant.access_denied");
        await AssertLimitedAsync(await WriteAsync(limited));
    }

    /// <summary>Rotating X-Tenant-Id does not mint budget: the per-user cap over all tenant headers (factor × limit) still applies.</summary>
    [Fact]
    public async Task Rotating_tenant_headers_hits_the_per_user_cap()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await using var host = fixture.Derive(builder =>
        {
            builder.UseSetting("RateLimiting:ReadsPerMinute", "100");
            builder.UseSetting("RateLimiting:WritesPerMinute", "2");
            builder.UseSetting("RateLimiting:UserAcrossTenantsFactor", "3");
        });
        var limited = On(host, owner);
        for (var i = 0; i < 6; i++)
        {
            // Each forged header is a fresh per-tenant bucket, but counts against the user's cap (2 × 3 = 6 per minute).
            await HttpAssert.ProblemAsync(await WriteAsync(limited.Targeting(Guid.NewGuid())), HttpStatusCode.Forbidden, "tenant.access_denied");
        }

        await AssertLimitedAsync(await WriteAsync(limited.Targeting(Guid.NewGuid())));
        // The cap covers the caller's real tenant too; reads are a separate bucket.
        await AssertLimitedAsync(await WriteAsync(limited));
        Assert.Equal(HttpStatusCode.OK, (await limited.GetAsync("/api/v1/brands")).StatusCode);
    }

    [Fact]
    public async Task Reads_have_their_own_bucket_and_health_is_never_limited()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await using var host = LimitedHost(reads: 4, writes: 100);
        var limited = On(host, owner);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await limited.GetAsync("/api/v1/brands")).StatusCode);
        }

        await AssertLimitedAsync(await limited.GetAsync("/api/v1/brands"));
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(limited)).StatusCode);

        var anonymous = host.CreateClient();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await limited.Http.GetAsync("/health", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task The_default_limits_are_the_threat_model_values_and_production_enforces_them()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        // The code defaults and the shipped appsettings.json are the §6.5 values (the shared test host raises them).
        var defaults = new RateLimitSettings();
        Assert.Equal((true, 300, 60), (defaults.Enabled, defaults.ReadsPerMinute, defaults.WritesPerMinute));
        var shipped = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepoPaths.Root, "services", "api", "src", "BayiOS.Api", "appsettings.json"), Ct))!["RateLimiting"]!;
        Assert.Equal((true, 300, 60), ((bool)shipped["Enabled"]!, (int)shipped["ReadsPerMinute"]!, (int)shipped["WritesPerMinute"]!));

        // Those values on a Production host: 61 concurrent writes of one caller → exactly 60 succeed.
        await using var host = LimitedHost(reads: 300, writes: 60, environment: "Production");
        Assert.True(host.Services.GetRequiredService<IOptions<RateLimitSettings>>().Value.Enabled);

        // 70 back-to-back writes of one caller: the 60-token burst plus one token per elapsed second gets through.
        var limited = On(host, owner);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 70; i++)
        {
            responses.Add(await WriteAsync(limited));
        }

        var elapsed = (int)Math.Ceiling(started.Elapsed.TotalSeconds);
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        var summary = string.Join(",", responses.Select(r => (int)r.StatusCode));
        Assert.True(responses.Take(60).All(r => r.StatusCode == HttpStatusCode.Created), summary);
        Assert.True(rejected.Count >= 70 - 60 - elapsed && rejected.Count > 0, $"{rejected.Count} rejected in {elapsed} s: {summary}");
        Assert.All(responses.Except(rejected), r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        await AssertLimitedAsync(rejected[0]);
    }

    /// <summary>L-1: nosniff on every response; no-store on authenticated ones; HSTS in Production (configurable).</summary>
    [Fact]
    public async Task Responses_carry_nosniff_no_store_and_production_hsts()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var anonymous = fixture.CreateClient();
        foreach (var response in new[]
                 {
                     await anonymous.GetAsync("/health", Ct),
                     await anonymous.GetAsync("/api/v1/brands", Ct), // 401
                     await anonymous.GetAsync("/does-not-exist", Ct), // 404
                     await owner.GetAsync("/api/v1/brands"),
                     await WriteAsync(owner),
                 })
        {
            Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
            Assert.False(response.Headers.Contains("Strict-Transport-Security"), "No HSTS outside Production by default.");
        }

        var authenticated = await owner.GetAsync("/api/v1/brands");
        Assert.True(authenticated.Headers.CacheControl?.NoStore);

        await using (var production = fixture.Derive(builder => builder.UseEnvironment("Production")))
        {
            var response = await production.CreateClient().GetAsync("/health", Ct);
            Assert.Equal("max-age=31536000", string.Join(",", response.Headers.GetValues("Strict-Transport-Security")));
        }

        await using (var off = fixture.Derive(builder =>
                     {
                         builder.UseEnvironment("Production");
                         builder.UseSetting("SecurityHeaders:HstsMaxAgeSeconds", "0");
                     }))
        {
            var response = await off.CreateClient().GetAsync("/health", Ct);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
            Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    /// <summary>L-2: an anonymous Supabase session (<c>is_anonymous: true</c>) is refused on every endpoint, even for a member's user id.</summary>
    [Fact]
    public async Task Anonymous_sessions_are_rejected_everywhere()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var anonymousOwner = fixture.CreateClient();
        anonymousOwner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.CreateSupabase(owner.UserId, email: null, metadataEmailVerified: null, isAnonymous: true));
        var probe = new CatalogClient(anonymousOwner, owner.TenantId, owner.UserId);

        await HttpAssert.ProblemAsync(await probe.GetAsync("/api/v1/brands"), HttpStatusCode.Unauthorized, "auth.invalid_token");
        await HttpAssert.ProblemAsync(await WriteAsync(probe), HttpStatusCode.Unauthorized, "auth.invalid_token");
        await HttpAssert.ProblemAsync(await anonymousOwner.GetAsync("/api/v1/me", Ct), HttpStatusCode.Unauthorized, "auth.invalid_token");
        await HttpAssert.ProblemAsync(
            await anonymousOwner.PostIdempotentAsync("/api/v1/tenants", new { name = "Anonim", slug = "anon-" + Guid.NewGuid().ToString("N")[..10] }),
            HttpStatusCode.Unauthorized, "auth.invalid_token");

        // A malformed flag fails closed; an explicit false (a normal e-mail user) is accepted.
        foreach (var (flag, expected) in new[] { ((object)"yes", HttpStatusCode.Unauthorized), (false, HttpStatusCode.OK) })
        {
            var client = fixture.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(
                owner.UserId, extraClaims: new Dictionary<string, object> { ["is_anonymous"] = flag }));
            Assert.Equal(expected, (await new CatalogClient(client, owner.TenantId, owner.UserId).GetAsync("/api/v1/brands")).StatusCode);
        }
    }

    /// <summary>L-4: a value that does not bind is a coded 400 request.malformed in Development and Production alike (was 500 / uncoded 400).</summary>
    [Fact]
    public async Task Malformed_values_get_a_coded_400_in_every_environment()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await using var production = fixture.Derive(builder => builder.UseEnvironment("Production"));
        foreach (var client in new[] { owner, On(production, owner) })
        {
            // "xyz" (not hex): the correlation id is hex and once contained "abc" by chance.
            foreach (var limit in new[] { "1e3", "xyz", "99999999999" })
            {
                var response = await client.GetAsync($"/api/v1/ai/order-parses?limit={limit}");
                var problem = await HttpAssert.ProblemJsonAsync(response, HttpStatusCode.BadRequest, "request.malformed");
                Assert.DoesNotContain(limit, problem.GetRawText(), StringComparison.Ordinal);
            }

            // An unreadable JSON body: same coded 400.
            await HttpAssert.ProblemAsync(await client.PostAsync("/api/v1/brands", "{not json"), HttpStatusCode.BadRequest, "request.malformed");
            // Other binding failures keep their status (not 500): a wrong Content-Type is 415.
            await HttpAssert.ProblemAsync(
                await client.SendAsync(HttpMethod.Post, "/api/v1/brands", "{\"name\":\"x\"}", contentType: "text/plain"),
                HttpStatusCode.UnsupportedMediaType, "request.unsupported_media_type");
        }
    }
}

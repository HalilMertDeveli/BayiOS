using System.Net;
using System.Net.Http.Json;
using System.Text;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Contracts;
using Npgsql;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>Tenant selection, membership enforcement and permissions at API level (#12).</summary>
public class TenancyApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Me_for_new_user_returns_identity_from_token_and_no_memberships()
    {
        var userId = Guid.NewGuid();
        var me = await fixture.CreateClientFor(userId, "new@example.test").GetFromJsonAsync<MeResponse>("/api/v1/me", Ct);

        Assert.Equal(userId, me!.UserId);
        Assert.Equal("new@example.test", me.Email);
        Assert.Empty(me.Memberships);
    }

    [Fact]
    public async Task Create_tenant_makes_caller_active_owner()
    {
        var userId = Guid.NewGuid();
        var client = fixture.CreateClientFor(userId, "owner@example.test");
        var slug = NewSlug();

        var response = await client.PostIdempotentAsync("/api/v1/tenants", new { name = "Demo Motor", slug, sector = "motorcycle_parts" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/tenant", response.Headers.Location?.OriginalString);
        var tenant = await response.Content.ReadFromJsonAsync<TenantResponse>(Ct);
        Assert.Equal(slug, tenant!.Slug);
        Assert.Equal("owner", tenant.Role);
        Assert.Equal("active", tenant.Status);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", Ct);
        var membership = Assert.Single(me!.Memberships);
        Assert.Equal((tenant.Id, "owner", "active"), (membership.TenantId, membership.Role, membership.Status));
        Assert.Equal("owner@example.test", me.Email);
    }

    [Fact]
    public async Task Client_supplied_tenant_id_in_body_is_ignored()
    {
        var forcedId = Guid.NewGuid();
        var response = await fixture.CreateClientFor(Guid.NewGuid())
            .PostIdempotentAsync("/api/v1/tenants", new { id = forcedId, tenantId = forcedId, name = "Body Trick", slug = NewSlug() });

        var tenant = await response.Content.ReadFromJsonAsync<TenantResponse>(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(forcedId, tenant!.Id);
    }

    [Fact]
    public async Task Create_tenant_validates_input()
    {
        var response = await fixture.CreateClientFor(Guid.NewGuid())
            .PostIdempotentAsync("/api/v1/tenants", new { name = "x", slug = "Bad Slug!", sector = "space" });

        await HttpAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task Duplicate_slug_returns_409()
    {
        var slug = NewSlug();
        await CreateTenant(fixture.CreateClientFor(Guid.NewGuid()), slug);

        var response = await fixture.CreateClientFor(Guid.NewGuid())
            .PostIdempotentAsync("/api/v1/tenants", new { name = "Second", slug });

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "tenant.slug_taken");
    }

    [Fact]
    public async Task Member_reads_its_tenant()
    {
        var client = fixture.CreateClientFor(Guid.NewGuid());
        var tenant = await CreateTenant(client);

        var response = await Send(client, HttpMethod.Get, "/api/v1/tenant", tenant.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(tenant.Id, (await response.Content.ReadFromJsonAsync<TenantResponse>(Ct))!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public async Task Missing_or_malformed_tenant_header_returns_400(string? header)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant");
        if (header is not null)
        {
            request.Headers.Add("X-Tenant-Id", header);
        }

        var response = await fixture.CreateClientFor(Guid.NewGuid()).SendAsync(request, Ct);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Tenant_A_user_cannot_read_or_write_tenant_B_through_the_api()
    {
        var clientA = fixture.CreateClientFor(Guid.NewGuid());
        var clientB = fixture.CreateClientFor(Guid.NewGuid());
        await CreateTenant(clientA);
        var tenantB = await CreateTenant(clientB);

        var read = await Send(clientA, HttpMethod.Get, "/api/v1/tenant", tenantB.Id);
        var readSettings = await Send(clientA, HttpMethod.Get, "/api/v1/tenant/settings", tenantB.Id);
        var write = await Send(clientA, HttpMethod.Patch, "/api/v1/tenant/settings", tenantB.Id, "{\"require2fa\":true}", "W/\"1\"");

        await HttpAssert.ProblemAsync(read, HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(readSettings, HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(write, HttpStatusCode.Forbidden, "tenant.access_denied");

        var settingsB = await Send(clientB, HttpMethod.Get, "/api/v1/tenant/settings", tenantB.Id);
        Assert.False((await settingsB.Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct))!.Require2fa);
        Assert.Equal("W/\"1\"", settingsB.Headers.ETag?.ToString());

        var meA = await clientA.GetFromJsonAsync<MeResponse>("/api/v1/me", Ct);
        Assert.DoesNotContain(meA!.Memberships, m => m.TenantId == tenantB.Id);
    }

    [Fact]
    public async Task Unknown_tenant_gets_the_same_403_as_a_foreign_one()
    {
        var response = await Send(fixture.CreateClientFor(Guid.NewGuid()), HttpMethod.Get, "/api/v1/tenant", Guid.NewGuid());

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "tenant.access_denied");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("invited")]
    public async Task Inactive_membership_is_denied(string status)
    {
        var tenant = await CreateTenant(fixture.CreateClientFor(Guid.NewGuid()));
        var userId = Guid.NewGuid();
        await AddMembership(tenant.Id, userId, "admin", status);

        var response = await Send(fixture.CreateClientFor(userId), HttpMethod.Get, "/api/v1/tenant", tenant.Id);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "tenant.access_denied");
    }

    [Fact]
    public async Task Viewer_reads_tenant_but_is_forbidden_from_settings()
    {
        var tenant = await CreateTenant(fixture.CreateClientFor(Guid.NewGuid()));
        var viewerId = Guid.NewGuid();
        await AddMembership(tenant.Id, viewerId, "viewer", "active");
        var viewer = fixture.CreateClientFor(viewerId);

        var read = await Send(viewer, HttpMethod.Get, "/api/v1/tenant", tenant.Id);
        var settings = await Send(viewer, HttpMethod.Get, "/api/v1/tenant/settings", tenant.Id);
        var patch = await Send(viewer, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, "{\"require2fa\":true}", "W/\"1\"");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("viewer", (await read.Content.ReadFromJsonAsync<TenantResponse>(Ct))!.Role);
        await HttpAssert.ProblemAsync(settings, HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(patch, HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Fact]
    public async Task Admin_can_manage_settings()
    {
        var tenant = await CreateTenant(fixture.CreateClientFor(Guid.NewGuid()));
        var adminId = Guid.NewGuid();
        await AddMembership(tenant.Id, adminId, "admin", "active");

        var response = await Send(fixture.CreateClientFor(adminId), HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, "{\"showCostToManager\":true}", "W/\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Settings_patch_enforces_if_match_and_persists()
    {
        var client = fixture.CreateClientFor(Guid.NewGuid());
        var tenant = await CreateTenant(client);
        const string body = "{\"negativeStockPolicy\":\"block\",\"salesMaxDiscountPercent\":5}";

        var missing = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, body, ifMatch: null);
        var stale = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, body, "W/\"7\"");
        var invalid = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, "{\"negativeStockPolicy\":\"maybe\"}", "W/\"1\"");
        // OD-09: stock never goes below zero — the former warn / allow values are validation errors.
        var warn = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, "{\"negativeStockPolicy\":\"warn\"}", "W/\"1\"");
        var allow = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, "{\"negativeStockPolicy\":\"allow\"}", "W/\"1\"");
        var ok = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, body, "W/\"1\"");
        var replay = await Send(client, HttpMethod.Patch, "/api/v1/tenant/settings", tenant.Id, body, "W/\"1\"");

        await HttpAssert.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation.failed");
        foreach (var refused in new[] { warn, allow })
        {
            var problem = await HttpAssert.ProblemJsonAsync(refused, HttpStatusCode.UnprocessableEntity, "validation.failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("negativeStockPolicy", out _));
        }

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("W/\"2\"", ok.Headers.ETag?.ToString());
        await HttpAssert.ProblemAsync(replay, HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");

        var saved = await (await Send(client, HttpMethod.Get, "/api/v1/tenant/settings", tenant.Id)).Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct);
        Assert.Equal(("block", 5m), (saved!.NegativeStockPolicy, saved.SalesMaxDiscountPercent));
        // OD-09 (2026-11-09): sales may approve orders by default on a new tenant; owner/admin can turn it off.
        Assert.True(saved.SalesCanApproveOrders);
    }

    private static async Task<TenantResponse> CreateTenant(HttpClient client, string? slug = null)
    {
        var response = await client.PostIdempotentAsync("/api/v1/tenants", new { name = "Tenant " + Guid.NewGuid().ToString("N")[..6], slug = slug ?? NewSlug() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, Guid tenantId, string? json = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/merge-patch+json");
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Seeds a membership directly (no invitation flow yet) as the superuser.</summary>
    private async Task AddMembership(Guid tenantId, Guid userId, string role, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO identity.memberships (id, tenant_id, user_id, role, status, created_at) VALUES ($1, $2, $3, $4, $5, now())",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string NewSlug() => "t-" + Guid.NewGuid().ToString("N")[..12];
}

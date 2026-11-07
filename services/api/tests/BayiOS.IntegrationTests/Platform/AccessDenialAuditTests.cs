using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BayiOS.Api.Platform.Audit;
using BayiOS.IntegrationTests.Billing;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.IntegrationTests.Orders;
using BayiOS.IntegrationTests.Security;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>
/// Access-denial audit events (#89, threat-model §7): every 403 path writes one row on its own transaction (it survives
/// the request's rollback), deduplicated per (user, tenant, code), tenant-isolated by RLS. 401s are never recorded.
/// </summary>
public sealed class AccessDenialAuditTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- TenantContextMiddleware: no active membership ----

    [Fact]
    public async Task A_non_member_is_recorded_once_in_the_named_tenant_however_often_it_tries()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var outsiderId = Guid.NewGuid();
        var outsider = new CatalogClient(fixture.CreateClientFor(outsiderId), owner.TenantId, outsiderId);

        var first = await outsider.GetAsync("/api/v1/products");
        await HttpAssert.ProblemAsync(first, HttpStatusCode.Forbidden, "tenant.access_denied");
        for (var i = 0; i < 5; i++)
        {
            await HttpAssert.ProblemAsync(await outsider.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");
            await HttpAssert.ProblemAsync(await outsider.PostAsync("/api/v1/units", new { code = "x", name = "x" }), HttpStatusCode.Forbidden, "tenant.access_denied");
        }

        var row = Assert.Single(await DenialsAsync(owner.TenantId, outsiderId));
        Assert.Equal(AccessDenialActions.MembershipDenied, row.Action);
        Assert.Equal(AccessDenialAuditLog.EntityType, row.EntityType);
        Assert.Null(row.Role);
        Assert.Equal(Assert.Single(first.Headers.GetValues("X-Correlation-Id")), row.CorrelationId);
        Assert.Null(row.UserAgent); // no free text of the caller in a denial row
        Assert.Equal("tenant.access_denied", row.Changes.GetProperty("code").GetString());
        Assert.Equal("none", row.Changes.GetProperty("membershipStatus").GetString());
        Assert.Equal("GET", row.Changes.GetProperty("method").GetString());
        Assert.Equal("/api/v1/products", row.Changes.GetProperty("route").GetString());
        Assert.False(row.Changes.TryGetProperty("suppressedRepeats", out _));

        // The owner reads it through the audit API like any other event.
        var page = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync($"/api/v1/audit-events?action={AccessDenialActions.MembershipDenied}"));
        Assert.Equal(outsiderId, Assert.Single(page.Items).ActorUserId);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("invited")]
    public async Task A_member_without_an_active_membership_is_recorded_with_its_status(string status)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var userId = Guid.NewGuid();
        await SecurityProbes.AddMembershipAsync(fixture, owner.TenantId, userId, Roles.Manager, status);
        var member = new CatalogClient(fixture.CreateClientFor(userId), owner.TenantId, userId);

        await HttpAssert.ProblemAsync(await member.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");

        var row = Assert.Single(await DenialsAsync(owner.TenantId, userId));
        Assert.Equal((AccessDenialActions.MembershipDenied, status), (row.Action, row.Changes.GetProperty("membershipStatus").GetString()));
    }

    [Fact]
    public async Task An_unknown_tenant_id_writes_nothing()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unknown = Guid.NewGuid();

        await HttpAssert.ProblemAsync(await owner.Targeting(unknown).GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");

        Assert.Equal(0L, await SuperScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1", unknown));
        Assert.Empty(await DenialsAsync(owner.TenantId, owner.UserId));
    }

    [Fact]
    public async Task A_member_of_an_inactive_tenant_is_recorded_with_its_role()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await SuperExecuteAsync("UPDATE identity.tenants SET status = 'suspended' WHERE id = $1", owner.TenantId);

        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.inactive");

        var row = Assert.Single(await DenialsAsync(owner.TenantId, owner.UserId));
        Assert.Equal((AccessDenialActions.MembershipDenied, Roles.Owner), (row.Action, row.Role));
        Assert.Equal(("tenant.inactive", "active"), (row.Changes.GetProperty("code").GetString(), row.Changes.GetProperty("membershipStatus").GetString()));
    }

    // ---- Permission gate and K / O conditions (IAuthorizationContext) ----

    [Fact]
    public async Task A_permission_gate_denial_is_recorded_with_role_and_permission()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var viewer = await owner.AddMemberAsync(fixture, Roles.Viewer);

        for (var i = 0; i < 3; i++)
        {
            await HttpAssert.ProblemAsync(await viewer.GetAsync("/api/v1/audit-events"), HttpStatusCode.Forbidden, "auth.forbidden");
        }

        var row = Assert.Single(await DenialsAsync(owner.TenantId, viewer.UserId));
        Assert.Equal((AccessDenialActions.AccessDenied, Roles.Viewer), (row.Action, row.Role));
        Assert.Equal("auth.forbidden", row.Changes.GetProperty("code").GetString());
        Assert.Equal(new[] { Permissions.AuditRead }, row.Changes.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal("/api/v1/audit-events", row.Changes.GetProperty("route").GetString());
    }

    [Fact]
    public async Task A_record_level_refusal_of_a_service_is_recorded_and_its_write_rolled_back()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var others = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));

        await HttpAssert.ProblemAsync(
            await sales.PatchAsync($"/api/v1/orders/{others.Order.Id}", new { notes = "başkası" }, CatalogClient.ETag(others.Order.Version)),
            HttpStatusCode.Forbidden,
            "orders.not_owner");

        Assert.Equal("Test siparişi", (await kit.GetAsync(others.Order.Id)).Order.Notes);
        var row = Assert.Single(await DenialsAsync(kit.Owner.TenantId, sales.UserId));
        Assert.Equal((AccessDenialActions.AccessDenied, Roles.Sales), (row.Action, row.Role));
        Assert.Equal("orders.not_owner", row.Changes.GetProperty("code").GetString());
        Assert.Equal(new[] { Permissions.OrdersEditDraft }, row.Changes.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal(("PATCH", "/api/v1/orders/{id:guid}"), (row.Changes.GetProperty("method").GetString(), row.Changes.GetProperty("route").GetString()));
    }

    // ---- Entitlements (RequireEntitlement gate and service limits) ----

    [Fact]
    public async Task Entitlement_denials_of_the_gate_and_of_a_limit_are_recorded()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var shelf = await kit.WarehouseAsync("RAF", usesLocations: true);
        await BillingKit.SetPlanAsync(fixture, kit.TenantId, BillingKit.Basic);

        await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync($"/api/v1/warehouses/{shelf.Id}/locations", new { code = "A-02" }), HttpStatusCode.Forbidden, "entitlement.required");
        await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync("/api/v1/warehouses", new { code = "IZM", name = "İzmir" }), HttpStatusCode.Forbidden, "entitlement.limit_exceeded");

        var rows = await DenialsAsync(kit.TenantId, kit.Owner.UserId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((AccessDenialActions.EntitlementDenied, Roles.Owner), (r.Action, r.Role)));
        var gate = Assert.Single(rows, r => r.Changes.GetProperty("code").GetString() == "entitlement.required");
        Assert.Equal("warehouse_locations", gate.Changes.GetProperty("feature").GetString());
        var limit = Assert.Single(rows, r => r.Changes.GetProperty("code").GetString() == "entitlement.limit_exceeded");
        Assert.Equal("max_warehouses", limit.Changes.GetProperty("feature").GetString());
        Assert.Equal(1L, await SuperScalarAsync<long>("SELECT count(*) FROM warehouse.warehouses WHERE tenant_id = $1", kit.TenantId));
    }

    // ---- Separate transaction ----

    [Fact]
    public async Task The_denial_row_survives_the_rollback_of_the_request_transaction()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var requestContext = services.GetRequiredService<RequestContext>();
        (requestContext.UserId, requestContext.TenantId, requestContext.Role, requestContext.CorrelationId) = (owner.UserId, owner.TenantId, Roles.Owner, "rollback-probe");
        var unitOfWork = services.GetRequiredService<UnitOfWork>();
        await unitOfWork.BeginAsync(readOnly: false, Ct);

        // A business event on the request transaction, then a denial, then the request rolls back.
        await services.GetRequiredService<IAuditLog>().RecordAsync(new AuditEvent("tenant.settings_changed", "tenant_settings", owner.TenantId), Ct);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "POST";
        await services.GetRequiredService<IAccessDenialAudit>().RecordAsync(
            http, new AccessDenial(AccessDenialActions.AccessDenied, "auth.forbidden", owner.TenantId, Roles.Owner, ["catalog.write"]));
        await unitOfWork.RollbackAsync(Ct);

        Assert.Equal(0L, await SuperScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND correlation_id = 'rollback-probe' AND action = 'tenant.settings_changed'", owner.TenantId));
        var row = Assert.Single(await DenialsAsync(owner.TenantId, owner.UserId));
        Assert.Equal(("rollback-probe", AccessDenialActions.AccessDenied), (row.CorrelationId, row.Action));
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task A_tenant_cannot_read_another_tenants_denial_rows()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture);
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        // B's owner probes A: the row lands in A's log, not in B's.
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");
        var viewer = await a.AddMemberAsync(fixture, Roles.Viewer);
        await HttpAssert.ProblemAsync(await viewer.GetAsync("/api/v1/audit-events"), HttpStatusCode.Forbidden, "auth.forbidden");
        Assert.Equal(2, (await SuperRowsAsync(a.TenantId)).Count);

        // API: B sees none of them, even filtered by A's actor; A sees both.
        foreach (var query in new[] { "action=auth.membership_denied", "action=auth.access_denied", $"actorUserId={b.UserId}&action=auth.membership_denied", $"actorUserId={viewer.UserId}" })
        {
            var page = await b.OkAsync<AuditEventPageResponse>(await b.GetAsync($"/api/v1/audit-events?{query}"));
            Assert.Empty(page.Items);
        }

        var own = await a.OkAsync<AuditEventPageResponse>(await a.GetAsync("/api/v1/audit-events?limit=200"));
        Assert.Equal(2, own.Items.Count(e => e.Action.StartsWith("auth.", StringComparison.Ordinal)));

        // Database: as bayios_app under B's tenant context, RLS hides A's rows and refuses to insert into A.
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)", connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter { Value = b.TenantId.ToString() });
            context.Parameters.Add(new NpgsqlParameter { Value = b.UserId.ToString() });
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using (var read = new NpgsqlCommand("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1", connection, transaction))
        {
            read.Parameters.Add(new NpgsqlParameter { Value = a.TenantId });
            Assert.Equal(0L, (long)(await read.ExecuteScalarAsync(Ct))!);
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO audit.audit_events (id, tenant_id, action, entity_type, occurred_at) VALUES (gen_random_uuid(), $1, 'auth.access_denied', 'access', now())",
            connection,
            transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = a.TenantId });
        var denied = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState); // RLS WITH CHECK violation
    }

    // ---- Dedupe window and 401 ----

    [Fact]
    public async Task After_the_window_the_next_row_reports_the_suppressed_repeats()
    {
        await using var host = fixture.Derive(builder => builder.UseSetting("Audit:DenialDedupeWindow", "00:00:01"));
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var outsiderId = Guid.NewGuid();
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(outsiderId));
        var outsider = new CatalogClient(http, owner.TenantId, outsiderId);

        for (var i = 0; i < 4; i++)
        {
            await HttpAssert.ProblemAsync(await outsider.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");
        }

        Assert.Single(await DenialsAsync(owner.TenantId, outsiderId));
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
        await HttpAssert.ProblemAsync(await outsider.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");

        var rows = await DenialsAsync(owner.TenantId, outsiderId);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].Changes.TryGetProperty("suppressedRepeats", out _));
        Assert.Equal(3, rows[1].Changes.GetProperty("suppressedRepeats").GetInt32());
    }

    [Fact]
    public async Task Different_codes_users_and_tenants_are_separate_dedupe_keys()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture);
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        var viewer = await a.AddMemberAsync(fixture, Roles.Viewer);
        var otherViewer = await a.AddMemberAsync(fixture, Roles.Viewer);

        // Same user: auth.forbidden in A and tenant.access_denied for B are two keys; a second user is a third.
        await HttpAssert.ProblemAsync(await viewer.GetAsync("/api/v1/audit-events"), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await viewer.Targeting(b.TenantId).GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(await otherViewer.GetAsync("/api/v1/audit-events"), HttpStatusCode.Forbidden, "auth.forbidden");

        Assert.Equal(AccessDenialActions.AccessDenied, Assert.Single(await DenialsAsync(a.TenantId, viewer.UserId)).Action);
        Assert.Equal(AccessDenialActions.MembershipDenied, Assert.Single(await DenialsAsync(b.TenantId, viewer.UserId)).Action);
        Assert.Single(await DenialsAsync(a.TenantId, otherViewer.UserId));
    }

    [Fact]
    public async Task A_401_is_never_recorded()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        using var anonymous = fixture.CreateClient();
        foreach (var token in new string?[] { null, "not-a-jwt", TestTokens.Create(Guid.NewGuid()) + "x" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/products");
            request.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request, Ct)).StatusCode);
        }

        Assert.Empty(await SuperRowsAsync(owner.TenantId));
    }

    // ---- helpers ----

    private sealed record DenialRow(string Action, string EntityType, string? Role, JsonElement Changes, string? CorrelationId, string? UserAgent);

    /// <summary>The <c>auth.*</c> rows of one actor in one tenant, oldest first (superuser: bypasses RLS).</summary>
    private async Task<List<DenialRow>> DenialsAsync(Guid tenantId, Guid actorId) =>
        [.. (await SuperRowsAsync(tenantId)).Where(r => r.Actor == actorId).Select(r => r.Row)];

    private async Task<List<(Guid? Actor, DenialRow Row)>> SuperRowsAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT actor_user_id, action, entity_type, actor_role, changes::text, correlation_id, user_agent
            FROM audit.audit_events WHERE tenant_id = $1 AND action LIKE 'auth.%' ORDER BY occurred_at, id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(Guid?, DenialRow)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                new DenialRow(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    JsonDocument.Parse(reader.GetString(4)).RootElement.Clone(),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6))));
        }

        return rows;
    }

    private async Task<T> SuperScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task SuperExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}

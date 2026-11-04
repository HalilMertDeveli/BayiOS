using System.Net;
using System.Text.Json;
using BayiOS.Api.Platform.Audit;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>Audit events (#79, threat-model §7): atomic with the business change, append-only, tenant-isolated.</summary>
public class AuditTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Settings_change_is_audited_with_a_minimal_diff_and_readable_by_owner()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);

        var patch = await owner.PatchAsync("/api/v1/tenant/settings", new { negativeStockPolicy = "warn", defaultTaxRate = 10m }, "W/\"1\"");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var correlationId = Assert.Single(patch.Headers.GetValues("X-Correlation-Id"));

        var page = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync("/api/v1/audit-events?entityType=tenant_settings"));
        var audit = Assert.Single(page.Items);
        Assert.Equal((AuditActions.TenantSettingsChanged, owner.TenantId, owner.UserId, Roles.Owner, correlationId),
            (audit.Action, audit.EntityId, audit.ActorUserId, audit.ActorRole, audit.CorrelationId));
        var changes = ChangesOf(audit);
        Assert.Equal("block", changes.GetProperty("negativeStockPolicy").GetProperty("from").GetString());
        Assert.Equal("warn", changes.GetProperty("negativeStockPolicy").GetProperty("to").GetString());
        Assert.Equal(10m, changes.GetProperty("defaultTaxRate").GetProperty("to").GetDecimal());
        Assert.False(changes.TryGetProperty("require2fa", out _)); // unchanged fields are not recorded

        // The tenant creation itself was audited too.
        var created = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync($"/api/v1/audit-events?action={AuditActions.TenantCreated}"));
        Assert.Equal(owner.TenantId, Assert.Single(created.Items).EntityId);
    }

    [Fact]
    public async Task Business_profile_audit_keeps_personal_data_out()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var patch = await owner.PatchAsync("/api/v1/tenant", new { taxNumber = "12345678901", phone = "+90 555 000 00 00", taxOffice = "Sentetik VD" }, "W/\"1\"");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var page = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync("/api/v1/audit-events?entityType=tenant"));
        var audit = Assert.Single(page.Items, e => e.Action == AuditActions.TenantSettingsChanged);
        var json = ChangesOf(audit).GetRawText();
        Assert.DoesNotContain("12345678901", json, StringComparison.Ordinal);
        Assert.DoesNotContain("555", json, StringComparison.Ordinal);
        Assert.True(ChangesOf(audit).GetProperty("taxNumber").GetProperty("changed").GetBoolean());
        Assert.Equal("Sentetik VD", ChangesOf(audit).GetProperty("taxOffice").GetProperty("to").GetString());
    }

    [Fact]
    public async Task Audit_event_commits_and_rolls_back_with_the_business_change()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("A1");

        for (var commit = 0; commit < 2; commit++)
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var unitOfWork = await kit.BeginAsync(scope.ServiceProvider);
            var result = await scope.ServiceProvider.GetRequiredService<IInventoryService>().AdjustAsync(
                new AdjustStock(variant, warehouse.Id, null, MovementTypes.AdjustmentIn, 5m, "Sayım farkı (test)"), Ct);
            Assert.True(result.Succeeded);
            if (commit == 1)
            {
                await unitOfWork.CommitAsync(Ct);
            }
            else
            {
                await unitOfWork.RollbackAsync(Ct);
            }
        }

        // Only the committed adjustment left a movement, and exactly that one has an audit event.
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1"));
        Assert.Equal(1L, await kit.CountAsync(
            $"""
            SELECT count(*) FROM audit.audit_events a JOIN inventory.inventory_movements m ON m.id = a.entity_id
            WHERE a.tenant_id = $1 AND a.action = '{AuditActions.InventoryAdjustmentPosted}'
            """));
        Assert.Equal(1L, await kit.CountAsync(
            $"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.InventoryAdjustmentPosted}'"));
    }

    [Fact]
    public async Task Failed_command_leaves_no_audit_event()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("A1");

        // block policy, nothing on hand: the adjustment fails and the request rolls back.
        var response = await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentOut, 3m);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action LIKE 'inventory.%'"));
    }

    [Fact]
    public async Task Receipts_adjustments_and_confirmed_negative_stock_are_audited()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("A1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 2m, null));
        await kit.SetNegativeStockPolicyAsync("warn");

        var response = await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentOut, 5m, confirm: true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var page = await kit.Owner.OkAsync<AuditEventPageResponse>(await kit.Owner.GetAsync("/api/v1/audit-events?limit=200"));
        var actions = page.Items.Select(e => e.Action).ToHashSet();
        Assert.Superset(
            new HashSet<string> { AuditActions.InventoryReceiptPosted, AuditActions.InventoryAdjustmentPosted, AuditActions.InventoryNegativeStockConfirmed },
            actions);
        var confirmed = Assert.Single(page.Items, e => e.Action == AuditActions.InventoryNegativeStockConfirmed);
        var shortage = Assert.Single(ChangesOf(confirmed).GetProperty("shortages").EnumerateArray());
        Assert.Equal((5m, 2m), (shortage.GetProperty("requested").GetDecimal(), shortage.GetProperty("available").GetDecimal()));
        var adjustment = Assert.Single(page.Items, e => e.Action == AuditActions.InventoryAdjustmentPosted);
        Assert.Equal(adjustment.EntityId, confirmed.EntityId);
        Assert.DoesNotContain("Sayım", ChangesOf(adjustment).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_table_is_append_only_for_app_and_owner()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);

        await using (var app = await OpenAsync(fixture.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync(Ct))
        {
            await SetTenantAsync(app, transaction, owner.TenantId);
            await AssertRejectedAsync(app, transaction, "UPDATE audit.audit_events SET action = 'tenant.renamed'");
        }

        await using (var app = await OpenAsync(fixture.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync(Ct))
        {
            await SetTenantAsync(app, transaction, owner.TenantId);
            await AssertRejectedAsync(app, transaction, "DELETE FROM audit.audit_events");
        }

        // Even the superuser (and so the owning migrator) is stopped by the trigger.
        foreach (var sql in new[] { "UPDATE audit.audit_events SET action = 'tenant.renamed'", "DELETE FROM audit.audit_events", "TRUNCATE audit.audit_events" })
        {
            await using var admin = await OpenAsync(fixture.SuperuserConnectionString);
            await using var transaction = await admin.BeginTransactionAsync(Ct);
            await AssertRejectedAsync(admin, transaction, sql);
        }

        Assert.True(await CountAsync(owner.TenantId) >= 1);
    }

    [Fact]
    public async Task Audit_events_are_isolated_per_tenant()
    {
        var tenantA = await CatalogClient.NewTenantOwnerAsync(fixture);
        var tenantB = await CatalogClient.NewTenantOwnerAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await tenantA.PatchAsync("/api/v1/tenant/settings", new { require2fa = true }, "W/\"1\"")).StatusCode);

        // API: B sees only its own events; B cannot select A's tenant.
        var pageB = await tenantB.OkAsync<AuditEventPageResponse>(await tenantB.GetAsync("/api/v1/audit-events?limit=200"));
        Assert.NotEmpty(pageB.Items);
        Assert.DoesNotContain(pageB.Items, e => e.EntityId == tenantA.TenantId);
        await HttpAssert.ProblemAsync(await tenantB.Targeting(tenantA.TenantId).GetAsync("/api/v1/audit-events"), HttpStatusCode.Forbidden, "tenant.access_denied");

        // Database: RLS hides A's rows in B's context and rejects forged inserts for A.
        await using var app = await OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync(Ct);
        await SetTenantAsync(app, transaction, tenantB.TenantId);
        await using (var select = new NpgsqlCommand("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1", app, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = tenantA.TenantId });
            Assert.Equal(0L, (long)(await select.ExecuteScalarAsync(Ct))!);
        }

        await using var forge = new NpgsqlCommand(
            "INSERT INTO audit.audit_events (id, tenant_id, action, entity_type, occurred_at) VALUES (gen_random_uuid(), $1, 'tenant.forged', 'tenant', now())",
            app,
            transaction);
        forge.Parameters.Add(new NpgsqlParameter { Value = tenantA.TenantId });
        var error = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Audit_read_needs_permission_and_pages_with_filters()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsync(
                "/api/v1/tenant/settings", new { salesMaxDiscountPercent = 10 + i }, CatalogClient.ETag(i + 1))).StatusCode);
        }

        var first = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync("/api/v1/audit-events?entityType=tenant_settings&limit=2"));
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.Page.HasMore);
        var second = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync(
            $"/api/v1/audit-events?entityType=tenant_settings&limit=2&cursor={first.Page.NextCursor}"));
        var last = Assert.Single(second.Items);
        Assert.False(second.Page.HasMore);
        Assert.Equal(10m, ChangesOf(last).GetProperty("salesMaxDiscountPercent").GetProperty("to").GetDecimal());
        Assert.True(first.Items[0].OccurredAt >= first.Items[1].OccurredAt && first.Items[1].OccurredAt >= last.OccurredAt);

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        Assert.Empty((await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync($"/api/v1/audit-events?from={future}"))).Items);
        Assert.Single((await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync(
            $"/api/v1/audit-events?action={AuditActions.TenantCreated}&actorUserId={owner.UserId}"))).Items);

        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/audit-events?entityId=nope"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/audit-events?cursor=abc"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/audit-events?sort=asc"), HttpStatusCode.BadRequest, "request.malformed");

        // roles-permissions §3.1: audit.read is owner/admin only.
        var admin = await owner.AddMemberAsync(fixture, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit-events")).StatusCode);
        foreach (var role in new[] { Roles.Manager, Roles.Sales, Roles.WarehouseStaff, Roles.Viewer })
        {
            var member = await owner.AddMemberAsync(fixture, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/audit-events")).StatusCode);
        }
    }

    [Fact]
    public async Task Audit_log_requires_a_unit_of_work_and_a_tenant()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        var auditEvent = new AuditEvent(AuditActions.TenantSettingsChanged, "tenant", Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.RecordAsync(auditEvent, Ct));

        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.UserId = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<UnitOfWork>().BeginAsync(readOnly: false, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.RecordAsync(auditEvent, Ct));
    }

    private static JsonElement ChangesOf(AuditEventResponse auditEvent) =>
        JsonSerializer.SerializeToElement(auditEvent.Changes ?? throw new InvalidOperationException("No changes recorded."));

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId.ToString() });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task AssertRejectedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private async Task<long> CountAsync(Guid tenantId)
    {
        await using var connection = await OpenAsync(fixture.SuperuserConnectionString);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}

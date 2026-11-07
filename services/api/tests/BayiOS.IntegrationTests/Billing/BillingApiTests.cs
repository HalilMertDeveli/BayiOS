using System.Net;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using Npgsql;

namespace BayiOS.IntegrationTests.Billing;

/// <summary>
/// Plans, add-ons and entitlements (#51, #52): trial of new tenants, Basic/Professional gates and limits, the AI add-on
/// and its monthly quota, the free module activation (audited, once), trial expiry (time moved in the database) that
/// keeps every record, permission-before-entitlement, tenant isolation and concurrency.
/// </summary>
public sealed class BillingApiTests(ApiFixture fixture)
{
    private const string Message = "2 adet debriyaj balatası\n1 far ampulü H4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_tenant_starts_a_trial_with_every_module_and_me_shows_the_entitlements()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);

        var subscription = await BillingKit.SubscriptionAsync(owner);
        Assert.Equal(("professional", "trialing"), (subscription.Plan, subscription.Status));
        Assert.NotNull(subscription.TrialEndsAt);
        Assert.InRange(subscription.TrialEndsAt!.Value, DateTimeOffset.UtcNow.AddDays(29.9), DateTimeOffset.UtcNow.AddDays(30.1));
        Assert.Contains("warehouse_locations", subscription.Features);
        Assert.Contains("ai_order_parsing", subscription.Features);
        Assert.Contains(subscription.Limits, l => l is { Code: "max_warehouses", Value: null });
        var ai = Assert.Single(subscription.Modules, m => m.Code == BillingKit.AiModule);
        Assert.Equal(("included_in_trial", true, false), (ai.Status, ai.CanActivate, ai.CanDeactivate));
        Assert.Equal("unavailable", Assert.Single(subscription.Modules, m => m.Code == "dealer_portal").Status);
        Assert.Equal(1L, await AuditCountAsync(owner.TenantId, "billing.trial_started"));

        var me = await owner.OkAsync<MeResponse>(await owner.GetAsync("/api/v1/me"));
        var entitlements = me.ActiveTenant!.Entitlements;
        Assert.Equal(("professional", "trialing"), (entitlements.Plan, entitlements.Status));
        Assert.Contains("ai_order_parsing", entitlements.Features);

        // Every role sees the gates in /me (no billing.read needed); billing details need billing.read.
        var staff = await owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var staffMe = await staff.OkAsync<MeResponse>(await staff.GetAsync("/api/v1/me"));
        Assert.Equal("professional", staffMe.ActiveTenant!.Entitlements.Plan);

        var catalog = await owner.OkAsync<BillingCatalogResponse>(await owner.Http.GetAsync("/api/v1/billing/plans", Ct));
        Assert.Equal(["basic", "professional"], catalog.Plans.Select(p => p.Code));
        Assert.Equal((30, "professional", "basic"), (catalog.TrialDays, catalog.TrialPlan, catalog.FallbackPlan));
        Assert.Contains(catalog.Modules, m => m is { Code: BillingKit.AiModule, Available: true });
        Assert.Contains(catalog.Plans.Single(p => p.Code == "basic").Limits, l => l is { Code: "max_warehouses", Value: 1 });
    }

    [Fact]
    public async Task Basic_caps_warehouses_and_refuses_locations_while_reads_and_stock_operations_stay_open()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var owner = kit.Owner;

        // Professional: several warehouses, locations, stock on a location.
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Professional);
        var main = await kit.WarehouseAsync("MAIN");
        var shelf = await kit.WarehouseAsync("RAF", usesLocations: true);
        var location = await kit.LocationAsync(shelf.Id, "A-01");
        var variant = await kit.VariantAsync();

        // Downgrade to Basic: nothing is deleted, configuration of Pro features becomes read-only.
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Basic);
        var tooMany = await HttpAssert.ProblemJsonAsync(
            await owner.PostAsync("/api/v1/warehouses", new { code = "IZM", name = "İzmir" }), HttpStatusCode.Forbidden, "entitlement.limit_exceeded");
        Assert.Equal(("max_warehouses", 1L, 2L, "professional"), (
            tooMany.GetProperty("feature").GetString(), tooMany.GetProperty("limit").GetInt64(), tooMany.GetProperty("current").GetInt64(),
            tooMany.GetProperty("suggestedPlan").GetString()));
        var noLocations = await HttpAssert.ProblemJsonAsync(
            await owner.PostAsync($"/api/v1/warehouses/{shelf.Id}/locations", new { code = "A-02" }), HttpStatusCode.Forbidden, "entitlement.required");
        Assert.Equal(("warehouse_locations", "professional"), (noLocations.GetProperty("feature").GetString(), noLocations.GetProperty("suggestedPlan").GetString()));
        Assert.Equal(JsonValueKind.Null, noLocations.GetProperty("suggestedModule").ValueKind);
        await BillingKit.AssertProblemAsync(
            await owner.PatchAsync($"/api/v1/locations/{location.Id}", new { pickSequence = 3 }, CatalogClient.ETag(1)), "entitlement.required", "warehouse_locations");
        await BillingKit.AssertProblemAsync(
            await owner.PatchAsync($"/api/v1/warehouses/{main.Id}", new { usesLocations = true }, CatalogClient.ETag(1)), "entitlement.required", "warehouse_locations");

        // Reads, stock operations on the located warehouse, renames and deactivation keep working.
        Assert.Equal(2, (await owner.OkAsync<WarehouseListResponse>(await owner.GetAsync("/api/v1/warehouses"))).Items.Count);
        Assert.Single((await owner.OkAsync<LocationListResponse>(await owner.GetAsync($"/api/v1/warehouses/{shelf.Id}/locations"))).Items);
        await kit.ReceiveAsync(shelf.Id, (variant, 5m, location.Id));
        Assert.Equal(5m, (await kit.BalanceAsync(variant, shelf.Id, location.Id)).OnHand);
        var renamed = await owner.OkAsync<WarehouseResponse>(await owner.PatchAsync($"/api/v1/warehouses/{shelf.Id}", new { name = "Raf deposu" }, CatalogClient.ETag(1)));
        var inactive = await owner.OkAsync<WarehouseResponse>(await owner.PatchAsync($"/api/v1/warehouses/{shelf.Id}", new { status = "inactive" }, CatalogClient.ETag(renamed.Version)));

        // Reactivating would make two active warehouses again.
        await BillingKit.AssertProblemAsync(
            await owner.PatchAsync($"/api/v1/warehouses/{shelf.Id}", new { status = "active" }, CatalogClient.ETag(inactive.Version)), "entitlement.limit_exceeded", "max_warehouses");

        var usage = await owner.OkAsync<UsageResponse>(await owner.GetAsync("/api/v1/billing/usage"));
        Assert.Equal(("basic", "active"), (usage.Plan, usage.Status));
        var warehouses = Assert.Single(usage.Items, i => i.Code == "max_warehouses");
        Assert.Equal((1L, 1L, 0L), (warehouses.Limit, warehouses.Current, warehouses.Remaining));
        Assert.Contains(usage.Items, i => i is { Code: "max_users", Limit: 5, Current: 1 });
    }

    [Fact]
    public async Task Missing_permission_answers_auth_forbidden_before_the_entitlement_and_store_needs_billing_permissions()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var owner = kit.Owner;
        var shelf = await kit.WarehouseAsync("RAF", usesLocations: true);
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Basic);

        var viewer = await owner.AddMemberAsync(fixture, Roles.Viewer);
        await HttpAssert.ProblemAsync(await viewer.PostAsync($"/api/v1/warehouses/{shelf.Id}/locations", new { code = "A-01" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await viewer.GetAsync("/api/v1/billing/subscription"), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await viewer.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "auth.forbidden");

        var admin = await owner.AddMemberAsync(fixture, Roles.Admin);
        Assert.Equal("basic", (await BillingKit.SubscriptionAsync(admin)).Plan);
        await HttpAssert.ProblemAsync(
            await admin.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/activate", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await admin.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "entitlement.required");
        Assert.Equal(0L, await BillingKit.ScalarAsync<long>(fixture, "SELECT count(*) FROM billing.tenant_modules WHERE tenant_id = $1", owner.TenantId));
    }

    [Fact]
    public async Task Ai_add_on_gates_parsing_and_its_free_activation_is_audited_and_given_once()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var owner = kit.Owner;
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Professional);

        var denied = await HttpAssert.ProblemJsonAsync(
            await owner.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "entitlement.required");
        Assert.Equal(("ai_order_parsing", BillingKit.AiModule), (denied.GetProperty("feature").GetString(), denied.GetProperty("suggestedModule").GetString()));
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("suggestedPlan").ValueKind);

        var activated = await BillingKit.ActivateAsync(owner);
        Assert.Equal(("trialing", false, true), (activated.Status, activated.CanActivate, activated.CanDeactivate));
        Assert.InRange(activated.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(29.9), DateTimeOffset.UtcNow.AddDays(30.1));
        Assert.Equal(1L, await AuditCountAsync(owner.TenantId, "billing.module_activated"));
        var parse = await ParseOkAsync(owner);
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/activate", new { }), HttpStatusCode.Conflict, "billing.module_already_active");

        // Switched off: new parses are refused, stored parses stay readable; nothing is deleted.
        var off = await owner.OkAsync<TenantModuleResponse>(await owner.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/deactivate", new { }));
        Assert.Equal(("cancelled", true), (off.Status, off.CanActivate));
        Assert.Equal(1L, await AuditCountAsync(owner.TenantId, "billing.module_deactivated"));
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "entitlement.required");
        await owner.OkAsync<OrderParseResponse>(await owner.GetAsync($"/api/v1/ai/order-parses/{parse.Id}"));
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/deactivate", new { }), HttpStatusCode.Conflict, "billing.module_not_active");

        // On again until the original end — not a second free period.
        var again = await BillingKit.ActivateAsync(owner);
        Assert.Equal(("trialing", activated.ExpiresAt), (again.Status, again.ExpiresAt));
        await ParseOkAsync(owner);

        // Once the free period is used up, it cannot be started again (paid activation does not exist yet).
        await BillingKit.ExecuteAsync(
            fixture, "UPDATE billing.tenant_modules SET status = 'expired', expires_at = now() - interval '1 minute' WHERE tenant_id = $1", owner.TenantId);
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "entitlement.required");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/activate", new { }), HttpStatusCode.Conflict, "billing.trial_used");
        var modules = await owner.OkAsync<TenantModuleListResponse>(await owner.GetAsync("/api/v1/billing/modules"));
        Assert.Equal(("expired", false), modules.Items.Where(m => m.Code == BillingKit.AiModule).Select(m => (m.Status, m.CanActivate)).Single());

        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/billing/modules/dealer_portal/activate", new { }), HttpStatusCode.Conflict, "billing.module_unavailable");
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/billing/modules/yok/activate", new { }), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Equal(2L, await AuditCountAsync(owner.TenantId, "billing.module_activated"));
    }

    [Fact]
    public async Task Monthly_ai_quota_answers_limit_exceeded_and_counts_nothing_more()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture); // trial: the AI add-on is included
        await BillingKit.OverrideAsync(fixture, owner.TenantId, "ai_parses_per_month", limit: 1);

        await ParseOkAsync(owner);
        var exceeded = await HttpAssert.ProblemJsonAsync(
            await owner.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "entitlement.limit_exceeded");
        Assert.Equal(("ai_parses_per_month", 1L, 1L), (
            exceeded.GetProperty("feature").GetString(), exceeded.GetProperty("limit").GetInt64(), exceeded.GetProperty("current").GetInt64()));
        Assert.Equal(JsonValueKind.Null, exceeded.GetProperty("suggestedModule").ValueKind); // the add-on is already on
        Assert.Equal(1L, await BillingKit.ScalarAsync<long>(
            fixture, "SELECT coalesce(sum(parse_requests), 0)::bigint FROM integrations.ai_usage WHERE tenant_id = $1", owner.TenantId));

        var usage = await owner.OkAsync<UsageResponse>(await owner.GetAsync("/api/v1/billing/usage"));
        Assert.Contains(usage.Items, i => i is { Code: "ai_parses_per_month", Limit: 1, Current: 1, Remaining: 0, Period: "month" });

        // Unlimited override lifts it.
        await BillingKit.OverrideAsync(fixture, owner.TenantId, "ai_parses_per_month", limit: null);
        await ParseOkAsync(owner);
    }

    [Fact]
    public async Task Trial_expiry_keeps_every_record_and_falls_back_to_basic_with_or_without_the_job()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var owner = kit.Owner;
        await BillingKit.ExecuteAsync(
            fixture,
            "UPDATE billing.tenant_subscriptions SET plan_code = 'professional', status = 'trialing', trial_ends_at = now() + interval '30 days' WHERE tenant_id = $1",
            owner.TenantId);
        var main = await kit.WarehouseAsync("MAIN");
        var shelf = await kit.WarehouseAsync("RAF", usesLocations: true);
        var location = await kit.LocationAsync(shelf.Id, "A-01");
        var variant = await kit.VariantAsync();
        await kit.ReceiveAsync(shelf.Id, (variant, 4m, location.Id));
        var parse = await ParseOkAsync(owner);

        // Activating the add-on during the trial keeps it after the trial: its free period starts when the trial ends.
        var trialEnd = (await BillingKit.SubscriptionAsync(owner)).TrialEndsAt!.Value;
        var module = await BillingKit.ActivateAsync(owner);
        Assert.InRange(module.ExpiresAt!.Value, trialEnd.AddDays(30).AddMinutes(-1), trialEnd.AddDays(30).AddMinutes(1));
        await BillingKit.ExecuteAsync(fixture, "UPDATE billing.tenant_modules SET expires_at = now() - interval '1 minute' WHERE tenant_id = $1", owner.TenantId);

        await BillingKit.EndTrialAsync(fixture, owner.TenantId);

        // Effective at once (no job needed): Basic, Pro features read-only, core work continues.
        var expired = await BillingKit.SubscriptionAsync(owner);
        Assert.Equal(("basic", "expired", (DateTimeOffset?)null), (expired.Plan, expired.Status, expired.TrialEndsAt));
        Assert.DoesNotContain("ai_order_parsing", expired.Features);
        Assert.Equal("basic", (await owner.OkAsync<MeResponse>(await owner.GetAsync("/api/v1/me"))).ActiveTenant!.Entitlements.Plan);
        await BillingKit.AssertProblemAsync(await owner.PostAsync("/api/v1/warehouses", new { code = "IZM", name = "İzmir" }), "entitlement.limit_exceeded", "max_warehouses");
        await BillingKit.AssertProblemAsync(await owner.PostAsync($"/api/v1/warehouses/{shelf.Id}/locations", new { code = "A-02" }), "entitlement.required", "warehouse_locations");
        await BillingKit.AssertProblemAsync(await owner.PostAsync("/api/v1/ai/order-parses", new { text = Message }), "entitlement.required", "ai_order_parsing");

        Assert.Equal(new[] { main.Id, shelf.Id }.Order(), (await owner.OkAsync<WarehouseListResponse>(await owner.GetAsync("/api/v1/warehouses"))).Items.Select(w => w.Id).Order());
        Assert.Single((await owner.OkAsync<LocationListResponse>(await owner.GetAsync($"/api/v1/warehouses/{shelf.Id}/locations"))).Items);
        await owner.OkAsync<OrderParseResponse>(await owner.GetAsync($"/api/v1/ai/order-parses/{parse.Id}"));
        await kit.ReceiveAsync(shelf.Id, (variant, 1m, location.Id));
        Assert.Equal(5m, (await kit.BalanceAsync(variant, shelf.Id, location.Id)).OnHand);

        // The job records the change (audited, idempotent) and deletes nothing.
        var before = await RowCountsAsync(owner.TenantId);
        await BillingKit.RunExpiryJobAsync(fixture);
        await BillingKit.RunExpiryJobAsync(fixture);
        Assert.Equal(
            "basic/expired",
            await BillingKit.ScalarAsync<string>(fixture, "SELECT plan_code || '/' || status FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId));
        Assert.Equal("expired", await BillingKit.ScalarAsync<string>(fixture, "SELECT status FROM billing.tenant_modules WHERE tenant_id = $1", owner.TenantId));
        Assert.Equal(1L, await AuditCountAsync(owner.TenantId, "billing.trial_expired"));
        Assert.Equal(1L, await AuditCountAsync(owner.TenantId, "billing.module_trial_expired"));
        Assert.Equal(before, await RowCountsAsync(owner.TenantId));
        Assert.Equal(("basic", "expired"), ((await BillingKit.SubscriptionAsync(owner)).Plan, (await BillingKit.SubscriptionAsync(owner)).Status));
    }

    [Fact]
    public async Task Tenant_without_a_subscription_gets_its_trial_on_the_first_write_only()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await BillingKit.ExecuteAsync(fixture, "DELETE FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId);

        // Read-only requests resolve the trial without storing it.
        Assert.Equal("trialing", (await BillingKit.SubscriptionAsync(owner)).Status);
        Assert.Equal(0L, await BillingKit.ScalarAsync<long>(fixture, "SELECT count(*) FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId));

        // The first write provisions it (audited), exactly once.
        await owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "MAIN", name = "Ana" });
        await owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "YEDEK", name = "Yedek" });
        Assert.Equal("trialing", await BillingKit.ScalarAsync<string>(
            fixture, "SELECT status FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId));
        Assert.Equal(2L, await AuditCountAsync(owner.TenantId, "billing.trial_started")); // creation + re-provisioning
    }

    [Fact]
    public async Task Store_changes_and_entitlements_never_cross_tenants()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture);
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        await BillingKit.SetPlanAsync(fixture, a.TenantId, BillingKit.Professional);
        await BillingKit.SetPlanAsync(fixture, b.TenantId, BillingKit.Basic);
        await BillingKit.ActivateAsync(a);

        var bSubscription = await BillingKit.SubscriptionAsync(b);
        Assert.Equal("basic", bSubscription.Plan);
        Assert.Equal("available", bSubscription.Modules.Single(m => m.Code == BillingKit.AiModule).Status);
        await HttpAssert.ProblemAsync(await b.PostAsync("/api/v1/ai/order-parses", new { text = Message }), HttpStatusCode.Forbidden, "entitlement.required");

        // B's owner naming A's tenant is not a member there.
        var spoof = b.Targeting(a.TenantId);
        await HttpAssert.ProblemAsync(await spoof.GetAsync("/api/v1/billing/subscription"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(
            await spoof.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/deactivate", new { }), HttpStatusCode.Forbidden, "tenant.access_denied");
        Assert.Equal("trialing", await BillingKit.ScalarAsync<string>(fixture, "SELECT status FROM billing.tenant_modules WHERE tenant_id = $1", a.TenantId));

        // RLS: in B's context the runtime role sees only B's billing rows.
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter { Value = b.TenantId.ToString() });
            await context.ExecuteNonQueryAsync(Ct);
        }

        foreach (var table in new[] { "tenant_subscriptions", "tenant_modules", "tenant_entitlements" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM billing.{table} WHERE tenant_id <> $1", connection, transaction);
            count.Parameters.Add(new NpgsqlParameter { Value = b.TenantId });
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
        }

        await using var update = new NpgsqlCommand("UPDATE billing.tenant_modules SET status = 'cancelled' WHERE tenant_id = $1", connection, transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = a.TenantId });
        Assert.Equal(0, await update.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_creates_and_activations_cannot_overshoot_the_limits()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Basic);

        var creates = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => owner.PostAsync("/api/v1/warehouses", new { code = $"D{i}", name = $"Depo {i}" })));
        Assert.Single(creates, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in creates.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await HttpAssert.ProblemAsync(refused, HttpStatusCode.Forbidden, "entitlement.limit_exceeded");
        }

        Assert.Equal(1L, await BillingKit.ScalarAsync<long>(fixture, "SELECT count(*) FROM warehouse.warehouses WHERE tenant_id = $1", owner.TenantId));

        var activations = await Task.WhenAll(Enumerable.Range(1, 3).Select(_ =>
            owner.PostAsync($"/api/v1/billing/modules/{BillingKit.AiModule}/activate", new { })));
        Assert.Single(activations, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var refused in activations.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "billing.module_already_active");
        }

        Assert.Equal(1L, await AuditCountAsync(owner.TenantId, "billing.module_activated"));
    }

    [Fact]
    public async Task OpenApi_declares_entitlements_and_limits()
    {
        using var document = JsonDocument.Parse(await fixture.CreateClient().GetStringAsync("/openapi/v1.json", Ct));
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("warehouse_locations", paths.GetProperty("/api/v1/warehouses/{id}/locations").GetProperty("post").GetProperty("x-entitlement").GetString());
        Assert.Equal("warehouse_locations", paths.GetProperty("/api/v1/locations/{id}").GetProperty("patch").GetProperty("x-entitlement").GetString());
        var parse = paths.GetProperty("/api/v1/ai/order-parses").GetProperty("post");
        Assert.Equal("ai_order_parsing", parse.GetProperty("x-entitlement").GetString());
        Assert.Equal("ai_parses_per_month", Assert.Single(parse.GetProperty("x-entitlement-limit").EnumerateArray()).GetString());
        Assert.Equal("max_warehouses", Assert.Single(paths.GetProperty("/api/v1/warehouses").GetProperty("post").GetProperty("x-entitlement-limit").EnumerateArray()).GetString());

        // Reads and exports are never gated.
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Value.TryGetProperty("get", out var get))
            {
                Assert.False(get.TryGetProperty("x-entitlement", out _), $"GET {path.Name} must not be gated");
            }
        }
    }

    /// <summary>Accepted for parsing (202) — with the tenant's AI opt-in switched on first (data-policy §7).</summary>
    private static async Task<OrderParseAcceptedResponse> ParseOkAsync(CatalogClient owner)
    {
        var optIn = await owner.SendAsync(HttpMethod.Put, "/api/v1/ai/settings", new { aiParsingEnabled = true });
        Assert.True(optIn.IsSuccessStatusCode, $"opt-in: {(int)optIn.StatusCode} {await optIn.Content.ReadAsStringAsync(Ct)}");
        var response = await owner.PostAsync("/api/v1/ai/order-parses", new { text = Message });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"parse: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return JsonSerializer.Deserialize<OrderParseAcceptedResponse>(await response.Content.ReadAsStringAsync(Ct), JsonSerializerOptions.Web)!;
    }

    private Task<long> AuditCountAsync(Guid tenantId, string action) =>
        BillingKit.ScalarAsync<long>(fixture, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = $2", tenantId, action);

    /// <summary>Row counts of the tenant's business data that a downgrade must never touch.</summary>
    private Task<string> RowCountsAsync(Guid tenantId) =>
        BillingKit.ScalarAsync<string>(
            fixture,
            """
            SELECT concat_ws('/',
                (SELECT count(*) FROM warehouse.warehouses WHERE tenant_id = $1),
                (SELECT count(*) FROM warehouse.warehouse_locations WHERE tenant_id = $1),
                (SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1),
                (SELECT count(*) FROM integrations.ai_parse_requests WHERE tenant_id = $1),
                (SELECT count(*) FROM catalog.product_variants WHERE tenant_id = $1))
            """,
            tenantId);
}

using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace BayiOS.IntegrationTests.Authorization;

/// <summary>
/// #69: conditional (K) and own-records (O) grants of roles-permissions §3, one table per footnote that has an
/// endpoint, plus the fail-closed safety net and <c>GET /me</c> permissions. Synthetic data only.
/// </summary>
public sealed class ConditionalPermissionsTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- O: orders.edit.draft (sales edits its own drafts) ----

    [Fact]
    public async Task Sales_edits_only_drafts_it_created()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var own = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);
        var others = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));

        var edited = await OrdersKit.ReadAsync(await sales.PatchAsync($"/api/v1/orders/{own.Order.Id}", new { notes = "kendi" }, CatalogClient.ETag(own.Order.Version)));
        Assert.Equal("kendi", edited.Order.Notes);
        var notOwner = await HttpAssert.ProblemJsonAsync(
            await sales.PatchAsync($"/api/v1/orders/{others.Order.Id}", new { notes = "başkası" }, CatalogClient.ETag(others.Order.Version)),
            HttpStatusCode.Forbidden,
            "orders.not_owner");
        // #111: the title the current web matches stays as it was.
        Assert.Equal("orders.edit.draft: Only drafts you created can be edited", notOwner.GetProperty("title").GetString());
        Assert.Equal(Permissions.OrdersEditDraft, notOwner.GetProperty("permission").GetString());
        Assert.Equal("Test siparişi", (await kit.GetAsync(others.Order.Id)).Order.Notes);

        // Unconditional holders edit anyone's draft.
        var manager = await kit.Owner.AddMemberAsync(fixture, Roles.Manager);
        await OrdersKit.ReadAsync(await manager.PatchAsync($"/api/v1/orders/{own.Order.Id}", new { notes = "müdür" }, CatalogClient.ETag(edited.Order.Version)));
    }

    // ---- K¹¹: orders.cancel (sales cancels its own draft / pending orders) ----

    [Fact]
    public async Task Sales_cancels_only_its_own_draft_or_pending_orders()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var reason = new { reason = "Müşteri vazgeçti" };

        var ownDraft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);
        Assert.Equal("cancelled", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, ownDraft, "cancel", reason))).Order.Status);

        var ownPending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(
            sales, await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created), "submit"));
        Assert.Equal("cancelled", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, ownPending, "cancel", reason))).Order.Status);

        var othersDraft = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        var notOwner = await HttpAssert.ProblemJsonAsync(
            await OrdersKit.ActAsync(sales, othersDraft, "cancel", reason), HttpStatusCode.Forbidden, "orders.not_owner");
        Assert.Equal("orders.cancel: Only your own draft or pending-approval orders can be cancelled", notOwner.GetProperty("title").GetString());

        var ownApproved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(
            sales, await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created), "submit"));
        ownApproved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, ownApproved, "approve"));
        var tooLate = await HttpAssert.ProblemJsonAsync(
            await OrdersKit.ActAsync(sales, ownApproved, "cancel", reason), HttpStatusCode.Forbidden, "orders.cancel_not_allowed_in_status");
        Assert.Equal("orders.cancel: Only your own draft or pending-approval orders can be cancelled", tooLate.GetProperty("title").GetString());
        Assert.Equal("approved", tooLate.GetProperty("orderStatus").GetString());
        Assert.Equal("approved", (await kit.GetAsync(ownApproved.Order.Id)).Order.Status);
        Assert.Equal((10m, 1m), await kit.BalanceAsync(variant));
        await kit.AssertConsistentAsync();
    }

    // ---- K⁹: orders.approve (tenant rule "sales may approve", default on) ----

    [Fact]
    public async Task Sales_approval_follows_the_tenant_rule()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);

        var first = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));
        Assert.Equal("approved", (await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, first, "approve"))).Order.Status);

        await SetRulesAsync(kit.Owner, new { salesCanApproveOrders = false });
        var second = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));
        var disabled = await HttpAssert.ProblemJsonAsync(
            await OrdersKit.ActAsync(sales, second, "approve"), HttpStatusCode.Forbidden, "auth.disabled_by_tenant_rule");
        Assert.Equal("Not allowed", disabled.GetProperty("title").GetString());
        Assert.Equal(Permissions.OrdersApprove, disabled.GetProperty("permission").GetString());
        Assert.Equal("tenant_rule.sales_can_approve_orders", disabled.GetProperty("condition").GetString());
        await HttpAssert.ProblemAsync(
            await OrdersKit.ActAsync(sales, second, "reject", new { reason = "x" }), HttpStatusCode.Forbidden, "auth.disabled_by_tenant_rule");
        Assert.Equal("pending_approval", (await kit.GetAsync(second.Order.Id)).Order.Status);

        // A role that never holds the permission still gets the plain auth.forbidden from the gate.
        var viewer = await kit.Owner.AddMemberAsync(fixture, Roles.Viewer);
        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(viewer, second, "approve"), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    // ---- K¹⁰: orders.price.override (sales discount limit) ----

    [Fact]
    public async Task Sales_overrides_only_within_the_discount_limit()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(100m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);

        // Default limit 0 %: no override at all (the K¹⁰ rule is off).
        var off = await HttpAssert.ProblemJsonAsync(
            await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m, discountPercent: 5m)]), HttpStatusCode.Forbidden, "auth.disabled_by_tenant_rule");
        Assert.Equal("Missing permission: orders.price.override", off.GetProperty("title").GetString());
        Assert.Equal("orders.discount_within_limit", off.GetProperty("condition").GetString());

        await SetRulesAsync(kit.Owner, new { salesMaxDiscountPercent = 10m });
        var within = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 2m, discountPercent: 10m)]), HttpStatusCode.Created);
        Assert.Equal(20m, within.Lines[0].LineDiscount);
        await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 2m, discountAmount: 15m)]), HttpStatusCode.Created);
        var exceeded = await HttpAssert.ProblemJsonAsync(
            await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m, discountPercent: 10.5m)]), HttpStatusCode.Forbidden, "orders.discount_limit_exceeded");
        Assert.Equal(10m, exceeded.GetProperty("limitPercent").GetDecimal());
        Assert.Equal("orders.price.override: The discount exceeds the sales limit of 10.00 %", exceeded.GetProperty("title").GetString());
        await HttpAssert.ProblemAsync(
            await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m, discountAmount: 11m)]), HttpStatusCode.Forbidden, "orders.discount_limit_exceeded");
        var manual = await HttpAssert.ProblemJsonAsync(
            await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m, unitPrice: 99m)]), HttpStatusCode.Forbidden, "orders.manual_price_not_allowed");
        Assert.Equal(10m, manual.GetProperty("limitPercent").GetDecimal());
        Assert.Equal(
            "orders.price.override: A manual unit price is not allowed; only a discount within the sales limit", manual.GetProperty("title").GetString());

        // Unconditional holders are not limited.
        await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 1m, unitPrice: 50m, discountPercent: 40m)]), HttpStatusCode.Created);
    }

    // ---- K⁸: orders.read (warehouse_staff: orders behind its tasks, without prices) ----

    [Fact]
    public async Task Warehouse_staff_reads_only_task_linked_orders_without_prices()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var colleague = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);

        var draft = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        var pooled = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var theirs = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var theirTask = await Fulfillment.ActiveTaskAsync(kit.Owner, theirs.Order.Id);
        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ClaimAsync(colleague, theirTask.Task.Id)).StatusCode);

        var list = await OrdersKit.ReadListAsync(await staff.GetAsync("/api/v1/orders"));
        var item = Assert.Single(list.Items);
        Assert.Equal(pooled.Order.Id, item.Id);
        Assert.Null(item.GrandTotal);

        var detail = await OrdersKit.ReadAsync(await staff.GetAsync($"/api/v1/orders/{pooled.Order.Id}"));
        Assert.Null(detail.Order.Totals);
        Assert.All(detail.Lines, l => Assert.True(l is { UnitPrice: null, LineTotal: null, LineSubtotal: null, DiscountPercent: null, PriceListId: null }));
        Assert.Equal(pooled.Lines[0].Quantity, detail.Lines[0].Quantity);

        await HttpAssert.ProblemAsync(await staff.GetAsync($"/api/v1/orders/{theirs.Order.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await staff.GetAsync($"/api/v1/orders/{draft.Order.Id}"), HttpStatusCode.NotFound, "resource.not_found");

        // The colleague sees its claimed order; the owner sees everything with prices.
        Assert.Contains((await OrdersKit.ReadListAsync(await colleague.GetAsync("/api/v1/orders"))).Items, o => o.Id == theirs.Order.Id);
        Assert.NotNull((await kit.GetAsync(pooled.Order.Id)).Order.Totals);
        Assert.Equal(3, (await OrdersKit.ReadListAsync(await kit.Owner.GetAsync("/api/v1/orders"))).Items.Count);
    }

    // ---- O⁵: inventory.movements.read (warehouse_staff: own movements + task context) ----

    [Fact]
    public async Task Warehouse_staff_sees_its_own_movements_and_its_task_products()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var onTask = await kit.VariantAsync(10m);
        var unrelated = await kit.VariantAsync(10m);
        var ownReceipt = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(onTask, 5m);
        await kit.ReceiveAsync(unrelated, 5m);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsync("/api/v1/inventory/receipts", new { warehouseId = kit.WarehouseId, lines = new[] { new { variantId = ownReceipt, quantity = 2m } } })).StatusCode);

        var before = await ReadMovementsAsync(staff, "/api/v1/inventory/movements");
        Assert.Equal([ownReceipt], before.Items.Select(m => m.VariantId).Distinct());

        await kit.ApprovedOrderAsync(OrdersKit.Line(onTask, 1m)); // pool task with the onTask variant
        var after = await ReadMovementsAsync(staff, "/api/v1/inventory/movements");
        Assert.Equal(new[] { onTask, ownReceipt }.Order(), after.Items.Select(m => m.VariantId).Distinct().Order());

        var hidden = (await ReadMovementsAsync(kit.Owner, $"/api/v1/inventory/movements?variantId={unrelated}")).Items[0];
        await HttpAssert.ProblemAsync(await staff.GetAsync($"/api/v1/inventory/movements/{hidden.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Empty((await ReadMovementsAsync(staff, $"/api/v1/inventory/movements?variantId={unrelated}")).Items);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/inventory/movements/{after.Items[0].Id}")).StatusCode);
    }

    // ---- K¹² + any-of: tasks.read.all / tasks.read.own ----

    [Fact]
    public async Task Task_reads_follow_all_own_and_sales_own_order_rules()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var viewer = await kit.Owner.AddMemberAsync(fixture, Roles.Viewer);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);

        var salesOrder = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(
            sales, await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created), "submit"));
        salesOrder = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, salesOrder, "approve"));
        var ownerOrder = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var salesTask = await Fulfillment.ActiveTaskAsync(kit.Owner, salesOrder.Order.Id);
        var ownerTask = await Fulfillment.ActiveTaskAsync(kit.Owner, ownerOrder.Order.Id);

        // sales: tasks of its own orders only (list and detail; detail is any-of tasks.read.own | tasks.read.all).
        var salesList = await Fulfillment.ReadAsync<PickingTaskPageResponse>(await sales.GetAsync("/api/v1/picking-tasks"));
        Assert.Equal([salesTask.Task.Id], salesList.Items.Select(t => t.Id));
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync($"/api/v1/picking-tasks/{salesTask.Task.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sales.GetAsync($"/api/v1/picking-tasks/{ownerTask.Task.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/picking-tasks/mine")).StatusCode);

        // viewer (tasks.read.all ✓, no tasks.read.own): every task and shipment through the any-of endpoints.
        Assert.Equal(2, (await Fulfillment.ReadAsync<PickingTaskPageResponse>(await viewer.GetAsync("/api/v1/picking-tasks"))).Items.Count);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/picking-tasks/{ownerTask.Task.Id}")).StatusCode);

        // staff (tasks.read.own only): pool tasks, not the all-tasks list.
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/picking-tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/picking-tasks/{ownerTask.Task.Id}")).StatusCode);

        // Shipments: owner ships the owner order's task; sales sees none of it, viewer sees it.
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, ownerTask, new Dictionary<Guid, decimal> { [ownerOrder.Lines[0].Id] = 1m });
        var shipmentId = completed.ShipmentId!.Value;
        Assert.Empty((await Fulfillment.ReadAsync<ShipmentPageResponse>(await sales.GetAsync("/api/v1/shipments"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await sales.GetAsync($"/api/v1/shipments/{shipmentId}")).StatusCode);
        Assert.Single((await Fulfillment.ReadAsync<ShipmentPageResponse>(await viewer.GetAsync("/api/v1/shipments"))).Items);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/shipments/{shipmentId}")).StatusCode);
    }

    // ---- K³: prices.read.cost (tenant rule per role) ----

    [Theory]
    [InlineData(Roles.Manager, "showCostToManager")]
    [InlineData(Roles.Viewer, "showCostToViewer")]
    public async Task Cost_price_lists_follow_the_visibility_rule(string role, string rule)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var purchase = await owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Alış", kind = "purchase" });
        var member = await owner.AddMemberAsync(fixture, role);

        Assert.DoesNotContain((await member.OkAsync<PriceListListResponse>(await member.GetAsync("/api/v1/price-lists"))).Items, l => l.Id == purchase.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/v1/price-lists/{purchase.Id}")).StatusCode);

        await SetRulesAsync(owner, new Dictionary<string, object> { [rule] = true });
        Assert.Contains((await member.OkAsync<PriceListListResponse>(await member.GetAsync("/api/v1/price-lists"))).Items, l => l.Id == purchase.Id);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/v1/price-lists/{purchase.Id}")).StatusCode);
    }

    // ---- Fail-closed safety net ----

    [Fact]
    public async Task A_record_level_grant_that_no_service_evaluates_is_denied_and_rolled_back()
    {
        // A host whose authorization context "forgets" to record service checks: every record-level gate pass is then
        // unchecked, as if the endpoint's service never applied its condition.
        await using var forgetful = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthorizationContext>();
            services.AddScoped<AuthorizationContext>();
            services.AddScoped<IAuthorizationContext>(sp => new ForgetfulAuthorizationContext(sp.GetRequiredService<AuthorizationContext>()));
        }));

        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        var salesOnMain = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var staffOnMain = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var draft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(salesOnMain, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);

        var sales = Rebind(forgetful, salesOnMain);
        var staff = Rebind(forgetful, staffOnMain);
        var owner = Rebind(forgetful, kit.Owner);

        // Record-level grants (O orders.edit.draft, K⁸ orders.read) → 403, and the write is rolled back.
        await HttpAssert.ProblemAsync(
            await sales.PatchAsync($"/api/v1/orders/{draft.Order.Id}", new { notes = "kaçak" }, CatalogClient.ETag(draft.Order.Version)),
            HttpStatusCode.Forbidden,
            "auth.forbidden");
        Assert.Equal("Test siparişi", (await kit.GetAsync(draft.Order.Id)).Order.Notes);
        await HttpAssert.ProblemAsync(await staff.GetAsync("/api/v1/orders"), HttpStatusCode.Forbidden, "auth.forbidden");

        // Unconditional grants are untouched by the safety net.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync($"/api/v1/orders/{draft.Order.Id}")).StatusCode);

        // The same calls on the normal host pass (the services do evaluate their conditions).
        Assert.Equal(HttpStatusCode.OK, (await staffOnMain.GetAsync("/api/v1/orders")).StatusCode);
    }

    // ---- GET /me effective permissions ----

    [Theory]
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Viewer)]
    public async Task Me_returns_the_effective_permissions_of_the_selected_tenant(string role)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var member = role == Roles.Owner ? owner : await owner.AddMemberAsync(fixture, role);

        var me = await MeAsync(member, member.TenantId);
        Assert.NotNull(me.ActiveTenant);
        Assert.Equal((owner.TenantId, role), (me.ActiveTenant.TenantId, me.ActiveTenant.Role));
        var actual = me.ActiveTenant.Permissions.ToDictionary(p => p.Permission);

        // Expected = the code matrix under the default tenant rules (roles-permissions §4).
        foreach (var permission in RolePermissions.AllPermissions)
        {
            var scope = AuthorizationContext.Evaluate(role, permission, TenantAccessRules.Defaults);
            if (scope == PermissionScope.None)
            {
                Assert.False(actual.ContainsKey(permission), $"{role} must not list {permission}");
                continue;
            }

            var grant = actual[permission];
            var expected = scope == PermissionScope.All ? "all"
                : RolePermissions.GetGrant(role, permission) == PermissionGrant.OwnRecordsOnly ? "own" : "conditional";
            Assert.Equal(expected, grant.Scope);
            Assert.Equal(scope == PermissionScope.All ? null : ConditionalGrants.Find(role, permission)!.Code, grant.Condition);
        }

        // Spot checks against the document.
        switch (role)
        {
            case Roles.Sales:
                Assert.Equal("own", actual[Permissions.OrdersEditDraft].Scope);
                Assert.Equal(("conditional", "orders.own_draft_or_pending"), (actual[Permissions.OrdersCancel].Scope, actual[Permissions.OrdersCancel].Condition));
                Assert.Equal("all", actual[Permissions.OrdersApprove].Scope);
                Assert.False(actual.ContainsKey(Permissions.OrdersPriceOverride));
                Assert.False(actual.ContainsKey(Permissions.PricesReadCost));
                break;
            case Roles.WarehouseStaff:
                Assert.Equal("conditional", actual[Permissions.OrdersRead].Scope);
                Assert.Equal("own", actual[Permissions.InventoryMovementsRead].Scope);
                Assert.False(actual.ContainsKey(Permissions.PricesReadSell));
                break;
            case Roles.Manager or Roles.Viewer:
                Assert.False(actual.ContainsKey(Permissions.PricesReadCost));
                break;
            default:
                Assert.Equal("all", actual[Permissions.PricesReadCost].Scope);
                break;
        }
    }

    [Fact]
    public async Task Me_permissions_reflect_tenant_rules_and_ignore_foreign_or_missing_tenants()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var manager = await owner.AddMemberAsync(fixture, Roles.Manager);
        var sales = await owner.AddMemberAsync(fixture, Roles.Sales);
        await SetRulesAsync(owner, new { showCostToManager = true, salesMaxDiscountPercent = 5m, salesCanApproveOrders = false });

        Assert.Equal("all", (await MeAsync(manager, owner.TenantId)).ActiveTenant!.Permissions.Single(p => p.Permission == Permissions.PricesReadCost).Scope);
        var salesPermissions = (await MeAsync(sales, owner.TenantId)).ActiveTenant!.Permissions.ToDictionary(p => p.Permission);
        Assert.Equal(("conditional", "orders.discount_within_limit"), (salesPermissions[Permissions.OrdersPriceOverride].Scope, salesPermissions[Permissions.OrdersPriceOverride].Condition));
        // #111: the condition's tenant value, so the UI can say "en fazla %5".
        Assert.Equal(5m, salesPermissions[Permissions.OrdersPriceOverride].Parameters!.MaxDiscountPercent);
        Assert.All(salesPermissions.Values.Where(p => p.Permission != Permissions.OrdersPriceOverride), p => Assert.Null(p.Parameters));
        Assert.False(salesPermissions.ContainsKey(Permissions.OrdersApprove));

        await SetRulesAsync(owner, new { salesMaxDiscountPercent = 12.5m });
        Assert.Equal(
            12.5m,
            (await MeAsync(sales, owner.TenantId)).ActiveTenant!.Permissions.Single(p => p.Permission == Permissions.OrdersPriceOverride).Parameters!.MaxDiscountPercent);

        var stranger = await CatalogClient.NewTenantOwnerAsync(fixture);
        Assert.Null((await MeAsync(stranger, owner.TenantId)).ActiveTenant);
        Assert.Null((await MeAsync(manager, null)).ActiveTenant);
        Assert.NotEmpty((await MeAsync(manager, null)).Memberships);
    }

    // ---- Tenant status (only active tenants are usable) ----

    [Theory]
    [InlineData("suspended")]
    [InlineData("closed")]
    public async Task Inactive_tenants_are_refused_with_tenant_inactive(string status)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var viewer = await owner.AddMemberAsync(fixture, Roles.Viewer);
        var stranger = await CatalogClient.NewTenantOwnerAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/tenant")).StatusCode);

        await SetTenantStatusAsync(owner.TenantId, status);

        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/tenant"), HttpStatusCode.Forbidden, "tenant.inactive");
        await HttpAssert.ProblemAsync(await viewer.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "tenant.inactive");
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/units", new { code = "kutu", name = "Kutu" }), HttpStatusCode.Forbidden, "tenant.inactive");
        // A non-member learns nothing about the status.
        await HttpAssert.ProblemAsync(await stranger.Targeting(owner.TenantId).GetAsync("/api/v1/tenant"), HttpStatusCode.Forbidden, "tenant.access_denied");
        // /me still answers; the inactive tenant is listed (with its status, #111) but not selected.
        var me = await MeAsync(owner, owner.TenantId);
        Assert.Null(me.ActiveTenant);
        Assert.Equal(status, Assert.Single(me.Memberships, m => m.TenantId == owner.TenantId).TenantStatus);

        await SetTenantStatusAsync(owner.TenantId, "active");
        Assert.Equal("active", Assert.Single((await MeAsync(owner, null)).Memberships, m => m.TenantId == owner.TenantId).TenantStatus);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/tenant")).StatusCode);
    }

    // ---- Helpers ----

    private static async Task SetRulesAsync(CatalogClient owner, object patch)
    {
        var current = await owner.GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var response = await owner.PatchAsync("/api/v1/tenant/settings", patch, current.Headers.ETag!.ToString());
        Assert.True(response.IsSuccessStatusCode, $"settings: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<MovementPageResponse> ReadMovementsAsync(CatalogClient client, string path) =>
        await client.OkAsync<MovementPageResponse>(await client.GetAsync(path));

    private static async Task<MeResponse> MeAsync(CatalogClient client, Guid? tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        if (tenantId is { } id)
        {
            request.Headers.Add("X-Tenant-Id", id.ToString());
        }

        var response = await client.Http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeResponse>(Ct))!;
    }

    private async Task SetTenantStatusAsync(Guid tenantId, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE identity.tenants SET status = $1 WHERE id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    private static CatalogClient Rebind(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, CatalogClient client)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.Create(client.UserId));
        return new CatalogClient(http, client.TenantId, client.UserId);
    }

    /// <summary>Delegates everything but never records a service-side check (simulates a forgotten condition).</summary>
    private sealed class ForgetfulAuthorizationContext(AuthorizationContext inner) : IAuthorizationContext
    {
        public IReadOnlyList<string> UncheckedRestrictedGrants => inner.UncheckedRestrictedGrants;

        public async ValueTask<PermissionScope> ScopeAsync(string permission, CancellationToken cancellationToken) =>
            AuthorizationContext.Evaluate(Role, permission, await inner.RulesAsync(cancellationToken));

        public async ValueTask<bool> CanAsync(string permission, bool recordCondition, CancellationToken cancellationToken) =>
            await ScopeAsync(permission, cancellationToken) switch
            {
                PermissionScope.All => true,
                PermissionScope.Restricted => recordCondition,
                _ => false,
            };

        public Task<TenantAccessRules> RulesAsync(CancellationToken cancellationToken) => inner.RulesAsync(cancellationToken);

        public ValueTask<IReadOnlyList<PermissionAccess>> EffectivePermissionsAsync(CancellationToken cancellationToken) =>
            inner.EffectivePermissionsAsync(cancellationToken);

        public ValueTask<PermissionScope> GateAsync(IReadOnlyList<string> anyOf, CancellationToken cancellationToken) =>
            inner.GateAsync(anyOf, cancellationToken);

        private string? Role => inner.Role;
    }
}

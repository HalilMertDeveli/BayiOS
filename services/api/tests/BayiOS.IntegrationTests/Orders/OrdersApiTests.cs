using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Orders;

/// <summary>
/// Orders over HTTP (issues #32, #33, #35): server-side pricing and totals, the state machine, all-or-nothing approval,
/// cancel / partial shipment / short-close against the stock ledger, idempotency, permissions and tenant isolation.
/// </summary>
public class OrdersApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Pricing and totals (#32) ----

    [Fact]
    public async Task Draft_resolves_list_prices_and_computes_totals_server_side()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var led = await kit.VariantAsync(12.345m);
        var bulb = await kit.VariantAsync(0.125m);

        var order = await kit.CreateOrderAsync(
            OrdersKit.Line(led, 3m, discountPercent: 10m),
            OrdersKit.Line(bulb, 1m, discountAmount: 0.03m));

        Assert.Matches(@"^SIP-\d{4}-000001$", order.Order.Number);
        Assert.Equal("draft", order.Order.Status);
        Assert.Equal("none", order.Order.FulfillmentStatus);
        Assert.Equal("manual", order.Order.Source);
        Assert.Equal("TRY", order.Order.Currency);
        var first = order.Lines[0];
        Assert.Equal(("priced", 12.345m, "default_price_list", kit.PriceListId), (first.PriceStatus, first.UnitPrice!.Value, first.PriceSource!, first.PriceListId!.Value));
        Assert.False(first.PriceOverridden);
        Assert.Equal(20m, first.TaxRate);
        // 3 × 12.345 = 37.035 → 37.04 (half away from zero); 10 % = 3.704 → 3.70.
        Assert.Equal((37.04m, 3.70m, 33.34m), (first.LineSubtotal!.Value, first.LineDiscount!.Value, first.LineTotal!.Value));
        Assert.Equal((0.13m, 0.03m, 0.10m), (order.Lines[1].LineSubtotal!.Value, order.Lines[1].LineDiscount!.Value, order.Lines[1].LineTotal!.Value));

        var totals = order.Order.Totals!;
        Assert.False(totals.PricesIncludeTax);
        Assert.False(totals.HasUnpricedLines);
        // VAT per rate on the summed base: 33.44 × 20 % = 6.688 → 6.69.
        Assert.Equal((37.17m, 3.73m, 6.69m, 33.44m, 40.13m), (totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, totals.NetTotal, totals.GrandTotal));
        var bucket = Assert.Single(totals.TaxBreakdown);
        Assert.Equal((20m, 33.44m, 6.69m, 40.13m), (bucket.Rate, bucket.TaxBase, bucket.Tax, bucket.Total));
        Assert.Equal(totals.Subtotal - totals.DiscountTotal + totals.TaxTotal, totals.GrandTotal);
    }

    [Fact]
    public async Task Prices_include_tax_lists_split_vat_out_of_the_total()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var list = await kit.Owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Bayi KDV dahil", pricesIncludeTax = true });
        var customer = await kit.Owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "C0002", name = "Sentetik Bayi", priceListId = list.Id });
        var helmet = await kit.VariantAsync();
        await kit.PriceAsync(list.Id, helmet, 118m);

        var inclusive = await OrdersKit.ReadAsync(
            await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(helmet, 1m)], customer.Customer.Id), HttpStatusCode.Created);

        Assert.True(inclusive.Order.Totals!.PricesIncludeTax);
        Assert.Equal(118m, inclusive.Lines[0].UnitPrice);
        // Base = 118 × 100 / 120 = 98.333… → 98.33; VAT = 118 − 98.33.
        Assert.Equal((118m, 0m, 19.67m, 98.33m, 118m), (inclusive.Order.Totals!.Subtotal, inclusive.Order.Totals!.DiscountTotal,
            inclusive.Order.Totals!.TaxTotal, inclusive.Order.Totals!.NetTotal, inclusive.Order.Totals!.GrandTotal));

        // The same list price on a VAT-excluded order is converted with 4-decimal rounding.
        var exclusive = await OrdersKit.ReadAsync(
            await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(helmet, 1m)], customer.Customer.Id, pricesIncludeTax: false), HttpStatusCode.Created);
        Assert.False(exclusive.Order.Totals!.PricesIncludeTax);
        Assert.Equal(98.3333m, exclusive.Lines[0].UnitPrice);
        Assert.Equal((98.33m, 19.67m, 118m), (exclusive.Order.Totals!.Subtotal, exclusive.Order.Totals!.TaxTotal, exclusive.Order.Totals!.GrandTotal));
    }

    [Fact]
    public async Task No_price_is_never_zero_and_blocks_submit_and_approval_until_priced()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var unpriced = await kit.VariantAsync();
        await kit.ReceiveAsync(unpriced, 5m);

        var order = await kit.CreateOrderAsync(OrdersKit.Line(unpriced, 2m));
        var line = Assert.Single(order.Lines);
        Assert.Equal("no_price", line.PriceStatus);
        Assert.Null(line.UnitPrice);
        Assert.Null(line.LineTotal);
        Assert.True(order.Order.Totals!.HasUnpricedLines);
        Assert.Equal(0m, order.Order.Totals!.GrandTotal);

        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, order, "submit"), HttpStatusCode.UnprocessableEntity, "order.price_missing");

        // A permitted user sets a manual price; the draft can then go through.
        var priced = await OrdersKit.ReadAsync(await kit.Owner.PatchAsync(
            $"/api/v1/orders/{order.Order.Id}",
            new { lines = new[] { OrdersKit.Line(unpriced, 2m, unitPrice: 49.9m, id: line.Id) } },
            CatalogClient.ETag(order.Order.Version)));
        Assert.Equal((49.9m, true, "manual", line.Id), (priced.Lines[0].UnitPrice!.Value, priced.Lines[0].PriceOverridden, priced.Lines[0].PriceSource!, priced.Lines[0].Id));
        Assert.Equal(119.76m, priced.Order.Totals!.GrandTotal);

        var submitted = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, priced, "submit"));
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, submitted, "approve"));
        Assert.Equal("approved", approved.Order.Status);
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.OrderPriceOverridden}'"));
    }

    [Fact]
    public async Task Approval_rechecks_prices_of_a_pending_order()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var order = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));

        // A pending order whose line lost its price (direct DB edit simulating a legacy row) cannot be approved.
        await kit.CountAsync($"WITH u AS (UPDATE orders.order_lines SET unit_price = NULL, line_subtotal = NULL, line_discount = NULL, line_total = NULL WHERE tenant_id = $1 AND order_id = '{order.Order.Id}' RETURNING 1) SELECT count(*) FROM u");

        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, order, "approve"), HttpStatusCode.UnprocessableEntity, "order.price_missing");
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
    }

    // ---- State machine (#32) ----

    [Fact]
    public async Task Illegal_transitions_are_409_and_edits_only_in_draft()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var draft = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));

        foreach (var action in new[] { "approve", "withdraw", "complete" })
        {
            await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, draft, action, action == "approve" ? new { } : null), HttpStatusCode.Conflict, "order.invalid_transition");
        }

        await HttpAssert.ProblemAsync(
            await OrdersKit.ActAsync(kit.Owner, draft, "close-remaining", new { reason = "Kalan iptal" }), HttpStatusCode.Conflict, "order.invalid_transition");

        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        await HttpAssert.ProblemAsync(
            await kit.Owner.PatchAsync($"/api/v1/orders/{approved.Order.Id}", new { notes = "x" }, CatalogClient.ETag(approved.Order.Version)),
            HttpStatusCode.Conflict,
            "order.not_editable");
        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, approved, "submit"), HttpStatusCode.Conflict, "order.invalid_transition");

        var cancelled = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, approved, "cancel", new { reason = "Müşteri vazgeçti" }));
        Assert.Equal("cancelled", cancelled.Order.Status);
        foreach (var action in new[] { "submit", "approve", "complete" })
        {
            await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, cancelled, action), HttpStatusCode.Conflict, "order.invalid_transition");
        }

        await HttpAssert.ProblemAsync(
            await OrdersKit.ActAsync(kit.Owner, cancelled, "cancel", new { reason = "Tekrar" }), HttpStatusCode.Conflict, "order.invalid_transition");
    }

    [Fact]
    public async Task Full_lifecycle_records_the_timeline()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(25m);
        await kit.ReceiveAsync(variant, 10m);
        var draft = await kit.CreateOrderAsync(OrdersKit.Line(variant, 4m));

        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        var backToDraft = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "reject", new { reason = "Fiyatı kontrol et" }));
        Assert.Equal("draft", backToDraft.Order.Status);
        pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, backToDraft, "submit"));
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"));
        // Picking and shipping run through the Warehouse task (first scan → picking, complete → packed, confirm → shipped).
        var shipped = await kit.ShipAsync(approved, (approved.Lines[0].Id, 4m));
        Assert.Equal(("shipped", "full"), (shipped.Order.Status, shipped.Order.FulfillmentStatus));
        Assert.False(shipped.Order.HasShortage);
        Assert.NotNull(shipped.Order.ShippedAt);
        var completed = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, shipped, "complete"));
        Assert.Equal("completed", completed.Order.Status);

        Assert.Equal(
            ["create", "submit", "reject", "submit", "approve", "start_picking", "pack", "ship", "complete"],
            completed.History.Select(h => h.Action));
        Assert.Equal("Fiyatı kontrol et", completed.History[2].Reason);
        Assert.All(completed.History, h => Assert.Equal(kit.Owner.UserId, h.ChangedBy));
        Assert.Equal((6m, 0m), await kit.BalanceAsync(variant));
        await kit.AssertConsistentAsync();
    }

    // ---- Approval and reservation (#33) ----

    [Fact]
    public async Task Approval_reserves_every_line_or_nothing_with_a_clear_shortage()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var plenty = await kit.VariantAsync(10m, "MOTOR-FILTRE");
        var scarce = await kit.VariantAsync(20m, "MOTOR-ZINCIR");
        await kit.ReceiveAsync(plenty, 5m);
        await kit.ReceiveAsync(scarce, 1m);
        var order = await kit.SubmittedOrderAsync(OrdersKit.Line(plenty, 2m), OrdersKit.Line(scarce, 3m));

        var response = await OrdersKit.ActAsync(kit.Owner, order, "approve");

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "inventory.insufficient_stock");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var shortage = Assert.Single(body.RootElement.GetProperty("shortages").EnumerateArray());
        Assert.Equal(scarce, shortage.GetProperty("variantId").GetGuid());
        Assert.Equal("MOTOR-ZINCIR", shortage.GetProperty("sku").GetString());
        Assert.Equal(3m, shortage.GetProperty("requested").GetDecimal());
        Assert.Equal(1m, shortage.GetProperty("available").GetDecimal());

        // Nothing reserved for the line that had stock either (I6); the order stays pending.
        Assert.Equal((5m, 0m), await kit.BalanceAsync(plenty));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(scarce));
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        var after = await kit.GetAsync(order.Order.Id);
        Assert.Equal(("pending_approval", order.Order.Version), (after.Order.Status, after.Order.Version));
        Assert.All(after.Lines, l => Assert.Equal(0m, l.ReservedQuantity));
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'orders.approved.v1'"));

        // With enough stock the same order is approved: every line reserved, available drops by the ordered amount.
        await kit.ReceiveAsync(scarce, 2m);
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, after, "approve"));
        Assert.Equal("approved", approved.Order.Status);
        Assert.Equal(kit.Owner.UserId, approved.Order.ApprovedBy);
        Assert.Equal([2m, 3m], approved.Lines.Select(l => l.ReservedQuantity));
        Assert.Equal((5m, 2m), await kit.BalanceAsync(plenty));
        Assert.Equal((3m, 3m), await kit.BalanceAsync(scarce));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.OrderApproved}' AND entity_id = '{order.Order.Id}'"));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'orders.approved.v1' AND payload->>'orderId' = '{order.Order.Id}'"));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Approval_requires_if_match_and_rejects_a_stale_version()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var order = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));

        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, order, "approve", ifMatch: false), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Post, $"/api/v1/orders/{order.Order.Id}/approve", new { }, CatalogClient.ETag(order.Order.Version - 1)),
            HttpStatusCode.PreconditionFailed,
            "concurrency.version_mismatch");
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
    }

    // ---- Cancel, partial shipment, short-close (#35) ----

    [Fact]
    public async Task Cancel_releases_exactly_the_reserved_amounts()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var a = await kit.VariantAsync(10m);
        var b = await kit.VariantAsync(15m);
        await kit.ReceiveAsync(a, 10m);
        await kit.ReceiveAsync(b, 4m);
        var other = await kit.ApprovedOrderAsync(OrdersKit.Line(a, 1m));
        var order = await kit.ApprovedOrderAsync(OrdersKit.Line(a, 3m), OrdersKit.Line(b, 4m));
        Assert.Equal((10m, 4m), await kit.BalanceAsync(a));
        Assert.Equal((4m, 4m), await kit.BalanceAsync(b));

        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, order, "cancel", new { reason = " " }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var cancelled = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, order, "cancel", new { reason = "Müşteri vazgeçti" }));

        Assert.Equal(("cancelled", "Müşteri vazgeçti"), (cancelled.Order.Status, cancelled.Order.CancelReason!));
        Assert.All(cancelled.Lines, l => Assert.Equal(0m, l.ReservedQuantity));
        // Only this order's reservation went back; the other order keeps its unit.
        Assert.Equal((10m, 1m), await kit.BalanceAsync(a));
        Assert.Equal((4m, 0m), await kit.BalanceAsync(b));
        Assert.Equal(1L, await kit.ActiveReservationsAsync());
        Assert.Equal("approved", (await kit.GetAsync(other.Order.Id)).Order.Status);
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND type = 'shipment_out'"));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.OrderCancelled}'"));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'orders.cancelled.v1'"));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Partial_shipment_then_short_close_keeps_ledger_and_balances_consistent()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var tire = await kit.VariantAsync(100m);
        var lamp = await kit.VariantAsync(30m);
        await kit.ReceiveAsync(tire, 12m);
        await kit.ReceiveAsync(lamp, 5m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(tire, 10m), OrdersKit.Line(lamp, 5m));
        // 4 of 10 tires picked (6 reported short), all lamps: the shipment confirms what was picked.
        var partial = await kit.ShipAsync(approved, (approved.Lines[0].Id, 4m), (approved.Lines[1].Id, 5m));
        Assert.Equal(("packed", "partial"), (partial.Order.Status, partial.Order.FulfillmentStatus));
        Assert.True(partial.Order.HasShortage);
        Assert.Equal((4m, 6m, 6m), (partial.Lines[0].ShippedQuantity, partial.Lines[0].ReservedQuantity, partial.Lines[0].OpenQuantity));
        Assert.Equal((5m, 0m, 0m), (partial.Lines[1].ShippedQuantity, partial.Lines[1].ReservedQuantity, partial.Lines[1].OpenQuantity));
        var shipment = Assert.Single(partial.Shipments);
        Assert.Equal(("Sentetik Kargo", 2), (shipment.Carrier!, shipment.Lines.Count));
        // Shipment lowers on_hand and reserved together: available unchanged (I5).
        Assert.Equal((8m, 6m), await kit.BalanceAsync(tire));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(lamp));
        await kit.AssertConsistentAsync();

        // After a partial shipment, cancel is refused (I9); only short-close.
        await HttpAssert.ProblemAsync(
            await OrdersKit.ActAsync(kit.Owner, partial, "cancel", new { reason = "Vazgeçti" }), HttpStatusCode.Conflict, "order.invalid_transition");

        var closed = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, partial, "close-remaining", new { reason = "Tedarik yok" }));
        Assert.Equal(("shipped", "partial"), (closed.Order.Status, closed.Order.FulfillmentStatus));
        Assert.Equal((4m, 6m, 0m, 0m), (closed.Lines[0].ShippedQuantity, closed.Lines[0].CancelledQuantity, closed.Lines[0].ReservedQuantity, closed.Lines[0].OpenQuantity));
        Assert.Equal((8m, 0m), await kit.BalanceAsync(tire));
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        Assert.Equal(-9L, await kit.CountAsync("SELECT sum(quantity)::bigint FROM inventory.inventory_movements WHERE tenant_id = $1 AND type = 'shipment_out'"));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.OrderRemainingClosed}'"));
        Assert.Equal(2L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'orders.shipped.v1'"));
        await kit.AssertConsistentAsync();

        var completed = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, closed, "complete"));
        Assert.Equal("completed", completed.Order.Status);
    }

    [Fact]
    public async Task Shipping_draws_from_the_reserved_locations()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var warehouse = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "RAF", name = "Raflı depo", usesLocations = true });
        var a1 = await kit.Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouse.Id}/locations", new { code = "A-01", pickSequence = 1 });
        var a2 = await kit.Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouse.Id}/locations", new { code = "A-02", pickSequence = 2 });
        var variant = await kit.VariantAsync(5m);
        await kit.ReceiveAsync(variant, 3m, warehouse.Id, a1.Id);
        await kit.ReceiveAsync(variant, 4m, warehouse.Id, a2.Id);
        var draft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 6m)], warehouseId: warehouse.Id), HttpStatusCode.Created);
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"));

        // The pick task has one line per reserved location (A-01: 3, A-02: 3), picked shelf by shelf.
        var shipped = await kit.ShipAsync(approved, (approved.Lines[0].Id, 6m));

        Assert.Equal(("shipped", "full"), (shipped.Order.Status, shipped.Order.FulfillmentStatus));
        Assert.Equal(2, Assert.Single(shipped.Shipments).Lines.Count);
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, warehouse.Id, a1.Id));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, warehouse.Id, a2.Id));
        await kit.AssertConsistentAsync();
    }

    // ---- Idempotency (#32, #35) ----

    [Fact]
    public async Task Create_and_approve_replays_with_the_same_key_do_not_duplicate()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var createKey = Guid.NewGuid().ToString();

        var first = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 2m)], key: createKey), HttpStatusCode.Created);
        var replay = await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 2m)], key: createKey);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(first.Order.Id, (await replay.Content.ReadFromJsonAsync<OrderDetailResponse>(Ct))!.Order.Id);
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM orders.orders WHERE tenant_id = $1"));

        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, first, "submit"));
        var approveKey = Guid.NewGuid().ToString();
        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve", key: approveKey));
        var again = await OrdersKit.ActAsync(kit.Owner, pending, "approve", key: approveKey);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(again.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(approved.Order.Version, (await again.Content.ReadFromJsonAsync<OrderDetailResponse>(Ct))!.Order.Version);
        Assert.Equal((5m, 2m), await kit.BalanceAsync(variant));
        Assert.Equal(1L, await kit.ActiveReservationsAsync());

        // A different key is a new request: the transition is no longer legal and nothing is reserved twice (I11).
        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(kit.Owner, approved, "approve"), HttpStatusCode.Conflict, "order.invalid_transition");
        Assert.Equal((5m, 2m), await kit.BalanceAsync(variant));
        await kit.AssertConsistentAsync();
    }

    // ---- Permissions (roles-permissions §3.5) ----

    [Fact]
    public async Task Permissions_follow_the_role_matrix()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var viewer = await kit.Owner.AddMemberAsync(fixture, Roles.Viewer);

        // sales: creates with list prices; a manual price or discount needs orders.price.override (K → denied).
        var salesOrder = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)]), HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m, unitPrice: 1m)]), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m, discountPercent: 50m)]), HttpStatusCode.Forbidden, "auth.forbidden");
        // orders.edit.draft is "own records" (O) for sales — fail-closed until #69.
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PatchAsync($"/api/v1/orders/{salesOrder.Order.Id}", new { notes = "x" }, CatalogClient.ETag(1))).StatusCode);
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(sales, salesOrder, "submit"));
        // orders.approve and orders.cancel are conditional (K) for sales — denied at the endpoint.
        Assert.Equal(HttpStatusCode.Forbidden, (await OrdersKit.ActAsync(sales, pending, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await OrdersKit.ActAsync(sales, pending, "cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync($"/api/v1/orders/{pending.Order.Id}")).StatusCode);

        // viewer reads only; warehouse_staff may ship but not read orders (K⁸).
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kit.CreateResponseAsync(viewer, [OrdersKit.Line(variant, 1m)])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await OrdersKit.ActAsync(staff, pending, "approve")).StatusCode);

        var approved = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"));
        // warehouse_staff picks and ships through the Warehouse task (claimed from the pool); sales may not ship.
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ClaimAsync(staff, task.Task.Id)).StatusCode);
        var completed = await Fulfillment.PickAndCompleteAsync(staff, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 1m });
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.ConfirmAsync(sales, completed.ShipmentId!.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ConfirmAsync(staff, completed.ShipmentId!.Value)).StatusCode);
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Dealer_group_discount_prefills_without_override_permission()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var group = await kit.Owner.CreatedAsync<DealerGroupResponse>("/api/v1/dealer-groups", new { name = "A Bayileri", defaultDiscountPercent = 5m });
        var customer = await kit.Owner.CreatedAsync<CustomerDetailResponse>("/api/v1/customers", new { code = "C0009", name = "Grup Bayi", dealerGroupId = group.Id });
        var variant = await kit.VariantAsync(200m);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);

        var order = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(sales, [OrdersKit.Line(variant, 1m)], customer.Customer.Id), HttpStatusCode.Created);

        Assert.Equal((5m, 10m, 190m), (order.Lines[0].DiscountPercent!.Value, order.Lines[0].LineDiscount!.Value, order.Lines[0].LineTotal!.Value));
        Assert.Equal(228m, order.Order.Totals!.GrandTotal);
        Assert.Equal(0L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.OrderPriceOverridden}'"));
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task Another_tenant_cannot_see_or_act_on_an_order()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var order = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));
        var other = await OrdersKit.CreateAsync(fixture);

        await HttpAssert.ProblemAsync(await other.Owner.GetAsync($"/api/v1/orders/{order.Order.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await OrdersKit.ActAsync(other.Owner, order, "approve"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(
            await OrdersKit.ActAsync(other.Owner, order, "cancel", new { reason = "x" }), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Empty((await other.Owner.OkAsync<OrderPageResponse>(await other.Owner.GetAsync("/api/v1/orders"))).Items);
        // Another tenant's customer / variant are not visible to this tenant's order either.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kit.CreateResponseAsync(other.Owner, [OrdersKit.Line(variant, 1m)], other.CustomerId, warehouseId: other.WarehouseId)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await kit.CreateResponseAsync(other.Owner, [OrdersKit.Line(await other.VariantAsync(1m), 1m)], kit.CustomerId, warehouseId: other.WarehouseId)).StatusCode);
        // Sending the victim tenant id without a membership is rejected before any handler runs.
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Owner.Targeting(kit.TenantId).GetAsync($"/api/v1/orders/{order.Order.Id}")).StatusCode);

        Assert.Equal("pending_approval", (await kit.GetAsync(order.Order.Id)).Order.Status);
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
    }

    // ---- Read API ----

    [Fact]
    public async Task List_pages_newest_first_and_filters()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var first = await kit.CreateOrderAsync(OrdersKit.Line(variant, 1m));
        var second = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m));
        var third = await kit.CreateOrderAsync(OrdersKit.Line(variant, 2m));
        await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, [OrdersKit.Line(variant, 1m)], source: "whatsapp_ai"), HttpStatusCode.Created);

        var page1 = await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync("/api/v1/orders?limit=2"));
        Assert.True(page1.Page.HasMore);
        var page2 = await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync($"/api/v1/orders?limit=2&cursor={page1.Page.NextCursor}"));
        Assert.False(page2.Page.HasMore);
        var all = page1.Items.Concat(page2.Items).ToList();
        Assert.Equal(4, all.Count);
        Assert.Equal(all.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Select(o => o.Id), all.Select(o => o.Id));
        Assert.Equal((24m, 1), (all.Single(o => o.Id == third.Order.Id).GrandTotal, all.Single(o => o.Id == third.Order.Id).LineCount));

        var pending = await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync("/api/v1/orders?status=pending_approval"));
        Assert.Equal([second.Order.Id], pending.Items.Select(o => o.Id));
        var drafts = await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync($"/api/v1/orders?status=draft&customerId={kit.CustomerId}&source=manual"));
        Assert.Equal([third.Order.Id, first.Order.Id], drafts.Items.Select(o => o.Id));
        var ai = await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync("/api/v1/orders?source=whatsapp_ai"));
        Assert.Equal("whatsapp_ai", Assert.Single(ai.Items).Source);
        var byNumber = await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync($"/api/v1/orders?q={first.Order.Number[^6..]}"));
        Assert.Equal([first.Order.Id], byNumber.Items.Select(o => o.Id));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(4, (await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync($"/api/v1/orders?from={today}&to={today}"))).Items.Count);
        Assert.Empty((await kit.Owner.OkAsync<OrderPageResponse>(await kit.Owner.GetAsync("/api/v1/orders?to=2000-01-01"))).Items);

        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/orders?status=bogus"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/orders?cursor=bad"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/orders?unknown=1"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Draft_edit_replaces_lines_and_requires_if_match()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var a = await kit.VariantAsync(10m);
        var b = await kit.VariantAsync(7.5m);
        var order = await kit.CreateOrderAsync(OrdersKit.Line(a, 1m), OrdersKit.Line(b, 2m));

        await HttpAssert.ProblemAsync(
            await kit.Owner.PatchAsync($"/api/v1/orders/{order.Order.Id}", new { notes = "x" }, null), HttpStatusCode.PreconditionRequired, "precondition.required");

        // Reorder: keep b (now first), drop a, add a new line of a — line numbers are renumbered in one save.
        var edited = await OrdersKit.ReadAsync(await kit.Owner.PatchAsync(
            $"/api/v1/orders/{order.Order.Id}",
            new { notes = "Revize", lines = new[] { OrdersKit.Line(b, 4m, id: order.Lines[1].Id), OrdersKit.Line(a, 3m) } },
            CatalogClient.ETag(order.Order.Version)));

        Assert.Equal(order.Order.Version + 1, edited.Order.Version);
        Assert.Equal("Revize", edited.Order.Notes);
        Assert.Equal([1, 2], edited.Lines.Select(l => l.LineNo));
        Assert.Equal(order.Lines[1].Id, edited.Lines[0].Id);
        Assert.NotEqual(order.Lines[0].Id, edited.Lines[1].Id);
        Assert.Equal(60m, edited.Order.Totals!.Subtotal);
        Assert.Equal(72m, edited.Order.Totals!.GrandTotal);

        await HttpAssert.ProblemAsync(
            await kit.Owner.PatchAsync($"/api/v1/orders/{order.Order.Id}", new { notes = "eski" }, CatalogClient.ETag(order.Order.Version)),
            HttpStatusCode.PreconditionFailed,
            "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(
            await kit.Owner.PatchAsync($"/api/v1/orders/{order.Order.Id}", new { lines = new[] { OrdersKit.Line(a, 1.5m) } }, CatalogClient.ETag(edited.Order.Version)),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
    }
}

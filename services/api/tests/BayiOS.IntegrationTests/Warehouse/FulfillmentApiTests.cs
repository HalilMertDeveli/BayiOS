using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// Warehouse fulfilment over HTTP (issues #36–#39): pick task on approval, assignment and claim, shelf-ordered
/// scanning with mismatch rejections, shortages, the pack check, shipment confirmation issuing stock once, the
/// putaway after a cancel, staff scoping and tenant isolation. Synthetic data only.
/// </summary>
public class FulfillmentApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- #36 / #37 / #38 / #39: the whole flow ----

    [Fact]
    public async Task Scan_driven_pick_pack_and_ship_with_locations_reconciles_ledger_balances_reservations_and_order()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var warehouse = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "RAF", name = "Raflı depo", usesLocations = true });
        var a01 = await kit.Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouse.Id}/locations", new { code = "A-01", barcode = "LOC-A01", pickSequence = 1 });
        var b01 = await kit.Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouse.Id}/locations", new { code = "B-01", barcode = "LOC-B01", pickSequence = 2 });
        var helmet = await kit.VariantAsync(100m, "KASK-01", "8690000000011");
        var lamp = await kit.VariantAsync(30m, "LAMBA-01", "8690000000028");
        var chain = await kit.VariantAsync(5m, "ZINCIR-01", "8690000000035");
        await kit.ReceiveAsync(helmet, 5m, warehouse.Id, b01.Id);
        await kit.ReceiveAsync(lamp, 10m, warehouse.Id, a01.Id);
        await kit.ReceiveAsync(chain, 3m, warehouse.Id, a01.Id);
        var approved = await ApproveAsync(kit, warehouse.Id, OrdersKit.Line(helmet, 2m), OrdersKit.Line(lamp, 3m));

        // #36: approval created the pick task (pool), lines in walking order: A-01 before B-01 regardless of line no.
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        Assert.Equal(("pick", "open", (Guid?)null, approved.Order.Number), (task.Task.Type, task.Task.Status, task.Task.AssignedTo, task.Task.OrderNumber));
        Assert.Equal(["A-01", "B-01"], task.Lines.Select(l => l.LocationCode));
        Assert.Equal(["LAMBA-01", "KASK-01"], task.Lines.Select(l => l.Sku));
        Assert.Equal(("8690000000028", "LOC-A01", 3m), (task.Lines[0].Barcode, task.Lines[0].LocationBarcode, task.Lines[0].QuantityToPick));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.picking_task_created.v1'"));

        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var assigned = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, task.Task.Id, staff.UserId));
        Assert.Equal(staff.UserId, assigned.Task.AssignedTo);
        var mine = await Fulfillment.ReadAsync<MyPickingTasksResponse>(await staff.GetAsync("/api/v1/picking-tasks/mine?scope=assigned"));
        Assert.Equal(task.Task.Id, Assert.Single(mine.Items).Id);

        // #37: mismatches are rejected with stable codes and change nothing.
        var id = task.Task.Id;
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "8690000000035", location: "LOC-A01"), HttpStatusCode.UnprocessableEntity, "picking.wrong_item");
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "0000000000000", location: "LOC-A01"), HttpStatusCode.UnprocessableEntity, "picking.unknown_code");
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "8690000000028"), HttpStatusCode.UnprocessableEntity, "picking.location_required");
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "8690000000028", location: "LOC-B01"), HttpStatusCode.UnprocessableEntity, "picking.wrong_location");
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "8690000000028", location: "LOC-ZZ"), HttpStatusCode.UnprocessableEntity, "picking.unknown_location");
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "8690000000028", 4m, "LOC-A01"), HttpStatusCode.UnprocessableEntity, "picking.over_pick");
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, id, "8690000000028", 0.5m, "LOC-A01"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var untouched = await Fulfillment.TaskAsync(staff, id);
        Assert.Equal(("open", 0m), (untouched.Task.Status, untouched.Task.QuantityPicked));
        Assert.Equal("approved", (await kit.GetAsync(approved.Order.Id)).Order.Status);

        // Barcode + default quantity 1, then a typed SKU and shelf code (case-insensitive) for the rest.
        var first = await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, id, "8690000000028", location: "LOC-A01"));
        Assert.Equal(("in_progress", 1m, 2), (first.Status, Assert.Single(first.Lines).QuantityPicked, first.OpenLineCount));
        Assert.Equal("picking", (await kit.GetAsync(approved.Order.Id)).Order.Status);
        var second = await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, id, "lamba-01", 2m, "a-01"));
        Assert.Equal((3m, 0m, 1), (second.Lines[0].QuantityPicked, second.Lines[0].QuantityRemaining, second.OpenLineCount));
        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(staff, id, "8690000000011", 2m, "LOC-B01"));

        // Picking writes no movement: stock is still on the shelf and reserved.
        Assert.Equal((10m, 3m), await kit.BalanceAsync(lamp, warehouse.Id, a01.Id));
        var completed = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.CompleteAsync(staff, id));
        Assert.Equal("completed", completed.Task.Status);
        Assert.Equal("packed", (await kit.GetAsync(approved.Order.Id)).Order.Status);
        var shipmentId = completed.ShipmentId!.Value;
        var draft = await Fulfillment.ReadAsync<ShipmentResponse>(await staff.GetAsync($"/api/v1/shipments/{shipmentId}"));
        Assert.Equal(("draft", approved.Order.Number + "-S1", 2), (draft.Status, draft.Number, draft.Lines.Count));

        // #38 pack check: every item scanned again into packages; wrong item / missing / extra are rejected.
        await HttpAssert.ProblemAsync(
            await Fulfillment.PackAsync(staff, shipmentId, [Fulfillment.Item("8690000000035", 1m)]), HttpStatusCode.UnprocessableEntity, "packing.wrong_item");
        await HttpAssert.ProblemAsync(
            await Fulfillment.PackAsync(staff, shipmentId, [Fulfillment.Item("8690000000028", 3m), Fulfillment.Item("8690000000011", 1m)]),
            HttpStatusCode.UnprocessableEntity,
            "packing.quantity_mismatch");
        await HttpAssert.ProblemAsync(
            await Fulfillment.PackAsync(staff, shipmentId, [Fulfillment.Item("8690000000028", 4m), Fulfillment.Item("8690000000011", 2m)]),
            HttpStatusCode.UnprocessableEntity,
            "packing.over_pack");
        var packed = await Fulfillment.ReadAsync<ShipmentResponse>(await Fulfillment.PackAsync(
            staff, shipmentId, [Fulfillment.Item("8690000000028", 3m)], [Fulfillment.Item("8690000000011", 1m), Fulfillment.Item("KASK-01", 1m)]));
        Assert.Equal(("packed", 2, 2), (packed.Status, packed.PackageCount!.Value, packed.Packages.Count));
        Assert.All(packed.Lines, l => Assert.Equal(l.Quantity, l.PackedQuantity));

        // #39: confirmation issues stock once, moves the order and links the movements to the shipment.
        var confirmed = await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(staff, shipmentId));
        Assert.Equal(("confirmed", "shipped", "full", 2), (confirmed.Shipment.Status, confirmed.OrderStatus, confirmed.OrderFulfillmentStatus, confirmed.MovementIds.Count));
        Assert.Equal((7m, 0m), await kit.BalanceAsync(lamp, warehouse.Id, a01.Id));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(helmet, warehouse.Id, b01.Id));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(chain, warehouse.Id, a01.Id));

        var movements = await kit.Owner.OkAsync<MovementPageResponse>(await kit.Owner.GetAsync($"/api/v1/inventory/movements?sourceId={shipmentId}"));
        Assert.Equal(2, movements.Items.Count);
        Assert.All(movements.Items, m => Assert.Equal(("shipment_out", "shipment", shipmentId), (m.Type, m.SourceType, m.SourceId!.Value)));
        Assert.Equal(confirmed.MovementIds.Order(), movements.Items.Select(m => m.Id).Order());
        Assert.Equal(packed.Lines.Select(l => l.Id).Order(), movements.Items.Select(m => m.SourceLineId!.Value).Order());

        var order = await kit.GetAsync(approved.Order.Id);
        Assert.Equal(shipmentId, Assert.Single(order.Shipments).Id);
        Assert.Equal(["create", "submit", "approve", "start_picking", "pack", "ship"], order.History.Select(h => h.Action));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.ShipmentConfirmed}'"));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.ShipmentPacked}'"));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.shipment_confirmed.v1'"));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Shortages_flag_the_order_and_partial_shipment_then_short_close_stays_consistent()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var tire = await kit.VariantAsync(100m);
        var lamp = await kit.VariantAsync(30m);
        await kit.ReceiveAsync(tire, 10m);
        await kit.ReceiveAsync(lamp, 5m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(tire, 10m), OrdersKit.Line(lamp, 5m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var tireLine = task.Lines.Single(l => l.VariantId == tire);
        var lampLine = task.Lines.Single(l => l.VariantId == lamp);

        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, tireLine.Sku, 6m));
        await HttpAssert.ProblemAsync(await Fulfillment.ShortageAsync(kit.Owner, task.Task.Id, tireLine.Id, "lost"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await Fulfillment.ShortageAsync(kit.Owner, task.Task.Id, tireLine.Id, "not_found", 5m), HttpStatusCode.UnprocessableEntity, "picking.shortage_exceeds_open");
        var reported = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.ShortageAsync(kit.Owner, task.Task.Id, tireLine.Id, "not_found"));
        Assert.Equal((4m, "not_found", 0m), (reported.Lines.Single(l => l.Id == tireLine.Id).QuantityShort, reported.Lines.Single(l => l.Id == tireLine.Id).ShortageReason, reported.Lines.Single(l => l.Id == tireLine.Id).QuantityRemaining));
        await HttpAssert.ProblemAsync(await Fulfillment.ShortageAsync(kit.Owner, task.Task.Id, tireLine.Id, "damaged"), HttpStatusCode.Conflict, "picking.line_closed");
        await HttpAssert.ProblemAsync(await Fulfillment.CompleteAsync(kit.Owner, task.Task.Id), HttpStatusCode.Conflict, "picking.incomplete");

        // The order is flagged (timeline + has_shortage) and the shortage went to the outbox (#38). Stock stays reserved.
        var flagged = await kit.GetAsync(approved.Order.Id);
        Assert.True(flagged.Order.HasShortage);
        Assert.Equal(("picking", "report_shortage"), (flagged.Order.Status, flagged.History[^1].Action));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.shortage_reported.v1'"));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.PickingShortageReported}'"));
        Assert.Equal((10m, 10m), await kit.BalanceAsync(tire));

        await Fulfillment.ReadAsync<PickScanResponse>(await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, lampLine.Sku, 5m));
        var completed = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.CompleteAsync(kit.Owner, task.Task.Id));
        Assert.Equal("completed_with_shortage", completed.Task.Status);

        // A damaged lamp found while packing: that line ships one less.
        var draft = await Fulfillment.ReadAsync<ShipmentResponse>(await kit.Owner.GetAsync($"/api/v1/shipments/{completed.ShipmentId}"));
        var shipLamp = draft.Lines.Single(l => l.VariantId == lamp);
        var reduced = await Fulfillment.ReadAsync<ShipmentResponse>(await Fulfillment.PostAsync(
            kit.Owner, $"/api/v1/shipments/{draft.Id}/lines/{shipLamp.Id}/shortage", new { quantity = 1m, reason = "damaged" }));
        Assert.Equal((5m, 4m, 1m), (reduced.Lines.Single(l => l.Id == shipLamp.Id).PickedQuantity, reduced.Lines.Single(l => l.Id == shipLamp.Id).Quantity, reduced.Lines.Single(l => l.Id == shipLamp.Id).ShortQuantity));
        Assert.Equal(2L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.shortage_reported.v1'"));

        // Packing is optional (WHT-07): a draft confirms directly. Partial: the order stays packed.
        var confirmed = await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(kit.Owner, draft.Id));
        Assert.Equal(("packed", "partial"), (confirmed.OrderStatus, confirmed.OrderFulfillmentStatus));
        var partial = await kit.GetAsync(approved.Order.Id);
        Assert.Equal((6m, 4m), (partial.Lines[0].ShippedQuantity, partial.Lines[0].ReservedQuantity));
        Assert.Equal((4m, 1m), (partial.Lines[1].ShippedQuantity, partial.Lines[1].ReservedQuantity));
        Assert.Equal((4m, 4m), await kit.BalanceAsync(tire));
        Assert.Equal((1m, 1m), await kit.BalanceAsync(lamp));
        await kit.AssertConsistentAsync();

        // The manager short-closes: the remaining reservations are released, nothing more ships.
        var closed = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, partial, "close-remaining", new { reason = "Rafta yok" }));
        Assert.Equal(("shipped", "partial"), (closed.Order.Status, closed.Order.FulfillmentStatus));
        Assert.Equal((4m, 0m), await kit.BalanceAsync(tire));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(lamp));
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task A_follow_up_task_ships_the_remainder_and_only_one_task_may_be_active()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var tire = await kit.VariantAsync(100m);
        await kit.ReceiveAsync(tire, 5m);
        var draftOrder = await kit.CreateOrderAsync(OrdersKit.Line(tire, 1m));
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = draftOrder.Order.Id }), HttpStatusCode.Conflict, "picking.order_not_fulfillable");
        await HttpAssert.ProblemAsync(await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = Guid.NewGuid() }), HttpStatusCode.NotFound, "resource.not_found");

        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(tire, 5m));
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = approved.Order.Id }), HttpStatusCode.Conflict, "picking.fulfillment_in_progress");
        var partial = await kit.ShipAsync(approved, (approved.Lines[0].Id, 3m));
        Assert.Equal(("packed", "partial"), (partial.Order.Status, partial.Order.FulfillmentStatus));

        var followUp = await Fulfillment.ReadAsync<PickingTaskResponse>(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = approved.Order.Id }), HttpStatusCode.Created);
        Assert.Equal(2m, Assert.Single(followUp.Lines).QuantityToPick);
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = approved.Order.Id }), HttpStatusCode.Conflict, "picking.fulfillment_in_progress");

        var shipped = await kit.ShipAsync(partial, (approved.Lines[0].Id, 2m));
        Assert.Equal(("shipped", "full", 5m), (shipped.Order.Status, shipped.Order.FulfillmentStatus, shipped.Lines[0].ShippedQuantity));
        var numbers = await Fulfillment.ReadAsync<ShipmentPageResponse>(await kit.Owner.GetAsync($"/api/v1/shipments?orderId={approved.Order.Id}"));
        Assert.Equal([approved.Order.Number + "-S1", approved.Order.Number + "-S2"], numbers.Items.Select(s => s.Number));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(tire));
        await kit.AssertConsistentAsync();
    }

    // ---- Idempotency: stock leaves exactly once ----

    [Fact]
    public async Task Confirm_replay_and_double_confirm_issue_stock_once()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 3m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 3m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 3m });
        var key = Guid.NewGuid().ToString();

        var first = await Fulfillment.ConfirmAsync(kit.Owner, completed.ShipmentId!.Value, key);
        var replay = await Fulfillment.ConfirmAsync(kit.Owner, completed.ShipmentId!.Value, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
        await HttpAssert.ProblemAsync(await Fulfillment.ConfirmAsync(kit.Owner, completed.ShipmentId!.Value), HttpStatusCode.Conflict, "shipment.already_confirmed");
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND type = 'shipment_out'"));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant));
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task A_replayed_scan_counts_once()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 3m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 3m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, task.Lines[0].Sku, key: key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ScanAsync(kit.Owner, task.Task.Id, task.Lines[0].Sku, key: key)).StatusCode);

        Assert.Equal(1m, (await Fulfillment.TaskAsync(kit.Owner, task.Task.Id)).Task.QuantityPicked);
    }

    // ---- Putaway after cancel (ORD-07) ----

    [Fact]
    public async Task Cancelling_after_picking_cancels_the_draft_shipment_and_creates_a_putaway_task()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 3m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 3m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 3m });
        var packed = await kit.GetAsync(approved.Order.Id);

        var cancelled = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, packed, "cancel", new { reason = "Müşteri vazgeçti" }));

        Assert.Equal("cancelled", cancelled.Order.Status);
        var shipment = await Fulfillment.ReadAsync<ShipmentResponse>(await kit.Owner.GetAsync($"/api/v1/shipments/{completed.ShipmentId}"));
        Assert.Equal("cancelled", shipment.Status);
        await HttpAssert.ProblemAsync(await Fulfillment.ConfirmAsync(kit.Owner, shipment.Id), HttpStatusCode.Conflict, "shipment.invalid_transition");
        var putaways = await Fulfillment.ReadAsync<PickingTaskPageResponse>(
            await kit.Owner.GetAsync($"/api/v1/picking-tasks?orderId={approved.Order.Id}&type=putaway"));
        var putaway = Assert.Single(putaways.Items);
        Assert.Equal(("open", 3m), (putaway.Status, putaway.QuantityToPick));

        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ClaimAsync(staff, putaway.Id)).StatusCode);
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(staff, putaway.Id, task.Lines[0].Sku), HttpStatusCode.Conflict, "picking.invalid_transition");
        var done = await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.CompleteAsync(staff, putaway.Id));
        Assert.Equal(("completed", 3m), (done.Task.Status, done.Task.QuantityPicked));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(variant));
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        await kit.AssertConsistentAsync();
    }

    // ---- Staff scope and permissions (#36, roles-permissions §3.6) ----

    [Fact]
    public async Task Staff_act_only_on_tasks_assigned_to_them_and_permissions_are_enforced()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 5m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var id = task.Task.Id;
        var sku = task.Lines[0].Sku;
        var picker = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var other = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var viewer = await kit.Owner.AddMemberAsync(fixture, Roles.Viewer);

        // Pool task: visible to every picker, but nobody acts on it before claiming or being assigned.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/v1/picking-tasks/{id}")).StatusCode);
        Assert.Contains((await Fulfillment.ReadAsync<MyPickingTasksResponse>(await other.GetAsync("/api/v1/picking-tasks/mine?scope=pool"))).Items, t => t.Id == id);
        await HttpAssert.ProblemAsync(await Fulfillment.ScanAsync(picker, id, sku), HttpStatusCode.Forbidden, "picking.not_assignee");

        // Only active members whose role may pick can be assigned.
        await HttpAssert.ProblemAsync(await Fulfillment.AssignAsync(kit.Owner, id, sales.UserId), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await Fulfillment.AssignAsync(kit.Owner, id, Guid.NewGuid()), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await Fulfillment.ReadAsync<PickingTaskResponse>(await Fulfillment.AssignAsync(kit.Owner, id, picker.UserId));

        // Someone else's task: invisible (404) to other staff, and a claim loses with 409.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/picking-tasks/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.ScanAsync(other, id, sku)).StatusCode);
        await HttpAssert.ProblemAsync(await Fulfillment.ClaimAsync(other, id), HttpStatusCode.Conflict, "picking.already_assigned");
        Assert.DoesNotContain((await Fulfillment.ReadAsync<MyPickingTasksResponse>(await other.GetAsync("/api/v1/picking-tasks/mine"))).Items, t => t.Id == id);
        Assert.Contains((await Fulfillment.ReadAsync<MyPickingTasksResponse>(await picker.GetAsync("/api/v1/picking-tasks/mine"))).Items, t => t.Id == id);

        // Endpoint permissions (§3.6): staff cannot assign, create or list everything; sales/viewer cannot pick.
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.AssignAsync(picker, id, picker.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.PostAsync(picker, "/api/v1/picking-tasks", new { orderId = approved.Order.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await picker.GetAsync("/api/v1/picking-tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/picking-tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.ScanAsync(viewer, id, sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.ScanAsync(sales, id, sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/picking-tasks/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.ClaimAsync(viewer, id)).StatusCode);

        var completed = await Fulfillment.PickAndCompleteAsync(picker, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 2m });
        var shipmentId = completed.ShipmentId!.Value;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/shipments/{shipmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.ConfirmAsync(other, shipmentId)).StatusCode);
        Assert.Empty((await Fulfillment.ReadAsync<ShipmentPageResponse>(await other.GetAsync("/api/v1/shipments"))).Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fulfillment.ConfirmAsync(sales, shipmentId)).StatusCode);

        // Fail-closed until #69: warehouse_staff holds orders.read (K⁸) and inventory.movements.read (O⁵) conditionally.
        Assert.Equal(HttpStatusCode.Forbidden, (await picker.GetAsync($"/api/v1/orders/{approved.Order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await picker.GetAsync($"/api/v1/inventory/movements?sourceId={shipmentId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Fulfillment.ConfirmAsync(picker, shipmentId)).StatusCode);
        await kit.AssertConsistentAsync();
    }

    // ---- Tenant isolation (API) ----

    [Fact]
    public async Task Another_tenant_cannot_see_or_touch_tasks_and_shipments()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 2m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 2m });
        var intruder = await CatalogClient.NewTenantOwnerAsync(fixture);

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/v1/picking-tasks/{task.Task.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.AssignAsync(intruder, task.Task.Id, intruder.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.ScanAsync(intruder, task.Task.Id, task.Lines[0].Sku)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.CompleteAsync(intruder, task.Task.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/v1/shipments/{completed.ShipmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.ConfirmAsync(intruder, completed.ShipmentId!.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Fulfillment.PostAsync(intruder, "/api/v1/picking-tasks", new { orderId = approved.Order.Id })).StatusCode);
        Assert.Empty((await Fulfillment.ReadAsync<PickingTaskPageResponse>(await intruder.GetAsync("/api/v1/picking-tasks"))).Items);
        Assert.Empty((await Fulfillment.ReadAsync<ShipmentPageResponse>(await intruder.GetAsync("/api/v1/shipments"))).Items);
        // The tenant header is a selection, never a grant.
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.Targeting(kit.TenantId).GetAsync($"/api/v1/picking-tasks/{task.Task.Id}")).StatusCode);

        Assert.Equal((2m, 2m), await kit.BalanceAsync(variant));
        await kit.AssertConsistentAsync();
    }

    private static async Task<OrderDetailResponse> ApproveAsync(OrdersKit kit, Guid warehouseId, params object[] lines)
    {
        var draft = await OrdersKit.ReadAsync(await kit.CreateResponseAsync(kit.Owner, lines, warehouseId: warehouseId), HttpStatusCode.Created);
        var pending = await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, draft, "submit"));
        return await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, pending, "approve"));
    }
}

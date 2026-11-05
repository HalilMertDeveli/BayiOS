using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// #86: cancelling an unconfirmed shipment (<c>POST /shipments/{id}/cancel</c>, <c>tasks.assign</c>) through
/// <c>FulfillmentStateMachine</c>, with its stock consequences made explicit: no movement, balances unchanged,
/// reservations stay active, the order keeps its status, the picked goods get a putaway task, and the order can be
/// picked again and shipped. Also: shipment lines carry the variant barcode. Synthetic data only.
/// </summary>
public sealed class ShipmentCancelTests(ApiFixture fixture)
{
    [Fact]
    public async Task Cancelling_a_draft_shipment_moves_no_stock_keeps_reservations_and_returns_goods_by_putaway()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m, "IPTAL-01", "8690000000929");
        var unbarcoded = await kit.VariantAsync(10m, "IPTAL-02");
        await kit.ReceiveAsync(variant, 5m);
        await kit.ReceiveAsync(unbarcoded, 5m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 3m), OrdersKit.Line(unbarcoded, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(
            kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 3m, [approved.Lines[1].Id] = 2m });
        var shipmentId = completed.ShipmentId!.Value;

        // Barcode on shipment lines (live from Catalog), null when the variant has none.
        var draft = await Fulfillment.ReadAsync<ShipmentResponse>(await kit.Owner.GetAsync($"/api/v1/shipments/{shipmentId}"));
        Assert.Equal("draft", draft.Status);
        Assert.Equal("8690000000929", draft.Lines.Single(l => l.VariantId == variant).Barcode);
        Assert.Null(draft.Lines.Single(l => l.VariantId == unbarcoded).Barcode);
        Assert.Equal((5m, 3m), await kit.BalanceAsync(variant));

        // Validation and permission (tasks.assign: supervisors only; staff with orders.ship may not cancel).
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "  " }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(staff, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Yanlış koli" }), HttpStatusCode.Forbidden, "auth.forbidden");
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(sales, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Yanlış koli" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Yanlış koli" }, ifMatch: draft.Version + 7),
            HttpStatusCode.PreconditionFailed,
            "concurrency.version_mismatch");

        var key = Guid.NewGuid().ToString();
        var cancelled = await Fulfillment.ReadAsync<CancelShipmentResponse>(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Yanlış koli" }, key, draft.Version));
        Assert.Equal("cancelled", cancelled.Shipment.Status);
        var putawayId = cancelled.PutawayTaskId!.Value;

        // Replay with the same key: the stored response, nothing done twice.
        var replay = await Fulfillment.ReadAsync<CancelShipmentResponse>(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Yanlış koli" }, key, draft.Version));
        Assert.Equal(putawayId, replay.PutawayTaskId);

        // Stock: nothing issued, nothing released — on_hand and reserved exactly as before; the order still holds its
        // reservations and keeps its status.
        Assert.Equal((5m, 3m), await kit.BalanceAsync(variant));
        Assert.Equal((5m, 2m), await kit.BalanceAsync(unbarcoded));
        Assert.Equal(2L, await kit.ActiveReservationsAsync());
        Assert.Equal(0L, await kit.CountAsync($"SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND source_id = '{shipmentId}'"));
        var order = await kit.GetAsync(approved.Order.Id);
        Assert.Equal(("packed", "none"), (order.Order.Status, order.Order.FulfillmentStatus));
        Assert.Equal([3m, 2m], order.Lines.Select(l => l.ReservedQuantity));
        Assert.Empty(order.Shipments);

        // The picked goods go back to the shelf: one putaway task with what the shipment would have shipped.
        var putaway = await Fulfillment.TaskAsync(kit.Owner, putawayId);
        Assert.Equal(("putaway", "open", 5m), (putaway.Task.Type, putaway.Task.Status, putaway.Task.QuantityToPick));
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = '{AuditActions.ShipmentCancelled}'"));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.shipment_cancelled.v1'"));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'warehouse.putaway_created.v1'"));

        // A cancelled shipment is terminal.
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Tekrar" }), HttpStatusCode.Conflict, "shipment.invalid_transition");
        await HttpAssert.ProblemAsync(await Fulfillment.ConfirmAsync(kit.Owner, shipmentId), HttpStatusCode.Conflict, "shipment.invalid_transition");
        await kit.AssertConsistentAsync();

        // The order is picked again from its untouched reservations and ships normally.
        var again = await Fulfillment.ReadAsync<PickingTaskResponse>(
            await Fulfillment.PostAsync(kit.Owner, "/api/v1/picking-tasks", new { orderId = approved.Order.Id }), HttpStatusCode.Created);
        Assert.Equal(5m, again.Task.QuantityToPick);
        var repicked = await Fulfillment.PickAndCompleteAsync(
            kit.Owner, again, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 3m, [approved.Lines[1].Id] = 2m });
        var confirmed = await Fulfillment.ReadAsync<ConfirmShipmentResponse>(await Fulfillment.ConfirmAsync(kit.Owner, repicked.ShipmentId!.Value));
        Assert.Equal(("shipped", "full"), (confirmed.OrderStatus, confirmed.OrderFulfillmentStatus));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(variant));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(unbarcoded));
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{repicked.ShipmentId}/cancel", new { reason = "Geç" }), HttpStatusCode.Conflict, "shipment.invalid_transition");
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task A_packed_shipment_can_be_cancelled_and_packing_shortages_are_not_put_away()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m, "IPTAL-PK", "8690000000936");
        await kit.ReceiveAsync(variant, 4m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 4m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 4m });
        var shipmentId = completed.ShipmentId!.Value;
        var draft = await Fulfillment.ReadAsync<ShipmentResponse>(await kit.Owner.GetAsync($"/api/v1/shipments/{shipmentId}"));

        // One damaged item found while packing (it is missing, not returned to the shelf), then the rest is packed.
        await Fulfillment.ReadAsync<ShipmentResponse>(await Fulfillment.PostAsync(
            kit.Owner, $"/api/v1/shipments/{shipmentId}/lines/{draft.Lines[0].Id}/shortage", new { quantity = 1m, reason = "damaged" }));
        var packed = await Fulfillment.ReadAsync<ShipmentResponse>(await Fulfillment.PackAsync(kit.Owner, shipmentId, [Fulfillment.Item("8690000000936", 3m)]));
        Assert.Equal("packed", packed.Status);

        var cancelled = await Fulfillment.ReadAsync<CancelShipmentResponse>(
            await Fulfillment.PostAsync(kit.Owner, $"/api/v1/shipments/{shipmentId}/cancel", new { reason = "Koli hasarlı" }));
        Assert.Equal("cancelled", cancelled.Shipment.Status);
        var putaway = await Fulfillment.TaskAsync(kit.Owner, cancelled.PutawayTaskId!.Value);
        Assert.Equal(3m, putaway.Task.QuantityToPick);
        Assert.Equal((4m, 4m), await kit.BalanceAsync(variant));
        Assert.Equal(1L, await kit.ActiveReservationsAsync());
        await kit.AssertConsistentAsync();
    }

    [Fact]
    public async Task Another_tenant_cannot_cancel_a_shipment()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 1m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 1m });
        var intruder = await CatalogClient.NewTenantOwnerAsync(fixture);

        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(intruder, $"/api/v1/shipments/{completed.ShipmentId}/cancel", new { reason = "Saldırı" }), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(
            await Fulfillment.PostAsync(intruder.Targeting(kit.TenantId), $"/api/v1/shipments/{completed.ShipmentId}/cancel", new { reason = "Saldırı" }),
            HttpStatusCode.Forbidden,
            "tenant.access_denied");
        var shipment = await Fulfillment.ReadAsync<ShipmentResponse>(await kit.Owner.GetAsync($"/api/v1/shipments/{completed.ShipmentId}"));
        Assert.Equal("draft", shipment.Status);
        Assert.Equal((1m, 1m), await kit.BalanceAsync(variant));
    }
}

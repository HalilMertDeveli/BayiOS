using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Inventory.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>
/// The stock engine's internal commands (reserve / release / issue are called by Orders and Warehouse, not over HTTP):
/// all-or-nothing behaviour, available-stock rules and the ledger ⇔ balance invariants (inventory-and-orders §5.1).
/// </summary>
public class InventoryEngineTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reserve_issue_release_keep_physical_reserved_and_available_consistent()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 10m, null));

        // Approval reserves: available drops by the ordered quantity, on_hand stays (I5).
        var orderId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();
        var reserved = await kit.EngineAsync(s => s.ReserveAsync(new ReserveStock(orderId, warehouse.Id, [new ReserveLine(lineId, variant, 6m)]), Ct));
        var reservation = Assert.Single(reserved.Value!.Reservations);
        Assert.Equal((6m, (Guid?)null, "active"), (reservation.Quantity, reservation.LocationId, reservation.Status));
        Assert.Equal((10m, 6m), await kit.BalanceAsync(variant, warehouse.Id));

        // The same order line cannot be reserved twice.
        var twice = await kit.EngineAsync(s => s.ReserveAsync(new ReserveStock(orderId, warehouse.Id, [new ReserveLine(lineId, variant, 1m)]), Ct));
        Assert.Equal("inventory.already_reserved", twice.Error?.Code);

        // Partial shipment: on_hand and reserved drop together, available unchanged.
        var shipmentId = Guid.CreateVersion7();
        var issued = await kit.EngineAsync(s => s.IssueAsync(
            new IssueStock(shipmentId, warehouse.Id, [new IssueLine(Guid.CreateVersion7(), lineId, variant, null, 4m)], "corr-ship-1"), Ct));
        var shipmentOut = Assert.Single(issued.Value!.Movements);
        Assert.Equal((MovementTypes.ShipmentOut, -4m, 6m, MovementSourceTypes.Shipment, shipmentId, "corr-ship-1"),
            (shipmentOut.Type, shipmentOut.Quantity, shipmentOut.BalanceAfter, shipmentOut.SourceType, shipmentOut.SourceId!.Value, shipmentOut.CorrelationId));
        Assert.Equal((6m, 2m), await kit.BalanceAsync(variant, warehouse.Id));

        // Shipping more than the open reservation is refused.
        var over = await kit.EngineAsync(s => s.IssueAsync(
            new IssueStock(Guid.CreateVersion7(), warehouse.Id, [new IssueLine(Guid.CreateVersion7(), lineId, variant, null, 3m)]), Ct));
        Assert.Equal("inventory.not_reserved", over.Error?.Code);

        // Short-close releases the remaining 2; the reservation ends "consumed" (it was partly shipped).
        var released = await kit.EngineAsync(s => s.ReleaseAsync(new ReleaseReservations(orderId), Ct));
        var closed = Assert.Single(released.Value!.Reservations);
        Assert.Equal((2m, "consumed"), (closed.Quantity, closed.Status));
        Assert.Equal((6m, 0m), await kit.BalanceAsync(variant, warehouse.Id));

        // Releasing again is a no-op.
        Assert.Empty((await kit.EngineAsync(s => s.ReleaseAsync(new ReleaseReservations(orderId), Ct))).Value!.Reservations);
        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Multi_line_reservation_is_all_or_nothing()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var plenty = await kit.VariantAsync();
        var scarce = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (plenty, 100m, null), (scarce, 2m, null));

        var result = await kit.ReserveAsync(warehouse.Id, (plenty, 10m), (scarce, 3m));

        Assert.Equal((409, "inventory.insufficient_stock"), (result.Error!.Status, result.Error.Code));
        var shortage = Assert.Single(result.Error.Shortages!);
        Assert.Equal((scarce, 3m, 2m), (shortage.VariantId, shortage.Requested, shortage.Available));
        Assert.Equal((100m, 0m), await kit.BalanceAsync(plenty, warehouse.Id));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(scarce, warehouse.Id));
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM inventory.stock_reservations WHERE tenant_id = $1"));
    }

    [Fact]
    public async Task Rolled_back_unit_of_work_leaves_no_movement_or_balance_change()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 5m, null));

        // A caller (e.g. order approval) succeeds in the engine but its own later step fails → the request rolls back.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var unitOfWork = await kit.BeginAsync(scope.ServiceProvider);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            Assert.True((await inventory.ReceiveAsync(new ReceiveStock(warehouse.Id, [new ReceiveLine(variant, null, 7m)]), Ct)).Succeeded);
            Assert.True((await inventory.ReserveAsync(
                new ReserveStock(Guid.CreateVersion7(), warehouse.Id, [new ReserveLine(Guid.CreateVersion7(), variant, 12m)]), Ct)).Succeeded);
            await unitOfWork.RollbackAsync(Ct);
        }

        Assert.Equal((5m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1"));
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM inventory.stock_reservations WHERE tenant_id = $1"));

        // A failing command inside a transaction that then commits contributes nothing (savepoint rollback),
        // while the successful command before it is kept.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var unitOfWork = await kit.BeginAsync(scope.ServiceProvider);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            var newVariant = await kit.VariantAsync();
            Assert.True((await inventory.ReceiveAsync(new ReceiveStock(warehouse.Id, [new ReceiveLine(variant, null, 1m)]), Ct)).Succeeded);
            var failed = await inventory.ReserveAsync(
                new ReserveStock(Guid.CreateVersion7(), warehouse.Id, [new ReserveLine(Guid.CreateVersion7(), variant, 1m), new ReserveLine(Guid.CreateVersion7(), newVariant, 1m)]), Ct);
            Assert.Equal("inventory.insufficient_stock", failed.Error?.Code);
            await unitOfWork.CommitAsync(Ct);
        }

        Assert.Equal((6m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM inventory.stock_reservations WHERE tenant_id = $1"));
        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Confirming_the_same_shipment_twice_deducts_stock_once()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 1m, null));

        var orderLine = Guid.CreateVersion7();
        Assert.True((await kit.EngineAsync(s => s.ReserveAsync(
            new ReserveStock(Guid.CreateVersion7(), warehouse.Id, [new ReserveLine(orderLine, variant, 1m)]), Ct))).Succeeded);
        var shipmentLine = Guid.CreateVersion7();
        var shipment = new IssueStock(Guid.CreateVersion7(), warehouse.Id, [new IssueLine(shipmentLine, orderLine, variant, null, 1m)]);
        Assert.True((await kit.EngineAsync(s => s.IssueAsync(shipment, Ct))).Succeeded);
        var again = await kit.EngineAsync(s => s.IssueAsync(shipment, Ct));
        Assert.Equal("inventory.not_reserved", again.Error?.Code);
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND type = 'shipment_out'"));
        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Located_warehouse_allocates_reservations_by_pick_sequence()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("LOC", usesLocations: true);
        var far = await kit.LocationAsync(warehouse.Id, "Z-99", pickSequence: 9);
        var near = await kit.LocationAsync(warehouse.Id, "A-01", pickSequence: 1);
        await kit.ReceiveAsync(warehouse.Id, (variant, 3m, far.Id), (variant, 2m, near.Id));

        var result = await kit.ReserveAsync(warehouse.Id, (variant, 4m));

        Assert.Equal([(near.Id, 2m), (far.Id, 2m)], result.Value!.Reservations.Select(r => (r.LocationId!.Value, r.Quantity)));
        Assert.Equal((2m, 2m), await kit.BalanceAsync(variant, warehouse.Id, near.Id));
        Assert.Equal((3m, 2m), await kit.BalanceAsync(variant, warehouse.Id, far.Id));

        var tooMuch = await kit.ReserveAsync(warehouse.Id, (variant, 2m));
        Assert.Equal(1m, Assert.Single(tooMuch.Error!.Shortages!).Available);
        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Ledger_equals_balances_after_mixed_operations()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var a = await kit.VariantAsync();
        var b = await kit.VariantAsync();
        var w1 = await kit.WarehouseAsync("W1");
        var w2 = await kit.WarehouseAsync("W2");
        var random = new Random(26);

        for (var step = 0; step < 60; step++)
        {
            var variant = random.Next(2) == 0 ? a : b;
            var warehouse = random.Next(2) == 0 ? w1.Id : w2.Id;
            var quantity = random.Next(1, 6);
            switch (random.Next(4))
            {
                case 0:
                    await kit.ReceiveAsync(warehouse, (variant, quantity, null));
                    break;
                case 1:
                    await kit.AdjustAsync(kit.Owner, variant, warehouse, random.Next(2) == 0 ? MovementTypes.AdjustmentOut : MovementTypes.DamageOut, quantity);
                    break;
                case 2:
                    var orderLine = Guid.CreateVersion7();
                    var reserved = await kit.EngineAsync(s => s.ReserveAsync(
                        new ReserveStock(Guid.CreateVersion7(), warehouse, [new ReserveLine(orderLine, variant, quantity)]), Ct));
                    if (reserved.Succeeded && random.Next(2) == 0)
                    {
                        await kit.EngineAsync(s => s.IssueAsync(
                            new IssueStock(Guid.CreateVersion7(), warehouse, [new IssueLine(Guid.CreateVersion7(), orderLine, variant, null, quantity)]), Ct));
                    }

                    break;
                default:
                    await kit.AdjustAsync(kit.Owner, variant, warehouse, MovementTypes.ReturnIn, quantity);
                    break;
            }

            if (step % 15 == 14)
            {
                await kit.AssertInvariantsAsync();
            }
        }

        await kit.AssertInvariantsAsync();
    }
}

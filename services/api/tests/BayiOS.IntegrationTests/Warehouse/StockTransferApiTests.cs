using System.Globalization;
using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// Two-stage warehouse transfers (OD-01, PRD INV-08, AF-07, US-INV-007) and shelf moves (#168) over HTTP against
/// PostgreSQL: the ledger carries every leg (source → in transit → destination), in-transit stock is available
/// nowhere, stock never goes negative and reserved stock never moves.
/// </summary>
public sealed class StockTransferApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>US-INV-007: 10 sent, 9 received → +9 at the destination, 1 reported, "Kısmi teslim"; then closed as lost.</summary>
    [Fact]
    public async Task Ten_sent_nine_received_reports_one_and_in_transit_stock_is_available_nowhere()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, 10m, null));

        var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 10m, null, null));
        Assert.Equal("draft", draft.Transfer.Status);
        Assert.StartsWith("TRF-", draft.Transfer.Number, StringComparison.Ordinal);
        Assert.Equal((10m, 0m), await kit.BalanceAsync(variant, source.Id));

        var dispatched = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch"));
        var transferId = dispatched.Transfer.Transfer.Id;
        Assert.Equal(("in_transit", 10m, 2), (dispatched.Transfer.Transfer.Status, dispatched.Transfer.Transfer.InTransitQuantity, dispatched.MovementIds.Count));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, source.Id));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, destination.Id));
        Assert.Equal((10m, 0m), await kit.BalanceAsync(variant, transferId));

        // "Yoldaki miktar hiçbirinde satılabilir sayılmaz": not in either warehouse, not in the tenant-wide list either.
        var all = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync($"/api/v1/inventory/balances?variantId={variant}"));
        Assert.DoesNotContain(all.Items, b => b.WarehouseId == transferId);
        Assert.Equal(0m, all.Items.Sum(b => b.Available));
        var reserve = await kit.ReserveAsync(destination.Id, (variant, 1m));
        Assert.Equal("inventory.insufficient_stock", reserve.Error?.Code);

        var partial = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.ReceiveAsync(
            kit.Owner, transferId, new { lineId = draft.Lines[0].Id, quantity = 9m }));
        var summary = partial.Transfer.Transfer;
        Assert.Equal(("partially_received", 9m, 1m, 1m, true), (summary.Status, summary.ReceivedQuantity, summary.InTransitQuantity, summary.DiscrepancyQuantity, summary.HasDiscrepancy));
        Assert.Equal((9m, 0m), await kit.BalanceAsync(variant, destination.Id));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, transferId));

        var closed = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(
            kit.Owner, transferId, "close", new { resolution = "lost", reason = "Kargoda kayboldu" }));
        Assert.Equal(("closed", "lost", 1m, 0m, 1m), (closed.Transfer.Transfer.Status, closed.Transfer.CloseResolution, closed.Transfer.Lines[0].LostQuantity,
            closed.Transfer.Transfer.InTransitQuantity, closed.Transfer.Transfer.DiscrepancyQuantity));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, transferId));
        Assert.Equal((9m, 0m), await kit.BalanceAsync(variant, destination.Id));

        var movements = await StockDocuments.MovementsAsync(kit.Owner, transferId);
        Assert.Equal(
            new[]
            {
                (MovementTypes.AdjustmentOut, transferId, -1m),
                (MovementTypes.TransferIn, destination.Id, 9m),
                (MovementTypes.TransferIn, transferId, 10m),
                (MovementTypes.TransferOut, source.Id, -10m),
                (MovementTypes.TransferOut, transferId, -9m),
            }.Order().ToList(),
            movements.Select(m => (m.Type, m.WarehouseId, m.Quantity)).Order().ToList());
        Assert.All(movements, m => Assert.Equal(MovementSourceTypes.Transfer, m.SourceType));

        // Server-provided label and in-transit flag: clients never derive them from ids.
        Assert.All(movements, m => Assert.Equal(draft.Transfer.Number, m.TransferNumber));
        Assert.All(movements, m => Assert.Equal(m.WarehouseId == transferId, m.IsTransit));
        Assert.Equal(3, movements.Count(m => m.IsTransit));
        var single = await kit.Owner.OkAsync<MovementResult>(await kit.Owner.GetAsync($"/api/v1/inventory/movements/{movements.First(m => m.IsTransit).Id}"));
        Assert.Equal((true, draft.Transfer.Number), (single.IsTransit, single.TransferNumber));
        await kit.AssertInvariantsAsync();
        await StockDocuments.AssertTransferPairsBalancedAsync(kit);
        Assert.Equal(4L, await kit.CountAsync(
            $"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND entity_id = '{transferId}' AND action LIKE 'stock_transfer.%'"));
    }

    /// <summary>"Kaynakta satılabilir 5, 6 adet gönderilmek istenirse reddedilir": reserved stock never moves, nothing goes negative.</summary>
    [Fact]
    public async Task Dispatch_takes_only_available_stock_and_reserved_stock_never_moves()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, 7m, null));
        Assert.True((await kit.ReserveAsync(source.Id, (variant, 2m))).Succeeded);

        var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 6m, null, null));
        var refused = await HttpAssert.ProblemJsonAsync(
            await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch"), HttpStatusCode.Conflict, "inventory.insufficient_available");
        var shortage = refused.GetProperty("shortages")[0];
        Assert.Equal((6m, 5m), (shortage.GetProperty("requested").GetDecimal(), shortage.GetProperty("available").GetDecimal()));
        Assert.Equal("draft", (await StockDocuments.GetTransferAsync(kit.Owner, draft.Transfer.Id)).Transfer.Status);
        Assert.Equal((7m, 2m), await kit.BalanceAsync(variant, source.Id));
        Assert.Equal(0L, await kit.CountAsync($"SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND source_id = '{draft.Transfer.Id}'"));

        var exact = await StockDocuments.DispatchedAsync(kit.Owner, source.Id, destination.Id, (variant, 5m, null, null));
        Assert.Equal("in_transit", exact.Transfer.Status);
        Assert.Equal((2m, 2m), await kit.BalanceAsync(variant, source.Id));
        await kit.AssertInvariantsAsync();
    }

    /// <summary>Handheld receipt: product barcode / SKU and shelf barcode / code; wrong, unknown and surplus scans refused; the rest returned.</summary>
    [Fact]
    public async Task Handheld_receipt_by_codes_and_close_by_returning_the_rest_to_the_source_shelf()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var barcode = "2" + Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture);
        var variant = await kit.VariantAsync("TRF-BALATA", barcode);
        await kit.VariantAsync("TRF-ZINCIR"); // a product of the tenant that is not on the transfer
        var source = await kit.WarehouseAsync("SRC", usesLocations: true);
        var destination = await kit.WarehouseAsync("DST", usesLocations: true);
        var shelf = await kit.LocationAsync(source.Id, "S-01");
        var planned = await kit.LocationAsync(destination.Id, "D-01");
        var scanned = await kit.Owner.CreatedAsync<LocationResponse>(
            $"/api/v1/warehouses/{destination.Id}/locations", new { code = "D-02", barcode = "RAF" + barcode[..8], pickSequence = 2 });
        await kit.ReceiveAsync(source.Id, (variant, 10m, shelf.Id));

        var transfer = await StockDocuments.DispatchedAsync(kit.Owner, source.Id, destination.Id, (variant, 6m, shelf.Id, planned.Id));
        var id = transfer.Transfer.Id;
        Assert.Equal((4m, 0m), await kit.BalanceAsync(variant, source.Id, shelf.Id));

        await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.ReceiveAsync(
            kit.Owner, id, new { code = barcode, quantity = 2m, locationCode = "RAF" + barcode[..8] }));
        await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.ReceiveAsync(
            kit.Owner, id, new { code = "TRF-BALATA", quantity = 1m }, new { variantId = variant, quantity = 1m, locationCode = "d-02" }));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(variant, destination.Id, scanned.Id));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, destination.Id, planned.Id));

        await HttpAssert.ProblemAsync(await StockDocuments.ReceiveAsync(kit.Owner, id, new { code = "TRF-ZINCIR", quantity = 1m }), HttpStatusCode.UnprocessableEntity, "transfer.wrong_item");
        await HttpAssert.ProblemAsync(await StockDocuments.ReceiveAsync(kit.Owner, id, new { code = "YOK-0000", quantity = 1m }), HttpStatusCode.UnprocessableEntity, "transfer.unknown_code");
        await HttpAssert.ProblemAsync(
            await StockDocuments.ReceiveAsync(kit.Owner, id, new { code = barcode, quantity = 1m, locationCode = "S-01" }), HttpStatusCode.UnprocessableEntity, "transfer.unknown_location");
        await HttpAssert.ProblemAsync(
            await StockDocuments.ReceiveAsync(kit.Owner, id, new { lineId = transfer.Lines[0].Id, quantity = 3m }), HttpStatusCode.UnprocessableEntity, "transfer.over_receipt");

        var closed = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(
            kit.Owner, id, "close", new { resolution = "return_to_source", reason = "Yanlış sevk" }));
        Assert.Equal(("closed", 2m, 4m), (closed.Transfer.Transfer.Status, closed.Transfer.Lines[0].ReturnedQuantity, closed.Transfer.Lines[0].ReceivedQuantity));
        Assert.Equal((6m, 0m), await kit.BalanceAsync(variant, source.Id, shelf.Id));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, id));
        Assert.Equal(("S-01", "D-01"), (closed.Transfer.Lines[0].SourceLocationCode, closed.Transfer.Lines[0].DestinationLocationCode));
        await kit.AssertInvariantsAsync();
        await StockDocuments.AssertTransferPairsBalancedAsync(kit);
    }

    [Fact]
    public async Task Document_rules_same_warehouse_cancel_only_drafts_and_no_deactivation_while_in_transit()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, 10m, null));

        var same = await HttpAssert.ProblemJsonAsync(
            await StockDocuments.CreateTransferAsync(kit.Owner, source.Id, source.Id, (variant, 1m, null, null)), HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.True(same.GetProperty("errors").TryGetProperty("destinationWarehouseId", out _));
        await HttpAssert.ProblemAsync(
            await StockDocuments.CreateTransferAsync(kit.Owner, source.Id, destination.Id, (variant, 1.5m, null, null)), HttpStatusCode.UnprocessableEntity, "validation.failed");

        var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 1m, null, null));
        await HttpAssert.ProblemAsync(
            await StockDocuments.ReceiveAsync(kit.Owner, draft.Transfer.Id, new { lineId = draft.Lines[0].Id, quantity = 1m }), HttpStatusCode.Conflict, "transfer.invalid_transition");
        var cancelled = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(
            kit.Owner, draft.Transfer.Id, "cancel", new { reason = "Vazgeçildi" }));
        Assert.Equal("cancelled", cancelled.Transfer.Transfer.Status);
        await HttpAssert.ProblemAsync(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch"), HttpStatusCode.Conflict, "transfer.invalid_transition");

        var moving = await StockDocuments.DispatchedAsync(kit.Owner, source.Id, destination.Id, (variant, 2m, null, null));
        await HttpAssert.ProblemAsync(
            await StockDocuments.TransferActionAsync(kit.Owner, moving.Transfer.Id, "cancel", new { reason = "Geç" }), HttpStatusCode.Conflict, "transfer.invalid_transition");
        await HttpAssert.ProblemAsync(
            await StockDocuments.TransferActionAsync(kit.Owner, moving.Transfer.Id, "dispatch", null), HttpStatusCode.Conflict, "transfer.invalid_transition");
        await HttpAssert.ProblemAsync(
            await kit.Owner.PatchAsync($"/api/v1/warehouses/{destination.Id}", new { status = "inactive" }, CatalogClient.ETag(destination.Version)),
            HttpStatusCode.Conflict,
            "warehouse.has_open_transfers");
        await HttpAssert.ProblemAsync(
            await StockDocuments.TransferActionAsync(kit.Owner, moving.Transfer.Id, "close", new { resolution = "stolen", reason = "?" }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");

        // If-Match is honoured: a stale version is refused without any stock change.
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(
                HttpMethod.Post, $"/api/v1/stock-transfers/{moving.Transfer.Id}/receive", new { lines = new[] { new { lineId = moving.Lines[0].Id, quantity = 1m } } }, CatalogClient.ETag(1)),
            HttpStatusCode.PreconditionFailed,
            "concurrency.version_mismatch");
        Assert.Equal((2m, 0m), await kit.BalanceAsync(variant, moving.Transfer.Id));

        var list = await kit.Owner.OkAsync<StockTransferPageResponse>(await kit.Owner.GetAsync($"/api/v1/stock-transfers?status=in_transit,partially_received&warehouseId={destination.Id}"));
        Assert.Equal(moving.Transfer.Id, Assert.Single(list.Items).Id);
        Assert.Equal(("Depo SRC", "Depo DST"), (list.Items[0].SourceWarehouseName, list.Items[0].DestinationWarehouseName));
    }

    /// <summary>roles-permissions §3.4: staff receive (inventory.transfer.receive) but cannot create or send; sales has neither.</summary>
    [Fact]
    public async Task Warehouse_staff_receive_but_cannot_create_dispatch_or_close()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, 3m, null));
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 3m, null, null));

        await HttpAssert.ProblemAsync(await StockDocuments.CreateTransferAsync(staff, source.Id, destination.Id, (variant, 1m, null, null)), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await StockDocuments.TransferActionAsync(staff, draft.Transfer.Id, "dispatch"), HttpStatusCode.Forbidden, "auth.forbidden");
        await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch"));

        Assert.Single((await staff.OkAsync<StockTransferPageResponse>(await staff.GetAsync("/api/v1/stock-transfers"))).Items);
        var received = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.ReceiveAsync(
            staff, draft.Transfer.Id, new { lineId = draft.Lines[0].Id, quantity = 3m }));
        Assert.Equal("received", received.Transfer.Transfer.Status);
        Assert.NotNull(received.Transfer.Transfer.CompletedAt);
        await HttpAssert.ProblemAsync(
            await StockDocuments.TransferActionAsync(staff, draft.Transfer.Id, "close", new { resolution = "lost", reason = "x" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await sales.GetAsync("/api/v1/stock-transfers"), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await sales.GetAsync($"/api/v1/stock-transfers/{draft.Transfer.Id}"), HttpStatusCode.Forbidden, "auth.forbidden");
        Assert.Equal((3m, 0m), await kit.BalanceAsync(variant, destination.Id));
    }

    /// <summary>I11: the same Idempotency-Key replays the stored answer; stock moves once.</summary>
    [Fact]
    public async Task Replayed_dispatch_and_receipt_move_stock_once()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, 5m, null));
        var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 4m, null, null));

        var dispatchKey = Guid.NewGuid().ToString();
        var first = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch", idempotencyKey: dispatchKey));
        var replay = await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch", idempotencyKey: dispatchKey));
        Assert.Equal(first.MovementIds, replay.MovementIds);

        var receiveKey = Guid.NewGuid().ToString();
        var body = new { lines = new[] { new { lineId = draft.Lines[0].Id, quantity = 1m } } };
        await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "receive", body, receiveKey));
        await StockDocuments.ReadAsync<StockTransferActionResponse>(await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "receive", body, receiveKey));

        Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, source.Id));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(variant, draft.Transfer.Id));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, destination.Id));
        Assert.Equal(4L, await kit.CountAsync($"SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND source_id = '{draft.Transfer.Id}'"));
    }

    /// <summary>Shelf-to-shelf (#168, #43): one transaction, two ledger legs; reserved stock stays; never negative.</summary>
    [Fact]
    public async Task Relocation_moves_available_stock_between_shelves_atomically()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("RAF", usesLocations: true);
        var from = await kit.LocationAsync(warehouse.Id, "A-01", 1);
        var to = await kit.LocationAsync(warehouse.Id, "A-02", 2);
        await kit.ReceiveAsync(warehouse.Id, (variant, 5m, from.Id));
        Assert.True((await kit.ReserveAsync(warehouse.Id, (variant, 2m))).Succeeded);

        object Body(decimal quantity, Guid? target = null) => new
        {
            warehouseId = warehouse.Id,
            reference = "Raf düzeni",
            lines = new[] { new { variantId = variant, fromLocationId = from.Id, toLocationId = target ?? to.Id, quantity } },
        };

        await HttpAssert.ProblemAsync(await kit.Owner.PostAsync("/api/v1/inventory/relocations", Body(4m)), HttpStatusCode.Conflict, "inventory.insufficient_available");
        await HttpAssert.ProblemAsync(await kit.Owner.PostAsync("/api/v1/inventory/relocations", Body(1m, from.Id)), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var moved = await StockDocuments.ReadAsync<TransferResult>(await kit.Owner.PostAsync("/api/v1/inventory/relocations", Body(3m)), HttpStatusCode.Created);

        Assert.Equal([MovementTypes.TransferOut, MovementTypes.TransferIn], moved.Movements.Select(m => m.Type));
        var legs = await StockDocuments.MovementsAsync(kit.Owner, moved.TransferId);
        Assert.All(legs, m => Assert.Equal((false, (string?)null), (m.IsTransit, m.TransferNumber))); // a shelf move has no document
        Assert.Equal((2m, 2m), await kit.BalanceAsync(variant, warehouse.Id, from.Id));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(variant, warehouse.Id, to.Id));
        // inventory.relocate (#98): warehouse_staff may move shelves too — here the rest of A-01 is reserved, so the
        // request is authorised but refused for stock; sales may not relocate at all.
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await HttpAssert.ProblemAsync(await staff.PostAsync("/api/v1/inventory/relocations", Body(1m)), HttpStatusCode.Conflict, "inventory.insufficient_available");
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        await HttpAssert.ProblemAsync(await sales.PostAsync("/api/v1/inventory/relocations", Body(1m)), HttpStatusCode.Forbidden, "auth.forbidden");
        await kit.AssertInvariantsAsync();
        await StockDocuments.AssertTransferPairsBalancedAsync(kit);
    }
}

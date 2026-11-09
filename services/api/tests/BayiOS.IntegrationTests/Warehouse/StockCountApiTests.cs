using System.Globalization;
using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// Stock counts (PRD INV-06, AF-06, US-INV-005; inventory-and-orders §4.8) over HTTP against PostgreSQL: snapshot,
/// blind counting by scans, difference report, recount, apply with count_correction movements, movements during
/// the count neither lost nor double counted, never below reserved.
/// </summary>
public sealed class StockCountApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Blind_count_by_scans_hides_system_quantities_from_staff_and_applies_the_differences()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var barcode = "2" + Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture);
        var first = await kit.VariantAsync("SAY-BALATA", barcode);
        var second = await kit.VariantAsync("SAY-ZINCIR");
        var warehouse = await kit.WarehouseAsync("SAY", usesLocations: true);
        var shelf1 = await kit.Owner.CreatedAsync<LocationResponse>($"/api/v1/warehouses/{warehouse.Id}/locations", new { code = "R-01", barcode = "RAF" + barcode[..9], pickSequence = 1 });
        var shelf2 = await kit.LocationAsync(warehouse.Id, "R-02", 2);
        await kit.ReceiveAsync(warehouse.Id, (first, 10m, shelf1.Id), (second, 4m, shelf2.Id));
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);

        var created = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id, blind = true, note = "Yıl sonu" }), HttpStatusCode.Created);
        Assert.Equal(("open", true, 2), (created.Count.Status, created.Count.IsBlind, created.Lines.Count));
        Assert.Equal([10m, 4m], created.Lines.Select(l => l.ExpectedQuantity!.Value));

        var blind = await StockDocuments.GetCountAsync(staff, created.Count.Id);
        Assert.True(blind.Count.QuantitiesHidden);
        Assert.All(blind.Lines, l => Assert.Null(l.ExpectedQuantity));
        Assert.Null(blind.DifferenceCount);

        // Handheld: shelf barcode + product barcode scanned one by one (add), the second shelf set absolutely, and an
        // item found on a shelf where the snapshot had none.
        var scanned = await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.EnterAsync(
            staff,
            created.Count.Id,
            new { code = barcode, locationCode = "RAF" + barcode[..9], quantity = 6m, mode = "add" },
            new { code = barcode, locationCode = "RAF" + barcode[..9], quantity = 2m, mode = "add" },
            new { variantId = second, locationId = shelf2.Id, quantity = 4m },
            new { code = "SAY-ZINCIR", locationCode = "R-01", quantity = 1m }));
        Assert.Equal(3, scanned.Lines.Count);
        Assert.All(scanned.Lines, l => Assert.Null(l.Difference));
        await HttpAssert.ProblemAsync(
            await StockDocuments.EnterAsync(staff, created.Count.Id, new { code = "YOK-0001", locationId = shelf1.Id, quantity = 1m }),
            HttpStatusCode.UnprocessableEntity,
            "count.unknown_code");
        await HttpAssert.ProblemAsync(
            await StockDocuments.EnterAsync(staff, created.Count.Id, new { variantId = first, quantity = 1m }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.CountActionAsync(staff, created.Count.Id, "submit"));
        await HttpAssert.ProblemAsync(
            await StockDocuments.CountActionAsync(staff, created.Count.Id, "apply", new { acceptMovedLines = false }), HttpStatusCode.Forbidden, "auth.forbidden");

        var report = await StockDocuments.GetCountAsync(kit.Owner, created.Count.Id);
        Assert.Equal(("submitted", false, 2, 0), (report.Count.Status, report.Count.QuantitiesHidden, report.DifferenceCount, report.MovedCount));
        Assert.Equal(
            [("R-01", "SAY-BALATA", -2m), ("R-01", "SAY-ZINCIR", 1m), ("R-02", "SAY-ZINCIR", 0m)],
            report.Lines.Select(l => (l.LocationCode, l.Sku, l.Difference!.Value)));

        var applied = await StockDocuments.ReadAsync<ApplyStockCountResponse>(
            await StockDocuments.CountActionAsync(kit.Owner, created.Count.Id, "apply", new { acceptMovedLines = false }));
        Assert.Equal("applied", applied.Count.Count.Status);
        Assert.Equal(2, applied.MovementIds.Count);
        Assert.All(applied.Count.Lines, l => Assert.Equal("applied", l.Status));
        Assert.Equal((8m, 0m), await kit.BalanceAsync(first, warehouse.Id, shelf1.Id));
        Assert.Equal((1m, 0m), await kit.BalanceAsync(second, warehouse.Id, shelf1.Id));
        Assert.Equal((4m, 0m), await kit.BalanceAsync(second, warehouse.Id, shelf2.Id));
        var movements = await StockDocuments.MovementsAsync(kit.Owner, created.Count.Id);
        Assert.All(movements, m => Assert.Equal((MovementTypes.CountCorrection, MovementSourceTypes.StockCount), (m.Type, m.SourceType)));
        Assert.Equal([-2m, 1m], movements.Select(m => m.Quantity).Order());
        await kit.AssertInvariantsAsync();
        Assert.Equal(1L, await kit.CountAsync($"SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'inventory.count_posted' AND entity_id = '{created.Count.Id}'"));
    }

    /// <summary>
    /// US-INV-005: stock keeps moving during the count. A movement before the shelf was counted is already in the
    /// counted quantity; one after it is kept on top — neither is lost or counted twice. Moved lines need consent.
    /// </summary>
    [Fact]
    public async Task Movements_during_the_count_are_neither_lost_nor_double_counted()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var before = await kit.VariantAsync();
        var after = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("SAY");
        await kit.ReceiveAsync(warehouse.Id, (before, 10m, null), (after, 10m, null));
        var count = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id }), HttpStatusCode.Created);

        // 2 leave before this shelf is counted (the counter sees 8; one is really missing → counted 7) …
        Assert.Equal(HttpStatusCode.Created, (await kit.AdjustAsync(kit.Owner, before, warehouse.Id, MovementTypes.DamageOut, 2m)).StatusCode);
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.EnterAsync(kit.Owner, count.Count.Id, new { variantId = before, quantity = 7m }));
        // … and 3 leave after the other one was counted (counted 10, then shipped 3).
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.EnterAsync(kit.Owner, count.Count.Id, new { variantId = after, quantity = 10m }));
        Assert.Equal(HttpStatusCode.Created, (await kit.AdjustAsync(kit.Owner, after, warehouse.Id, MovementTypes.AdjustmentOut, 3m)).StatusCode);
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "submit"));

        var report = await StockDocuments.GetCountAsync(kit.Owner, count.Count.Id);
        Assert.Equal(2, report.MovedCount);
        Assert.All(report.Lines, l => Assert.True(l.MovedSinceSnapshot));
        var refused = await HttpAssert.ProblemJsonAsync(
            await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "apply", new { acceptMovedLines = false }), HttpStatusCode.Conflict, "count.lines_moved");
        Assert.Equal(2, refused.GetProperty("errors").EnumerateObject().Count());
        Assert.Equal((8m, 0m), await kit.BalanceAsync(before, warehouse.Id));

        var applied = await StockDocuments.ReadAsync<ApplyStockCountResponse>(
            await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "apply", new { acceptMovedLines = true }));
        Assert.Single(applied.MovementIds);
        Assert.Equal((7m, 0m), await kit.BalanceAsync(before, warehouse.Id));
        Assert.Equal((7m, 0m), await kit.BalanceAsync(after, warehouse.Id));
        Assert.Equal([-1m, 0m], applied.Count.Lines.Select(l => l.AppliedDelta!.Value).Order());
        await kit.AssertInvariantsAsync();
    }

    /// <summary>AF-06 kenar durumu: counted below the reserved quantity → 409 count.below_reserved, nothing changes.</summary>
    [Fact]
    public async Task A_count_below_the_reserved_quantity_is_refused()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("SAY");
        await kit.ReceiveAsync(warehouse.Id, (variant, 5m, null));
        Assert.True((await kit.ReserveAsync(warehouse.Id, (variant, 4m))).Succeeded);
        var count = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id, scope = "variants", variantIds = new[] { variant } }), HttpStatusCode.Created);
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.EnterAsync(kit.Owner, count.Count.Id, new { lineId = count.Lines[0].Id, quantity = 3m }));
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "submit"));

        var refused = await HttpAssert.ProblemJsonAsync(
            await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "apply", new { acceptMovedLines = true }), HttpStatusCode.Conflict, "count.below_reserved");
        Assert.Equal(4m, refused.GetProperty("shortages")[0].GetProperty("requested").GetDecimal());
        Assert.Equal((5m, 4m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal("submitted", (await StockDocuments.GetCountAsync(kit.Owner, count.Count.Id)).Count.Status);
        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task One_open_count_per_warehouse_recount_reopens_and_cancel_moves_nothing()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var other = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("SAY");
        await kit.ReceiveAsync(warehouse.Id, (variant, 5m, null));
        var count = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id, scope = "variants", variantIds = new[] { variant } }), HttpStatusCode.Created);

        await HttpAssert.ProblemAsync(await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id }), HttpStatusCode.Conflict, "count.already_active");
        await HttpAssert.ProblemAsync(await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "submit"), HttpStatusCode.Conflict, "count.nothing_counted");
        await HttpAssert.ProblemAsync(
            await StockDocuments.EnterAsync(kit.Owner, count.Count.Id, new { variantId = other, quantity = 1m }), HttpStatusCode.UnprocessableEntity, "count.out_of_scope");
        await HttpAssert.ProblemAsync(
            await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id, scope = "locations" }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        var line = count.Lines[0].Id;
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.EnterAsync(kit.Owner, count.Count.Id, new { lineId = line, quantity = 9m }));
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "submit"));
        var reopened = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "recount", new { lineIds = new[] { line } }));
        Assert.Equal(("open", (decimal?)null, "pending"), (reopened.Count.Status, reopened.Lines[0].CountedQuantity, reopened.Lines[0].Status));

        var cancelled = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "cancel", new { reason = "Sayım ertelendi" }));
        Assert.Equal("cancelled", cancelled.Count.Status);
        await HttpAssert.ProblemAsync(
            await StockDocuments.EnterAsync(kit.Owner, count.Count.Id, new { lineId = line, quantity = 1m }), HttpStatusCode.Conflict, "count.invalid_transition");
        Assert.Equal((5m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND type = 'count_correction'"));
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id }), HttpStatusCode.Created);

        var list = await kit.Owner.OkAsync<StockCountPageResponse>(await kit.Owner.GetAsync($"/api/v1/stock-counts?warehouseId={warehouse.Id}&status=open"));
        Assert.Single(list.Items);
        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        await HttpAssert.ProblemAsync(await sales.GetAsync("/api/v1/stock-counts"), HttpStatusCode.Forbidden, "auth.forbidden");
    }
}

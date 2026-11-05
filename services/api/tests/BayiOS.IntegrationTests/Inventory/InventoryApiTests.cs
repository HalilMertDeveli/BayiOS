using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>Inventory API (#26, #27, #28, #30): receipts, adjustments, balances, stock card, policy, permissions.</summary>
public class InventoryApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_receipt_creates_the_default_warehouse_and_receipt_movements()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var brake = await kit.VariantAsync("FRN-01");
        var chain = await kit.VariantAsync("ZNC-01");

        var response = await kit.Owner.PostAsync("/api/v1/inventory/receipts", new
        {
            reference = "İrsaliye A-1",
            lines = new object[]
            {
                new { variantId = brake, quantity = 10m, unitCost = 12.5m },
                new { variantId = chain, quantity = 4m },
                new { variantId = brake, quantity = 2m },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<ReceiptResult>(Ct))!;
        Assert.Equal($"/api/v1/inventory/movements?sourceId={receipt.ReceiptId}", response.Headers.Location?.OriginalString);

        var warehouses = await kit.Owner.OkAsync<WarehouseListResponse>(await kit.Owner.GetAsync("/api/v1/warehouses"));
        var main = Assert.Single(warehouses.Items);
        Assert.Equal(("MAIN", true, main.Id), (main.Code, main.IsDefault, receipt.WarehouseId));

        Assert.Equal(3, receipt.Movements.Count);
        Assert.All(receipt.Movements, m =>
        {
            Assert.Equal((MovementTypes.Receipt, MovementSourceTypes.Receipt, receipt.ReceiptId), (m.Type, m.SourceType, m.SourceId!.Value));
            Assert.Equal(("İrsaliye A-1", kit.Owner.UserId), (m.Reason, m.CreatedBy!.Value));
            Assert.NotNull(m.SourceLineId);
            Assert.False(string.IsNullOrEmpty(m.CorrelationId));
        });
        Assert.Equal([10m, 4m, 12m], receipt.Movements.Select(m => m.BalanceAfter));
        Assert.Equal(12.5m, receipt.Movements[0].UnitCost);

        // A second receipt without warehouse reuses the default.
        var again = await kit.ReceiveAsync(null, (chain, 1m, null));
        Assert.Equal(main.Id, again.WarehouseId);

        var balances = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync($"/api/v1/inventory/balances?warehouseId={main.Id}"));
        Assert.Equal(2, balances.Items.Count);
        var brakeBalance = Assert.Single(balances.Items, b => b.VariantId == brake);
        Assert.Equal((12m, 0m, 12m, 2L), (brakeBalance.OnHand, brakeBalance.Reserved, brakeBalance.Available, brakeBalance.Version));

        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Adjustments_require_a_reason_and_cannot_consume_reserved_stock()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 10m, null));
        Assert.True((await kit.ReserveAsync(warehouse.Id, (variant, 7m))).Succeeded);

        var noReason = await kit.Owner.PostAsync("/api/v1/inventory/adjustments", new { variantId = variant, warehouseId = warehouse.Id, type = "adjustment_out", quantity = 1m });
        await HttpAssert.ProblemAsync(noReason, HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Available = 3: taking 4 would eat into the reservation.
        var tooMuch = await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentOut, 4m);
        await HttpAssert.ProblemAsync(tooMuch, HttpStatusCode.Conflict, "inventory.insufficient_available");
        using (var body = JsonDocument.Parse(await tooMuch.Content.ReadAsStringAsync(Ct)))
        {
            var shortage = Assert.Single(body.RootElement.GetProperty("shortages").EnumerateArray());
            Assert.Equal((4m, 3m), (shortage.GetProperty("requested").GetDecimal(), shortage.GetProperty("available").GetDecimal()));
        }

        var damaged = await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.DamageOut, 3m);
        Assert.Equal(HttpStatusCode.Created, damaged.StatusCode);
        var movement = (await damaged.Content.ReadFromJsonAsync<MovementResult>(Ct))!;
        Assert.Equal((-3m, 7m, "Sayım farkı (test)", MovementSourceTypes.Adjustment), (movement.Quantity, movement.BalanceAfter, movement.Reason, movement.SourceType));
        Assert.Equal($"/api/v1/inventory/movements/{movement.Id}", damaged.Headers.Location?.OriginalString);

        var found = await kit.Owner.OkAsync<MovementResult>(await kit.Owner.GetAsync($"/api/v1/inventory/movements/{movement.Id}"));
        Assert.Equal(movement.Id, found.Id);

        Assert.Equal(HttpStatusCode.Created, (await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentIn, 2m)).StatusCode);
        Assert.Equal((9m, 7m), await kit.BalanceAsync(variant, warehouse.Id));
        await kit.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Negative_stock_policy_is_applied_per_tenant()
    {
        var blocking = await InventoryKit.CreateAsync(fixture);
        var warning = await InventoryKit.CreateAsync(fixture);
        var allowing = await InventoryKit.CreateAsync(fixture);
        await warning.SetNegativeStockPolicyAsync(NegativeStockPolicies.Warn);
        await allowing.SetNegativeStockPolicyAsync(NegativeStockPolicies.Allow);

        foreach (var kit in new[] { blocking, warning, allowing })
        {
            var variant = await kit.VariantAsync();
            var warehouse = await kit.WarehouseAsync("W1");
            await kit.ReceiveAsync(warehouse.Id, (variant, 2m, null));
            var response = await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentOut, 5m);

            if (kit == blocking)
            {
                await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "inventory.insufficient_available");
                Assert.Equal((2m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
                var reserve = await kit.ReserveAsync(warehouse.Id, (variant, 3m));
                Assert.Equal("inventory.insufficient_stock", reserve.Error?.Code);
                await kit.AssertInvariantsAsync();
            }
            else if (kit == warning)
            {
                await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "inventory.insufficient_stock");
                using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)))
                {
                    Assert.True(body.RootElement.GetProperty("confirmationRequired").GetBoolean());
                }

                var confirmed = await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentOut, 5m, confirm: true);
                Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
                Assert.Equal((-3m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
                await kit.AssertInvariantsAsync(negativeAllowed: true);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Equal((-3m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
                var reserve = await kit.ReserveAsync(warehouse.Id, (variant, 2m));
                Assert.True(reserve.Succeeded);
                Assert.Equal((-3m, 2m), await kit.BalanceAsync(variant, warehouse.Id));
                await kit.AssertInvariantsAsync(negativeAllowed: true);
            }
        }
    }

    [Fact]
    public async Task Receipt_is_all_or_nothing_and_validates_references()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var other = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var foreignVariant = await other.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        var located = await kit.WarehouseAsync("W2", usesLocations: true);
        var shelf = await kit.LocationAsync(located.Id, "A-01");

        // Another tenant's variant is "not found": the valid first line must not be applied either.
        var mixed = await kit.Owner.PostAsync("/api/v1/inventory/receipts", new
        {
            warehouseId = warehouse.Id,
            lines = new object[] { new { variantId = variant, quantity = 5m }, new { variantId = foreignVariant, quantity = 1m } },
        });
        await HttpAssert.ProblemAsync(mixed, HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Equal(0L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1"));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, warehouse.Id));

        async Task Invalid(object body) => await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync("/api/v1/inventory/receipts", body), HttpStatusCode.UnprocessableEntity, "validation.failed");

        await Invalid(new { warehouseId = warehouse.Id, lines = Array.Empty<object>() });
        await Invalid(new { warehouseId = warehouse.Id, lines = new[] { new { variantId = variant, quantity = 0m } } });
        await Invalid(new { warehouseId = warehouse.Id, lines = new[] { new { variantId = variant, quantity = 1.5m } } }); // unit "adet" is whole
        await Invalid(new { warehouseId = Guid.NewGuid(), lines = new[] { new { variantId = variant, quantity = 1m } } });
        await Invalid(new { warehouseId = warehouse.Id, lines = new[] { new { variantId = variant, quantity = 1m, locationId = shelf.Id } } });
        await Invalid(new { warehouseId = located.Id, lines = new[] { new { variantId = variant, quantity = 1m } } });
        await Invalid(new { warehouseId = other.TenantId, lines = new[] { new { variantId = variant, quantity = 1m } } });

        var ok = await kit.ReceiveAsync(located.Id, (variant, 2m, shelf.Id));
        Assert.Equal(shelf.Id, Assert.Single(ok.Movements).LocationId);
        Assert.Equal((2m, 0m), await kit.BalanceAsync(variant, located.Id, shelf.Id));
    }

    [Fact]
    public async Task Idempotent_replay_of_a_receipt_does_not_double_stock()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        var key = Guid.NewGuid().ToString();
        var body = new { warehouseId = warehouse.Id, lines = new[] { new { variantId = variant, quantity = 5m } } };

        var first = await kit.Owner.PostAsync("/api/v1/inventory/receipts", body, idempotencyKey: key);
        var replay = await kit.Owner.PostAsync("/api/v1/inventory/receipts", body, idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal((5m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1"));

        var reused = await kit.Owner.PostAsync(
            "/api/v1/inventory/receipts", new { warehouseId = warehouse.Id, lines = new[] { new { variantId = variant, quantity = 6m } } }, idempotencyKey: key);
        await HttpAssert.ProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        Assert.Equal((5m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
    }

    [Fact]
    public async Task Movement_history_is_newest_first_with_keyset_pages()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var otherVariant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        for (var i = 1; i <= 5; i++)
        {
            await kit.ReceiveAsync(warehouse.Id, (variant, i, null));
        }

        await kit.ReceiveAsync(warehouse.Id, (otherVariant, 1m, null));

        var seen = new List<MovementResult>();
        string? cursor = null;
        do
        {
            var path = $"/api/v1/inventory/movements?variantId={variant}&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var page = await kit.Owner.OkAsync<MovementPageResponse>(await kit.Owner.GetAsync(path));
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items);
            cursor = page.Page.NextCursor;
            Assert.Equal(cursor is not null, page.Page.HasMore);
        }
        while (cursor is not null);

        Assert.Equal([15m, 10m, 6m, 3m, 1m], seen.Select(m => m.BalanceAfter));
        Assert.All(seen, m => Assert.Equal(variant, m.VariantId));

        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/inventory/movements?cursor=bogus"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/inventory/movements?type=teleport"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/inventory/balances?sort=x"), HttpStatusCode.BadRequest, "request.malformed");

        // Balances page with keyset too.
        var first = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync("/api/v1/inventory/balances?limit=1"));
        Assert.True(first.Page.HasMore);
        var second = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync($"/api/v1/inventory/balances?limit=1&cursor={first.Page.NextCursor}"));
        Assert.False(second.Page.HasMore);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
    }

    [Theory]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.WarehouseStaff)]
    public async Task Roles_without_inventory_write_are_denied(string role)
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 5m, null));
        var member = await kit.Owner.AddMemberAsync(fixture, role);

        // Every role reads balances (inventory.read).
        Assert.Single((await member.OkAsync<BalancePageResponse>(await member.GetAsync("/api/v1/inventory/balances"))).Items);

        // Only owner/admin/manager adjust; warehouse staff may receive goods.
        await HttpAssert.ProblemAsync(
            await kit.AdjustAsync(member, variant, warehouse.Id, MovementTypes.AdjustmentOut, 1m), HttpStatusCode.Forbidden, "auth.forbidden");
        var receipt = await member.PostAsync("/api/v1/inventory/receipts", new { warehouseId = warehouse.Id, lines = new[] { new { variantId = variant, quantity = 1m } } });
        if (role == Roles.WarehouseStaff)
        {
            Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
            Assert.Equal((6m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        }
        else
        {
            await HttpAssert.ProblemAsync(receipt, HttpStatusCode.Forbidden, "auth.forbidden");
            Assert.Equal((5m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        }

        // Movement history: sales has none; warehouse staff only "own records" (O⁵): the receipt they posted, not the owner's.
        var history = await member.GetAsync("/api/v1/inventory/movements");
        Assert.Equal(role == Roles.Sales ? HttpStatusCode.Forbidden : HttpStatusCode.OK, history.StatusCode);
        if (role == Roles.WarehouseStaff)
        {
            var own = await member.OkAsync<MovementPageResponse>(history);
            Assert.All(own.Items, m => Assert.Equal(member.UserId, m.CreatedBy));
            Assert.Single(own.Items);
        }
    }

    [Fact]
    public async Task Another_tenants_stock_is_neither_readable_nor_writable_over_the_api()
    {
        var a = await InventoryKit.CreateAsync(fixture);
        var b = await InventoryKit.CreateAsync(fixture);
        var variantA = await a.VariantAsync();
        var warehouseA = await a.WarehouseAsync("A1");
        var receipt = await a.ReceiveAsync(warehouseA.Id, (variantA, 5m, null));

        var spoofed = b.Owner.Targeting(a.TenantId);
        await HttpAssert.ProblemAsync(await spoofed.GetAsync("/api/v1/inventory/balances"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(
            await spoofed.PostAsync("/api/v1/inventory/receipts", new { warehouseId = warehouseA.Id, lines = new[] { new { variantId = variantA, quantity = 1m } } }),
            HttpStatusCode.Forbidden,
            "tenant.access_denied");

        // B in its own tenant sees nothing of A and cannot address A's warehouse/variant.
        Assert.Empty((await b.Owner.OkAsync<BalancePageResponse>(await b.Owner.GetAsync($"/api/v1/inventory/balances?variantId={variantA}"))).Items);
        Assert.Empty((await b.Owner.OkAsync<MovementPageResponse>(await b.Owner.GetAsync($"/api/v1/inventory/movements?sourceId={receipt.ReceiptId}"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/inventory/movements/{receipt.Movements[0].Id}")).StatusCode);
        await HttpAssert.ProblemAsync(
            await b.AdjustAsync(b.Owner, variantA, warehouseA.Id, MovementTypes.AdjustmentIn, 1m), HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Equal((5m, 0m), await a.BalanceAsync(variantA, warehouseA.Id));
    }
}

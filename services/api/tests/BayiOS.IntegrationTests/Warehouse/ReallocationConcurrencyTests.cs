using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Warehouse.Contracts;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// #98 races over real HTTP requests and parallel PostgreSQL connections: re-allocation (a relocation carrying
/// reservations, a scan that re-allocates) against a pick scan / a relocation draining the target shelf. The lock
/// hierarchy (order → Warehouse documents → balances → reservations) serialises them; outcomes are exact and the
/// ledger, balances, reservations and order lines reconcile after every round.
/// </summary>
public class ReallocationConcurrencyTests(ApiFixture fixture)
{
    private const int Rounds = 4;

    /// <summary>
    /// A relocation carrying 1 reserved unit A → B races a +1 pick scan on A (task line: 2 on A, A holds exactly the
    /// 2 reserved). Whatever runs first, both succeed and the end state is identical: A keeps the picked unit, the other
    /// reserved unit and its open task quantity are on B — the picked unit is never moved.
    /// </summary>
    [Fact]
    public async Task Relocation_carrying_reservations_racing_a_pick_scan_never_moves_picked_goods()
    {
        var (kit, shelves) = await ReallocationApiTests.LocatedAsync(fixture);
        for (var round = 0; round < Rounds; round++)
        {
            var sku = $"RACE-A{round}";
            var variant = await kit.VariantAsync(10m, sku);
            await kit.ReceiveAsync(variant, 2m, shelves.WarehouseId, shelves.A.Id);
            var approved = await ReallocationApiTests.ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(variant, 2m));
            var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);

            var responses = await StockDocuments.ParallelAsync(2, i => i == 0
                ? Fulfillment.ScanAsync(kit.Owner, task.Task.Id, sku, 1m, "A-01")
                : kit.Owner.PostAsync("/api/v1/inventory/relocations", new
                {
                    warehouseId = shelves.WarehouseId,
                    lines = new[] { new { variantId = variant, fromLocationId = shelves.A.Id, toLocationId = shelves.B.Id, quantity = 1m, includeReserved = true } },
                }));

            Assert.True(responses[0].StatusCode == HttpStatusCode.OK, await responses[0].Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(responses[1].StatusCode == HttpStatusCode.Created, await responses[1].Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var after = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
            Assert.Equal(
                [("A-01", 1m, 1m), ("B-01", 1m, 0m)],
                after.Lines.Select(l => (l.LocationCode!, l.QuantityToPick, l.QuantityPicked)));
            Assert.Equal((1m, 1m), await kit.BalanceAsync(variant, shelves.WarehouseId, shelves.A.Id));
            Assert.Equal((1m, 1m), await kit.BalanceAsync(variant, shelves.WarehouseId, shelves.B.Id));
            await kit.AssertConsistentAsync();
        }
    }

    /// <summary>
    /// A scan on B that needs B's single free unit (re-allocation A → B) races a plain relocation of that free unit
    /// B → C. Exactly one wins: either the reservation moved to B and the relocation is refused for lack of available
    /// stock, or the unit went to C and the scan is refused as a wrong location. Stock is never double-booked.
    /// </summary>
    [Fact]
    public async Task Scan_reallocation_racing_a_relocation_of_the_same_free_stock_exactly_one_wins()
    {
        var (kit, shelves) = await ReallocationApiTests.LocatedAsync(fixture);
        var wins = (Scan: 0, Relocation: 0);
        for (var round = 0; round < Rounds; round++)
        {
            var sku = $"RACE-B{round}";
            var variant = await kit.VariantAsync(10m, sku);
            await kit.ReceiveAsync(variant, 1m, shelves.WarehouseId, shelves.A.Id);
            await kit.ReceiveAsync(variant, 1m, shelves.WarehouseId, shelves.B.Id);
            var approved = await ReallocationApiTests.ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(variant, 1m));
            var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
            Assert.Equal("A-01", Assert.Single(task.Lines).LocationCode);

            var responses = await StockDocuments.ParallelAsync(2, i => i == 0
                ? Fulfillment.ScanAsync(kit.Owner, task.Task.Id, sku, 1m, "B-01")
                : kit.Owner.PostAsync("/api/v1/inventory/relocations", new
                {
                    warehouseId = shelves.WarehouseId,
                    lines = new[] { new { variantId = variant, fromLocationId = shelves.B.Id, toLocationId = shelves.C.Id, quantity = 1m } },
                }));

            var scanWon = responses[0].StatusCode == HttpStatusCode.OK;
            var relocationWon = responses[1].StatusCode == HttpStatusCode.Created;
            Assert.True(scanWon ^ relocationWon, $"scan {(int)responses[0].StatusCode}, relocation {(int)responses[1].StatusCode}");
            var after = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
            if (scanWon)
            {
                wins.Scan++;
                await HttpAssert.ProblemAsync(responses[1], HttpStatusCode.Conflict, "inventory.insufficient_available");
                Assert.Equal(("B-01", 1m), (Assert.Single(after.Lines).LocationCode, after.Lines[0].QuantityPicked));
                Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, shelves.WarehouseId, shelves.A.Id));
                Assert.Equal((1m, 1m), await kit.BalanceAsync(variant, shelves.WarehouseId, shelves.B.Id));
            }
            else
            {
                wins.Relocation++;
                await HttpAssert.ProblemAsync(responses[0], HttpStatusCode.UnprocessableEntity, "picking.wrong_location");
                Assert.Equal(("A-01", 0m), (Assert.Single(after.Lines).LocationCode, after.Lines[0].QuantityPicked));
                Assert.Equal((1m, 1m), await kit.BalanceAsync(variant, shelves.WarehouseId, shelves.A.Id));
                Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, shelves.WarehouseId, shelves.C.Id));
            }

            await kit.AssertConsistentAsync();
        }

        Assert.Equal(Rounds, wins.Scan + wins.Relocation);
    }

    /// <summary>
    /// Deactivating the reserved shelf races a +1 scan on it: the scan either lands first (the picked unit stays on A,
    /// the open rest moves to B) or after (A's open quantity already moved; the scan on the inactive shelf is refused).
    /// Picked goods never move and the books reconcile either way.
    /// </summary>
    [Fact]
    public async Task Shelf_deactivation_racing_a_pick_scan_is_always_consistent()
    {
        var (kit, shelves) = await ReallocationApiTests.LocatedAsync(fixture);
        var sku = "RACE-C";
        var variant = await kit.VariantAsync(10m, sku);
        await kit.ReceiveAsync(variant, 2m, shelves.WarehouseId, shelves.A.Id);
        await kit.ReceiveAsync(variant, 2m, shelves.WarehouseId, shelves.B.Id);
        var approved = await ReallocationApiTests.ApproveAsync(kit, shelves.WarehouseId, OrdersKit.Line(variant, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);

        var responses = await StockDocuments.ParallelAsync(2, i => i == 0
            ? Fulfillment.ScanAsync(kit.Owner, task.Task.Id, sku, 1m, "A-01")
            : kit.Owner.PatchAsync($"/api/v1/locations/{shelves.A.Id}", new { isActive = false }, Catalog.CatalogClient.ETag(shelves.A.Version)));

        Assert.True(responses[1].IsSuccessStatusCode, await responses[1].Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var after = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
        if (responses[0].StatusCode == HttpStatusCode.OK)
        {
            // The scan's lock came first, or the pick was taken on A before its quantity moved.
            Assert.Equal(1m, after.Task.QuantityPicked);
            Assert.Equal(1m, after.Lines.Single(l => l.LocationCode == "A-01").QuantityPicked);
        }
        else
        {
            await HttpAssert.ProblemAsync(responses[0], HttpStatusCode.UnprocessableEntity, "picking.wrong_location");
            Assert.Equal(0m, after.Task.QuantityPicked);
        }

        Assert.Equal(2m, after.Lines.Sum(l => l.QuantityToPick));
        Assert.Equal(0m, after.Lines.Where(l => l.LocationCode == "A-01").Sum(l => l.QuantityRemaining));
        await kit.AssertConsistentAsync();
    }
}

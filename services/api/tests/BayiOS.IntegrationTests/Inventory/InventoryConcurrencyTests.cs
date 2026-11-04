using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Inventory.Contracts;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>
/// Concurrency (inventory-and-orders §5.2, issue #30): real parallel connections against PostgreSQL. Every scenario
/// releases all workers at once through a barrier, then checks exact outcomes and the ledger ⇔ balance invariants.
/// </summary>
public class InventoryConcurrencyTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>C1: 20 parallel reserve-1 against on_hand 5 → exactly 5 succeed; never oversold.</summary>
    [Fact]
    public async Task Parallel_reservations_never_oversell()
    {
        const int Stock = 5;
        const int Workers = 20;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, Stock, null));

        var results = await Parallel(Workers, _ => kit.ReserveAsync(warehouse.Id, (variant, 1m)));

        Assert.Equal(Stock, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal("inventory.insufficient_stock", r.Error!.Code));
        Assert.Equal((5m, 5m), await kit.BalanceAsync(variant, warehouse.Id));
        Assert.Equal(Stock, await kit.CountAsync("SELECT count(*) FROM inventory.stock_reservations WHERE tenant_id = $1 AND status = 'active'"));
        await kit.AssertInvariantsAsync();
    }

    /// <summary>"Son birim" (#30): 10 parallel API adjustments for the last unit → exactly one wins.</summary>
    [Fact]
    public async Task Parallel_requests_for_the_last_unit_let_exactly_one_succeed()
    {
        const int Workers = 10;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, 1m, null));

        var responses = await Parallel(Workers, _ => kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.DamageOut, 1m));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "inventory.insufficient_available");
        }

        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        await kit.AssertInvariantsAsync();
    }

    /// <summary>
    /// Receipts and shipment issues race on the same balance row: 10 reserved units are shipped one by one while 10
    /// units are received one by one. Every operation succeeds and the final state is exact.
    /// </summary>
    [Fact]
    public async Task Concurrent_receipts_and_issues_stay_consistent()
    {
        const int Pairs = 10;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, Pairs, null));
        var orderLines = new List<Guid>();
        for (var i = 0; i < Pairs; i++)
        {
            var line = Guid.CreateVersion7();
            orderLines.Add(line);
            Assert.True((await kit.EngineAsync(s => s.ReserveAsync(
                new ReserveStock(Guid.CreateVersion7(), warehouse.Id, [new ReserveLine(line, variant, 1m)]), Ct))).Succeeded);
        }

        var results = await Parallel(Pairs * 2, async i => i % 2 == 0
            ? (await kit.EngineAsync(s => s.IssueAsync(
                new IssueStock(Guid.CreateVersion7(), warehouse.Id, [new IssueLine(Guid.CreateVersion7(), orderLines[i / 2], variant, null, 1m)]), Ct))).Error
            : (await kit.EngineAsync(s => s.ReceiveAsync(new ReceiveStock(warehouse.Id, [new ReceiveLine(variant, null, 1m)]), Ct))).Error);

        Assert.All(results, Assert.Null);
        Assert.Equal((10m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        // No lost update: 1 initial receipt + 10 reservations + 20 concurrent changes, each one version step.
        Assert.Equal(1 + Pairs + (Pairs * 2), await kit.CountAsync(
            "SELECT max(version) FROM inventory.inventory_balances WHERE tenant_id = $1"));
        Assert.Equal(Pairs, await kit.CountAsync(
            "SELECT count(*) FROM inventory.stock_reservations WHERE tenant_id = $1 AND status = 'consumed' AND consumed_quantity = quantity"));
        await kit.AssertInvariantsAsync();
    }

    /// <summary>C2/C7: multi-line operations touching the same rows in opposite line order do not deadlock.</summary>
    [Fact]
    public async Task Opposite_line_orders_do_not_deadlock()
    {
        const int Workers = 16;
        var kit = await InventoryKit.CreateAsync(fixture);
        var a = await kit.VariantAsync();
        var b = await kit.VariantAsync();
        var c = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (a, 100m, null), (b, 100m, null), (c, 100m, null));

        var receipts = await Parallel(Workers, i => kit.EngineAsync(s => s.ReceiveAsync(
            new ReceiveStock(warehouse.Id, i % 2 == 0
                ? [new ReceiveLine(a, null, 1m), new ReceiveLine(b, null, 1m), new ReceiveLine(c, null, 1m)]
                : [new ReceiveLine(c, null, 1m), new ReceiveLine(b, null, 1m), new ReceiveLine(a, null, 1m)]),
            Ct)));
        var reservations = await Parallel(Workers, i => i % 2 == 0
            ? kit.ReserveAsync(warehouse.Id, (a, 1m), (b, 1m), (c, 1m))
            : kit.ReserveAsync(warehouse.Id, (c, 1m), (b, 1m), (a, 1m)));

        Assert.All(receipts, r => Assert.Null(r.Error));
        Assert.All(reservations, r => Assert.Null(r.Error));
        foreach (var variant in new[] { a, b, c })
        {
            Assert.Equal(100m + Workers, (await kit.BalanceAsync(variant, warehouse.Id)).OnHand);
            Assert.Equal(Workers, (await kit.BalanceAsync(variant, warehouse.Id)).Reserved);
        }

        await kit.AssertInvariantsAsync();
    }

    /// <summary>C3: approvals (reservations) racing manual adjustments at the stock boundary never exceed on_hand.</summary>
    [Fact]
    public async Task Reservations_racing_adjustments_never_exceed_stock()
    {
        const int Stock = 6;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var warehouse = await kit.WarehouseAsync("W1");
        await kit.ReceiveAsync(warehouse.Id, (variant, Stock, null));

        var outcomes = await Parallel(12, async i =>
        {
            if (i % 2 == 0)
            {
                return (await kit.ReserveAsync(warehouse.Id, (variant, 1m))).Succeeded;
            }

            return (await kit.AdjustAsync(kit.Owner, variant, warehouse.Id, MovementTypes.AdjustmentOut, 1m)).StatusCode == HttpStatusCode.Created;
        });

        Assert.Equal(Stock, outcomes.Count(o => o));
        var (onHand, reserved) = await kit.BalanceAsync(variant, warehouse.Id);
        Assert.Equal(onHand, reserved);
        Assert.Equal(Stock, reserved + (Stock - onHand));
        await kit.AssertInvariantsAsync();
    }

    /// <summary>Queues every worker behind one gate, opens it at once and waits for all of them.</summary>
    private static async Task<T[]> Parallel<T>(int count, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(
                async () =>
                {
                    await gate.Task;
                    return await work(i);
                },
                Ct))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}

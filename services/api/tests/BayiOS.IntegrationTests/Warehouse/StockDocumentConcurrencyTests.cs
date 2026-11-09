using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// Transfer and count races over real HTTP requests and parallel PostgreSQL connections (inventory-and-orders §5.2,
/// #168). Workers start together through a barrier; outcomes are exact, nothing goes negative, every unit is in the
/// ledger exactly once (I1–I3) and every transfer pair sums to zero (I10).
/// </summary>
public sealed class StockDocumentConcurrencyTests(ApiFixture fixture)
{
    /// <summary>Two (and ten) dispatches race for the same stock: only what is available leaves; never oversold.</summary>
    [Fact]
    public async Task Dispatches_racing_for_the_same_stock_never_send_more_than_is_available()
    {
        const int Stock = 5;
        const int Workers = 10;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, Stock, null));

        // Two transfers of 4 against 5: exactly one leaves.
        var big = new[]
        {
            await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 4m, null, null)),
            await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 4m, null, null)),
        };
        var pair = await StockDocuments.ParallelAsync(2, i => StockDocuments.TransferActionAsync(kit.Owner, big[i].Transfer.Id, "dispatch"));
        Assert.Single(pair, r => r.StatusCode == HttpStatusCode.OK);
        await HttpAssert.ProblemAsync(pair.Single(r => r.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "inventory.insufficient_available");
        Assert.Equal((1m, 0m), await kit.BalanceAsync(variant, source.Id));

        // Ten transfers of 1 against the 1 left plus 4 more received: exactly 5 leave.
        await kit.ReceiveAsync(source.Id, (variant, 4m, null));
        var small = new List<StockTransferResponse>();
        for (var i = 0; i < Workers; i++)
        {
            small.Add(await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 1m, null, null)));
        }

        var many = await StockDocuments.ParallelAsync(Workers, i => StockDocuments.TransferActionAsync(kit.Owner, small[i].Transfer.Id, "dispatch"));
        Assert.Equal(Stock, many.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var refused in many.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "inventory.insufficient_available");
        }

        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, source.Id));
        Assert.Equal(4L + Stock, await kit.CountAsync(
            "SELECT coalesce(sum(on_hand), 0)::bigint FROM inventory.inventory_balances WHERE tenant_id = $1 AND is_transit"));
        await kit.AssertInvariantsAsync();
        await StockDocuments.AssertTransferPairsBalancedAsync(kit);
    }

    /// <summary>The same transfer dispatched twice at once (different Idempotency-Keys): it leaves once.</summary>
    [Fact]
    public async Task The_same_transfer_dispatched_twice_at_once_leaves_once()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, 10m, null));
        var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 3m, null, null));

        var responses = await StockDocuments.ParallelAsync(4, _ => StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "transfer.invalid_transition");
        }

        Assert.Equal((7m, 0m), await kit.BalanceAsync(variant, source.Id));
        Assert.Equal((3m, 0m), await kit.BalanceAsync(variant, draft.Transfer.Id));
        Assert.Equal(2L, await kit.CountAsync($"SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND source_id = '{draft.Transfer.Id}'"));
        await kit.AssertInvariantsAsync();
    }

    /// <summary>
    /// Receive racing close (the in-transit counterpart of receive vs cancel): either the goods arrive and the close
    /// is refused, or they are written off and the receipt is refused — never both, never neither.
    /// </summary>
    [Fact]
    public async Task Receive_racing_close_lets_exactly_one_win()
    {
        const int Rounds = 4;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");
        await kit.ReceiveAsync(source.Id, (variant, Rounds * 5m, null));

        var received = 0m;
        var lost = 0m;
        for (var round = 0; round < Rounds; round++)
        {
            var transfer = await StockDocuments.DispatchedAsync(kit.Owner, source.Id, destination.Id, (variant, 5m, null, null));
            var responses = await StockDocuments.ParallelAsync(2, i => i == 0
                ? StockDocuments.ReceiveAsync(kit.Owner, transfer.Transfer.Id, new { lineId = transfer.Lines[0].Id, quantity = 5m })
                : StockDocuments.TransferActionAsync(kit.Owner, transfer.Transfer.Id, "close", new { resolution = "lost", reason = "Yarış" }));

            var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            await HttpAssert.ProblemAsync(responses.Single(r => r != winner), HttpStatusCode.Conflict, "transfer.invalid_transition");
            var final = await StockDocuments.GetTransferAsync(kit.Owner, transfer.Transfer.Id);
            Assert.Equal(winner == responses[0] ? "received" : "closed", final.Transfer.Status);
            received += final.Transfer.ReceivedQuantity;
            lost += final.Transfer.LostQuantity;
            Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, transfer.Transfer.Id));
        }

        Assert.Equal(Rounds * 5m, received + lost);
        Assert.Equal((received, 0m), await kit.BalanceAsync(variant, destination.Id));
        await kit.AssertInvariantsAsync();
        await StockDocuments.AssertTransferPairsBalancedAsync(kit);
    }

    /// <summary>C7: transfers in both directions at once (A→B and B→A) do not deadlock; Σ on_hand is preserved.</summary>
    [Fact]
    public async Task Opposite_transfers_do_not_deadlock_and_preserve_the_total()
    {
        const int Workers = 12;
        var kit = await InventoryKit.CreateAsync(fixture);
        var first = await kit.VariantAsync();
        var second = await kit.VariantAsync();
        var a = await kit.WarehouseAsync("WA");
        var b = await kit.WarehouseAsync("WB");
        await kit.ReceiveAsync(a.Id, (first, 50m, null), (second, 50m, null));
        await kit.ReceiveAsync(b.Id, (first, 50m, null), (second, 50m, null));
        var drafts = new List<StockTransferResponse>();
        for (var i = 0; i < Workers; i++)
        {
            drafts.Add(i % 2 == 0
                ? await StockDocuments.DraftAsync(kit.Owner, a.Id, b.Id, (first, 1m, null, null), (second, 1m, null, null))
                : await StockDocuments.DraftAsync(kit.Owner, b.Id, a.Id, (second, 1m, null, null), (first, 1m, null, null)));
        }

        var dispatched = await StockDocuments.ParallelAsync(Workers, i => StockDocuments.TransferActionAsync(kit.Owner, drafts[i].Transfer.Id, "dispatch"));
        Assert.All(dispatched, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var receipts = await StockDocuments.ParallelAsync(Workers, i => StockDocuments.ReceiveAsync(
            kit.Owner, drafts[i].Transfer.Id, new { lineId = drafts[i].Lines[0].Id, quantity = 1m }, new { lineId = drafts[i].Lines[1].Id, quantity = 1m }));
        Assert.All(receipts, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        foreach (var variant in new[] { first, second })
        {
            Assert.Equal(50m, (await kit.BalanceAsync(variant, a.Id)).OnHand);
            Assert.Equal(50m, (await kit.BalanceAsync(variant, b.Id)).OnHand);
        }

        await kit.AssertInvariantsAsync();
        await StockDocuments.AssertTransferPairsBalancedAsync(kit);
    }

    /// <summary>An order approval (reservation) racing a dispatch for the last units: exactly one gets them.</summary>
    [Fact]
    public async Task Reservation_racing_dispatch_for_the_last_units_lets_exactly_one_win()
    {
        const int Rounds = 4;
        var kit = await InventoryKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var source = await kit.WarehouseAsync("SRC");
        var destination = await kit.WarehouseAsync("DST");

        for (var round = 0; round < Rounds; round++)
        {
            await kit.ReceiveAsync(source.Id, (variant, 3m, null));
            var draft = await StockDocuments.DraftAsync(kit.Owner, source.Id, destination.Id, (variant, 3m, null, null));
            var outcomes = await StockDocuments.ParallelAsync(2, async i => i == 0
                ? (await StockDocuments.TransferActionAsync(kit.Owner, draft.Transfer.Id, "dispatch")).StatusCode == HttpStatusCode.OK
                : (await kit.ReserveAsync(source.Id, (variant, 3m))).Succeeded);
            Assert.Single(outcomes, o => o);
        }

        var (onHand, reserved) = await kit.BalanceAsync(variant, source.Id);
        Assert.True(onHand == reserved, $"on_hand {onHand} must equal reserved {reserved}: every unit went to exactly one side");
        await kit.AssertInvariantsAsync();
    }

    /// <summary>
    /// C8: applying a count while stock of the counted lines moves. Whichever runs first, the final on_hand is
    /// counted + the movement (the movement happened after counting): never lost, never doubled.
    /// </summary>
    [Fact]
    public async Task Count_apply_racing_a_movement_keeps_the_movement_exactly_once()
    {
        const int Lines = 6;
        var kit = await InventoryKit.CreateAsync(fixture);
        var warehouse = await kit.WarehouseAsync("SAY");
        var variants = new List<Guid>();
        for (var i = 0; i < Lines; i++)
        {
            var variant = await kit.VariantAsync();
            variants.Add(variant);
            await kit.ReceiveAsync(warehouse.Id, (variant, 10m, null));
        }

        var count = await StockDocuments.ReadAsync<StockCountResponse>(
            await StockDocuments.CreateCountAsync(kit.Owner, new { warehouseId = warehouse.Id }), HttpStatusCode.Created);
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.EnterAsync(
            kit.Owner, count.Count.Id, [.. variants.Select(v => (object)new { variantId = v, quantity = 12m })]));
        await StockDocuments.ReadAsync<StockCountResponse>(await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "submit"));

        var results = await StockDocuments.ParallelAsync(Lines + 1, async i => i == 0
            ? (await StockDocuments.CountActionAsync(kit.Owner, count.Count.Id, "apply", new { acceptMovedLines = true })).StatusCode
            : (await kit.AdjustAsync(kit.Owner, variants[i - 1], warehouse.Id, MovementTypes.AdjustmentOut, 1m)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, results[0]);
        Assert.All(results.Skip(1), s => Assert.Equal(HttpStatusCode.Created, s));
        foreach (var variant in variants)
        {
            Assert.Equal((11m, 0m), await kit.BalanceAsync(variant, warehouse.Id));
        }

        await kit.AssertInvariantsAsync();
    }
}

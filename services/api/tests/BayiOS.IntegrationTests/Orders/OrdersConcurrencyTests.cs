using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Orders.Contracts;

namespace BayiOS.IntegrationTests.Orders;

/// <summary>
/// Order-level concurrency (inventory-and-orders §5.2, issue #33) over real HTTP requests and parallel PostgreSQL
/// connections. Workers are released together through a barrier; outcomes are exact and the ledger reconciles.
/// </summary>
public class OrdersConcurrencyTests(ApiFixture fixture)
{
    private static readonly HttpStatusCode[] LoserStatuses = [HttpStatusCode.PreconditionFailed, HttpStatusCode.Conflict];
    private static readonly string[] RaceOutcomes = ["cancelled", "shipped"];

    /// <summary>C1 at order level: 10 orders × 1 unit approved in parallel against stock 3 → exactly 3 approved.</summary>
    [Fact]
    public async Task Parallel_approvals_competing_for_limited_stock_never_oversell()
    {
        const int Orders = 10;
        const decimal Stock = 3m;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(50m, "LED-SON-STOK");
        await kit.ReceiveAsync(variant, Stock);
        var pending = new List<OrderDetailResponse>();
        for (var i = 0; i < Orders; i++)
        {
            pending.Add(await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 1m)));
        }

        var responses = await Parallel(Orders, i => OrdersKit.ActAsync(kit.Owner, pending[i], "approve"));

        Assert.Equal((int)Stock, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.ProblemAsync(rejected, HttpStatusCode.Conflict, "inventory.insufficient_stock");
        }

        Assert.Equal((Stock, Stock), await kit.BalanceAsync(variant));
        Assert.Equal((long)Stock, await kit.ActiveReservationsAsync());
        Assert.Equal((long)Stock, await kit.CountAsync("SELECT count(*) FROM orders.orders WHERE tenant_id = $1 AND status = 'approved'"));
        Assert.Equal(Orders - (long)Stock, await kit.CountAsync("SELECT count(*) FROM orders.orders WHERE tenant_id = $1 AND status = 'pending_approval'"));
        await kit.AssertConsistentAsync();
    }

    /// <summary>C4: the same order approved 5 times in parallel (different keys) → reserved exactly once.</summary>
    [Fact]
    public async Task Parallel_approvals_of_the_same_order_reserve_once()
    {
        const int Workers = 5;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 10m);
        var order = await kit.SubmittedOrderAsync(OrdersKit.Line(variant, 4m));

        var responses = await Parallel(Workers, _ => OrdersKit.ActAsync(kit.Owner, order, "approve"));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        // Losers wait on the order row lock, then see a newer version (If-Match) — never a second reservation.
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.StatusCode, LoserStatuses));
        Assert.Equal((10m, 4m), await kit.BalanceAsync(variant));
        Assert.Equal(1L, await kit.ActiveReservationsAsync());
        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM orders.order_status_history WHERE tenant_id = $1 AND action = 'approve'"));
        await kit.AssertConsistentAsync();
    }

    /// <summary>C2: orders with lines (A, B) and (B, A) approved in parallel → no deadlock, both reserved.</summary>
    [Fact]
    public async Task Approvals_with_opposite_line_order_do_not_deadlock()
    {
        const int Pairs = 5;
        var kit = await OrdersKit.CreateAsync(fixture);
        var a = await kit.VariantAsync(10m);
        var b = await kit.VariantAsync(20m);
        await kit.ReceiveAsync(a, Pairs * 2);
        await kit.ReceiveAsync(b, Pairs * 2);
        var pending = new List<OrderDetailResponse>();
        for (var i = 0; i < Pairs; i++)
        {
            pending.Add(await kit.SubmittedOrderAsync(OrdersKit.Line(a, 1m), OrdersKit.Line(b, 1m)));
            pending.Add(await kit.SubmittedOrderAsync(OrdersKit.Line(b, 1m), OrdersKit.Line(a, 1m)));
        }

        var responses = await Parallel(pending.Count, i => OrdersKit.ActAsync(kit.Owner, pending[i], "approve"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal((Pairs * 2m, Pairs * 2m), await kit.BalanceAsync(a));
        Assert.Equal((Pairs * 2m, Pairs * 2m), await kit.BalanceAsync(b));
        await kit.AssertConsistentAsync();
    }

    /// <summary>C6: cancel and shipment of the same order race → exactly one wins; never both.</summary>
    [Fact]
    public async Task Cancel_and_shipment_race_lets_exactly_one_win()
    {
        const int Rounds = 4;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, Rounds * 2);
        for (var round = 0; round < Rounds; round++)
        {
            var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
            // Picked and packed: the draft shipment is ready to confirm.
            var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
            var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 2m });
            var packed = await kit.GetAsync(approved.Order.Id);

            var responses = await Parallel(2, i => i == 0
                ? OrdersKit.ActAsync(kit.Owner, packed, "cancel", new { reason = "Yarış" }, ifMatch: false)
                : Fulfillment.ConfirmAsync(kit.Owner, completed.ShipmentId!.Value));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            var final = await kit.GetAsync(packed.Order.Id);
            Assert.Contains(final.Order.Status, RaceOutcomes);
            // Cancel won: the shipment was cancelled with the order (confirm sees it), picked goods go back (putaway).
            // Shipment won: cancel is an illegal transition after shipping.
            await HttpAssert.ProblemAsync(loser, HttpStatusCode.Conflict, final.Order.Status == "cancelled" ? "shipment.invalid_transition" : "order.invalid_transition");
        }

        await kit.AssertConsistentAsync();
        Assert.Equal(0L, await kit.ActiveReservationsAsync());
    }

    private static async Task<T[]> Parallel<T>(int workers, Func<int, Task<T>> work)
    {
        using var barrier = new Barrier(workers);
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(
            async () =>
            {
                barrier.SignalAndWait(TestContext.Current.CancellationToken);
                return await work(i);
            },
            TestContext.Current.CancellationToken));
        return await Task.WhenAll(tasks);
    }
}

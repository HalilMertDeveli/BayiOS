using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// Fulfilment races over real HTTP requests and parallel PostgreSQL connections (inventory-and-orders §5.2 C5/C6 and
/// the picking equivalents). Workers start together through a barrier; outcomes are exact and the ledger reconciles.
/// </summary>
public class FulfillmentConcurrencyTests(ApiFixture fixture)
{
    /// <summary>Two pickers claim the same pool task at once → exactly one owns it, the other gets 409.</summary>
    [Fact]
    public async Task Two_pickers_claiming_the_same_task_exactly_one_wins()
    {
        const int Rounds = 5;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, Rounds);
        var pickers = new[] { await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff), await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff) };

        for (var round = 0; round < Rounds; round++)
        {
            var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 1m));
            var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);

            var responses = await Parallel(2, i => Fulfillment.ClaimAsync(pickers[i], task.Task.Id));

            var winner = Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.True(winner >= 0, "nobody won the claim");
            await HttpAssert.ProblemAsync(responses[1 - winner], HttpStatusCode.Conflict, "picking.already_assigned");
            Assert.Equal(pickers[winner].UserId, (await Fulfillment.TaskAsync(kit.Owner, task.Task.Id)).Task.AssignedTo);
        }
    }

    /// <summary>10 parallel +1 scans on a line of 5 → exactly 5 counted, 5 rejected as over-pick; never more.</summary>
    [Fact]
    public async Task Parallel_scans_never_over_pick()
    {
        const int Scans = 10;
        const decimal Quantity = 5m;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, Quantity);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, Quantity));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);

        var responses = await Parallel(Scans, _ => Fulfillment.ScanAsync(kit.Owner, task.Task.Id, task.Lines[0].Sku));

        Assert.Equal((int)Quantity, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.ProblemAsync(rejected, HttpStatusCode.UnprocessableEntity, "picking.over_pick");
        }

        Assert.Equal(Quantity, (await Fulfillment.TaskAsync(kit.Owner, task.Task.Id)).Task.QuantityPicked);
    }

    /// <summary>C5: parallel confirmations of one shipment (distinct keys) → one shipment_out set, the rest 409.</summary>
    [Fact]
    public async Task Parallel_confirmations_of_one_shipment_issue_stock_exactly_once()
    {
        const int Workers = 6;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, 4m);
        var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);
        var completed = await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [approved.Lines[0].Id] = 2m });

        var responses = await Parallel(Workers, _ => Fulfillment.ConfirmAsync(kit.Owner, completed.ShipmentId!.Value));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.ProblemAsync(loser, HttpStatusCode.Conflict, "shipment.already_confirmed");
        }

        Assert.Equal(1L, await kit.CountAsync("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1 AND type = 'shipment_out'"));
        Assert.Equal((2m, 0m), await kit.BalanceAsync(variant));
        await kit.AssertConsistentAsync();
    }

    /// <summary>
    /// Cancel racing a pick scan: cancel always wins its transition; when the scan got in first the picked unit
    /// goes to a putaway task, otherwise the scan is refused. Reservations are released either way.
    /// </summary>
    [Fact]
    public async Task Cancel_racing_a_pick_is_always_consistent()
    {
        const int Rounds = 4;
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(variant, Rounds * 2);
        for (var round = 0; round < Rounds; round++)
        {
            var approved = await kit.ApprovedOrderAsync(OrdersKit.Line(variant, 2m));
            var task = await Fulfillment.ActiveTaskAsync(kit.Owner, approved.Order.Id);

            var responses = await Parallel(2, i => i == 0
                ? OrdersKit.ActAsync(kit.Owner, approved, "cancel", new { reason = "Yarış" }, ifMatch: false)
                : Fulfillment.ScanAsync(kit.Owner, task.Task.Id, task.Lines[0].Sku));

            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            var after = await Fulfillment.TaskAsync(kit.Owner, task.Task.Id);
            Assert.Equal("cancelled", after.Task.Status);
            var putaways = await Fulfillment.ReadAsync<PickingTaskPageResponse>(
                await kit.Owner.GetAsync($"/api/v1/picking-tasks?orderId={approved.Order.Id}&type=putaway"));
            if (responses[1].StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal((1m, 1m), (after.Task.QuantityPicked, Assert.Single(putaways.Items).QuantityToPick));
            }
            else
            {
                await HttpAssert.ProblemAsync(responses[1], HttpStatusCode.Conflict, "picking.invalid_transition");
                Assert.Equal(0m, after.Task.QuantityPicked);
                Assert.Empty(putaways.Items);
            }
        }

        Assert.Equal(0L, await kit.ActiveReservationsAsync());
        await kit.AssertConsistentAsync();
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

using BayiOS.Modules.Warehouse.Domain;

namespace BayiOS.UnitTests.Warehouse;

/// <summary>
/// The full transfer (OD-01, AF-07) and stock count (AF-06) transition tables: every (action, status) pair not listed
/// as legal is refused (null → 409 invalid_transition), terminal states have no way out.
/// </summary>
public class StockDocumentStateMachineTests
{
    private static readonly string[] TransferActions =
        [StockTransferActions.Dispatch, StockTransferActions.Receive, StockTransferActions.Close, StockTransferActions.Cancel];

    private static readonly string[] CountActions =
        [StockCountActions.Count, StockCountActions.Submit, StockCountActions.Recount, StockCountActions.Apply, StockCountActions.Cancel];

    private static readonly (string Action, string From, string To)[] LegalTransfer =
    [
        (StockTransferActions.Dispatch, StockTransferStatuses.Draft, StockTransferStatuses.InTransit),
        (StockTransferActions.Cancel, StockTransferStatuses.Draft, StockTransferStatuses.Cancelled),
        (StockTransferActions.Receive, StockTransferStatuses.InTransit, StockTransferStatuses.PartiallyReceived),
        (StockTransferActions.Receive, StockTransferStatuses.PartiallyReceived, StockTransferStatuses.PartiallyReceived),
        (StockTransferActions.Close, StockTransferStatuses.InTransit, StockTransferStatuses.Closed),
        (StockTransferActions.Close, StockTransferStatuses.PartiallyReceived, StockTransferStatuses.Closed),
    ];

    private static readonly (string Action, string From, string To)[] LegalCount =
    [
        (StockCountActions.Count, StockCountStatuses.Open, StockCountStatuses.Open),
        (StockCountActions.Submit, StockCountStatuses.Open, StockCountStatuses.Submitted),
        (StockCountActions.Recount, StockCountStatuses.Open, StockCountStatuses.Open),
        (StockCountActions.Recount, StockCountStatuses.Submitted, StockCountStatuses.Open),
        (StockCountActions.Apply, StockCountStatuses.Submitted, StockCountStatuses.Applied),
        (StockCountActions.Cancel, StockCountStatuses.Open, StockCountStatuses.Cancelled),
        (StockCountActions.Cancel, StockCountStatuses.Submitted, StockCountStatuses.Cancelled),
    ];

    [Fact]
    public void Transfer_transitions_are_exactly_the_legal_table()
    {
        foreach (var action in TransferActions)
        {
            foreach (var status in StockTransferStatuses.All)
            {
                var legal = LegalTransfer.Where(t => t.Action == action && t.From == status).Select(t => t.To).SingleOrDefault();
                Assert.Equal(legal, StockTransferStateMachine.Next(action, status, outstandingAfter: 1m));
            }
        }
    }

    [Theory]
    [InlineData(StockTransferStatuses.InTransit)]
    [InlineData(StockTransferStatuses.PartiallyReceived)]
    public void A_receipt_that_leaves_nothing_in_transit_completes_the_transfer(string status)
    {
        Assert.Equal(StockTransferStatuses.Received, StockTransferStateMachine.Next(StockTransferActions.Receive, status, outstandingAfter: 0m));
        Assert.Equal(StockTransferStatuses.PartiallyReceived, StockTransferStateMachine.Next(StockTransferActions.Receive, status, outstandingAfter: 0.001m));
    }

    [Theory]
    [InlineData(StockTransferStatuses.Received)]
    [InlineData(StockTransferStatuses.Closed)]
    [InlineData(StockTransferStatuses.Cancelled)]
    public void Terminal_transfers_accept_nothing(string status) =>
        Assert.All(TransferActions, action => Assert.Null(StockTransferStateMachine.Next(action, status)));

    [Fact]
    public void Only_a_draft_can_be_cancelled_dispatched_goods_are_closed_instead()
    {
        Assert.Equal([StockTransferStatuses.Draft], StockTransferStatuses.All.Where(s => StockTransferStateMachine.Next(StockTransferActions.Cancel, s) is not null));
        Assert.Equal(StockTransferStatuses.Open, StockTransferStatuses.All.Where(s => StockTransferStateMachine.Next(StockTransferActions.Close, s) is not null));
    }

    [Fact]
    public void Count_transitions_are_exactly_the_legal_table()
    {
        foreach (var action in CountActions)
        {
            foreach (var status in StockCountStatuses.All)
            {
                var legal = LegalCount.Where(t => t.Action == action && t.From == status).Select(t => t.To).SingleOrDefault();
                Assert.Equal(legal, StockCountStateMachine.Next(action, status));
            }
        }
    }

    [Theory]
    [InlineData(StockCountStatuses.Applied)]
    [InlineData(StockCountStatuses.Cancelled)]
    public void Terminal_counts_accept_nothing(string status) =>
        Assert.All(CountActions, action => Assert.Null(StockCountStateMachine.Next(action, status)));

    [Fact]
    public void A_line_is_never_fully_dispatched_and_still_outstanding()
    {
        var transfer = StockTransfer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var line = StockTransferLine.Create(transfer, 1, Guid.NewGuid(), "SKU-1", null, null, 10m);
        line.ReceivedQuantity = 6m;
        line.LostQuantity = 1m;
        line.ReturnedQuantity = 2m;
        Assert.Equal(1m, line.Outstanding);
        Assert.StartsWith("TRF-", transfer.Number, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StockCountScopes.Full, true, true)]
    [InlineData(StockCountScopes.Locations, true, false)]
    [InlineData(StockCountScopes.Variants, false, true)]
    public void Count_scope_decides_which_found_items_may_be_added(string scope, bool scopedShelfOtherVariant, bool otherShelfScopedVariant)
    {
        var shelf = Guid.NewGuid();
        var variant = Guid.NewGuid();
        var count = StockCount.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            scope,
            scope == StockCountScopes.Locations ? [shelf] : [],
            scope == StockCountScopes.Variants ? [variant] : [],
            isBlind: false,
            note: null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        Assert.True(count.Covers(variant, shelf));
        Assert.Equal(scopedShelfOtherVariant, count.Covers(Guid.NewGuid(), shelf));
        Assert.Equal(otherShelfScopedVariant, count.Covers(variant, Guid.NewGuid()));
    }
}

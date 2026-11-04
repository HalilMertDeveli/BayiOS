namespace BayiOS.Modules.Orders.Domain;

/// <summary>
/// The single place that decides order status transitions (inventory-and-orders §3.1). Every endpoint asks
/// <see cref="Next"/>; a null answer is <c>409 order.invalid_transition</c>. Terminal states (shipped is only left by
/// <c>complete</c>; completed and cancelled never) have no way out (I8).
/// <list type="table">
/// <item><term>submit</term><description>draft → pending_approval</description></item>
/// <item><term>withdraw / reject</term><description>pending_approval → draft</description></item>
/// <item><term>approve</term><description>pending_approval → approved (all-or-nothing reservation)</description></item>
/// <item><term>start_picking</term><description>approved → picking</description></item>
/// <item><term>pack</term><description>picking → packed</description></item>
/// <item><term>ship</term><description>picking / packed → shipped when every line is fully shipped or closed, else
/// unchanged with fulfillment <c>partial</c></description></item>
/// <item><term>close_remaining</term><description>picking / packed with fulfillment <c>partial</c> → shipped (short-close)</description></item>
/// <item><term>complete</term><description>shipped → completed</description></item>
/// <item><term>cancel</term><description>draft / pending_approval → cancelled; approved / picking / packed → cancelled only
/// while fulfillment is <c>none</c> (after a partial shipment only short-close is possible, I9)</description></item>
/// </list>
/// </summary>
internal static class OrderStateMachine
{
    /// <param name="shipsEverything">For <c>ship</c>: whether, after this shipment, nothing remains open.</param>
    /// <returns>The status after the action, or null when the action is not allowed from here.</returns>
    public static string? Next(string action, string status, string fulfillmentStatus, bool shipsEverything = false) => (action, status) switch
    {
        (OrderActions.Submit, OrderStatuses.Draft) => OrderStatuses.PendingApproval,
        (OrderActions.Withdraw or OrderActions.Reject, OrderStatuses.PendingApproval) => OrderStatuses.Draft,
        (OrderActions.Approve, OrderStatuses.PendingApproval) => OrderStatuses.Approved,
        (OrderActions.StartPicking, OrderStatuses.Approved) => OrderStatuses.Picking,
        (OrderActions.Pack, OrderStatuses.Picking) => OrderStatuses.Packed,
        (OrderActions.Ship, OrderStatuses.Picking or OrderStatuses.Packed) => shipsEverything ? OrderStatuses.Shipped : status,
        (OrderActions.CloseRemaining, OrderStatuses.Picking or OrderStatuses.Packed)
            when fulfillmentStatus == FulfillmentStatuses.Partial => OrderStatuses.Shipped,
        (OrderActions.Complete, OrderStatuses.Shipped) => OrderStatuses.Completed,
        (OrderActions.Cancel, OrderStatuses.Draft or OrderStatuses.PendingApproval) => OrderStatuses.Cancelled,
        (OrderActions.Cancel, OrderStatuses.Approved or OrderStatuses.Picking or OrderStatuses.Packed)
            when fulfillmentStatus == FulfillmentStatuses.None => OrderStatuses.Cancelled,
        _ => null,
    };

    /// <summary>Lines and header fields may change only in draft.</summary>
    public static bool IsEditable(string status) => status == OrderStatuses.Draft;

    /// <summary>Statuses in which the order holds stock reservations.</summary>
    public static bool HoldsReservations(string status) =>
        status is OrderStatuses.Approved or OrderStatuses.Picking or OrderStatuses.Packed;
}

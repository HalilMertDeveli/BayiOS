using BayiOS.Modules.Orders.Domain;

namespace BayiOS.UnitTests.Orders;

/// <summary>Order money rules (PRD ORD-03/04) and the complete transition table (inventory-and-orders §3.1, I8/I9).</summary>
public class OrderDomainTests
{
    // ---- Money ----

    [Theory]
    [InlineData("3", "12.345", null, null, "37.04", "0", "37.04")] // 37.035 → half away from zero
    [InlineData("3", "12.345", "10", null, "37.04", "3.70", "33.34")] // 3.704 → 3.70
    [InlineData("1", "0.125", null, "0.03", "0.13", "0.03", "0.10")]
    [InlineData("2.5", "19.99", "12.5", null, "49.98", "6.25", "43.73")] // 49.975 → 49.98; 6.2475 → 6.25
    [InlineData("0.001", "0.0049", null, null, "0.00", "0", "0.00")]
    [InlineData("7", "100", "100", null, "700", "700", "0")]
    public void Line_amounts_round_half_away_from_zero_to_two_decimals(
        string quantity, string unitPrice, string? percent, string? amount, string subtotal, string discount, string total)
    {
        var line = OrderMath.Line(D(quantity), D(unitPrice), N(percent), N(amount));

        Assert.NotNull(line);
        Assert.Equal((D(subtotal), D(discount), D(total)), (line.Subtotal, line.Discount, line.Total));
    }

    [Fact]
    public void Line_rejects_a_fixed_discount_above_the_subtotal_and_overflow()
    {
        Assert.Null(OrderMath.Line(1m, 10m, null, 10.01m));
        Assert.Null(OrderMath.Line(999_999_999_999_999m, 99_999_999_999_999m, null, null));
        Assert.Null(OrderMath.Line(10_000_000m, 10_000_000m, null, null)); // ≥ 10^14
    }

    [Fact]
    public void Vat_excluded_totals_round_per_rate_on_the_summed_base()
    {
        // Two lines of 0.05 at 10 %: per-line VAT would be 0.01 + 0.01; per rate it is round2(0.10 × 10 %) = 0.01.
        var totals = OrderMath.Totals([Priced(10m, 0.05m), Priced(10m, 0.05m), Priced(20m, 33.34m), Unpriced(20m)], pricesIncludeTax: false);

        Assert.Equal((33.44m, 0m, 6.68m, 33.44m, 40.12m), (totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, totals.NetTotal, totals.GrandTotal));
        Assert.Equal(
            [(10m, 0.10m, 0.01m, 0.11m), (20m, 33.34m, 6.67m, 40.01m)],
            totals.TaxBreakdown.Select(b => (b.Rate, b.TaxBase, b.Tax, b.Total)));
        Assert.Equal(totals.Subtotal - totals.DiscountTotal + totals.TaxTotal, totals.GrandTotal);
    }

    [Fact]
    public void Vat_included_totals_split_vat_out_and_keep_the_gross()
    {
        var totals = OrderMath.Totals([Priced(20m, 118m), Priced(20m, 30.03m), Priced(0m, 5m), Priced(10m, 10m, discount: 1m)], pricesIncludeTax: true);

        // 20 %: 148.03 → base round2(123.358…) = 123.36, VAT 24.67. 10 %: 10 → base 9.09, VAT 0.91. 0 %: 5 → 5, 0.
        Assert.Equal(
            [(0m, 5m, 0m, 5m), (10m, 9.09m, 0.91m, 10m), (20m, 123.36m, 24.67m, 148.03m)],
            totals.TaxBreakdown.Select(b => (b.Rate, b.TaxBase, b.Tax, b.Total)));
        Assert.Equal((164.03m, 1m, 25.58m, 137.45m, 163.03m), (totals.Subtotal, totals.DiscountTotal, totals.TaxTotal, totals.NetTotal, totals.GrandTotal));
        Assert.Equal(totals.Subtotal - totals.DiscountTotal, totals.GrandTotal);
        Assert.Equal(totals.NetTotal + totals.TaxTotal, totals.GrandTotal);
    }

    [Theory]
    [InlineData("118", "20", true, false, "98.3333")]
    [InlineData("98.3333", "20", false, true, "118")] // 117.99996 → 118.0000
    [InlineData("10", "8", false, true, "10.8")]
    [InlineData("10", "20", true, true, "10")]
    public void Unit_prices_convert_between_tax_modes_with_four_decimals(string price, string rate, bool from, bool to, string expected) =>
        Assert.Equal(D(expected), OrderMath.ConvertUnitPrice(D(price), D(rate), from, to));

    // ---- State machine ----

    /// <summary>Every (status, fulfillment) pair an order can actually be in.</summary>
    private static readonly (string Status, string Fulfillment)[] Reachable =
    [
        (OrderStatuses.Draft, FulfillmentStatuses.None),
        (OrderStatuses.PendingApproval, FulfillmentStatuses.None),
        (OrderStatuses.Approved, FulfillmentStatuses.None),
        (OrderStatuses.Picking, FulfillmentStatuses.None),
        (OrderStatuses.Picking, FulfillmentStatuses.Partial),
        (OrderStatuses.Packed, FulfillmentStatuses.None),
        (OrderStatuses.Packed, FulfillmentStatuses.Partial),
        (OrderStatuses.Shipped, FulfillmentStatuses.Partial),
        (OrderStatuses.Shipped, FulfillmentStatuses.Full),
        (OrderStatuses.Completed, FulfillmentStatuses.Partial),
        (OrderStatuses.Completed, FulfillmentStatuses.Full),
        (OrderStatuses.Cancelled, FulfillmentStatuses.None),
    ];

    private static readonly (string Action, string From, string Fulfillment, string To)[] Legal =
    {
        (OrderActions.Submit, OrderStatuses.Draft, FulfillmentStatuses.None, OrderStatuses.PendingApproval),
        (OrderActions.Withdraw, OrderStatuses.PendingApproval, FulfillmentStatuses.None, OrderStatuses.Draft),
        (OrderActions.Reject, OrderStatuses.PendingApproval, FulfillmentStatuses.None, OrderStatuses.Draft),
        (OrderActions.Approve, OrderStatuses.PendingApproval, FulfillmentStatuses.None, OrderStatuses.Approved),
        (OrderActions.StartPicking, OrderStatuses.Approved, FulfillmentStatuses.None, OrderStatuses.Picking),
        (OrderActions.Pack, OrderStatuses.Picking, FulfillmentStatuses.None, OrderStatuses.Packed),
        (OrderActions.Pack, OrderStatuses.Picking, FulfillmentStatuses.Partial, OrderStatuses.Packed),
        (OrderActions.Ship, OrderStatuses.Picking, FulfillmentStatuses.None, OrderStatuses.Picking),
        (OrderActions.Ship, OrderStatuses.Picking, FulfillmentStatuses.Partial, OrderStatuses.Picking),
        (OrderActions.Ship, OrderStatuses.Packed, FulfillmentStatuses.None, OrderStatuses.Packed),
        (OrderActions.Ship, OrderStatuses.Packed, FulfillmentStatuses.Partial, OrderStatuses.Packed),
        (OrderActions.CloseRemaining, OrderStatuses.Picking, FulfillmentStatuses.Partial, OrderStatuses.Shipped),
        (OrderActions.CloseRemaining, OrderStatuses.Packed, FulfillmentStatuses.Partial, OrderStatuses.Shipped),
        (OrderActions.ReportShortage, OrderStatuses.Approved, FulfillmentStatuses.None, OrderStatuses.Approved),
        (OrderActions.ReportShortage, OrderStatuses.Picking, FulfillmentStatuses.None, OrderStatuses.Picking),
        (OrderActions.ReportShortage, OrderStatuses.Picking, FulfillmentStatuses.Partial, OrderStatuses.Picking),
        (OrderActions.ReportShortage, OrderStatuses.Packed, FulfillmentStatuses.None, OrderStatuses.Packed),
        (OrderActions.ReportShortage, OrderStatuses.Packed, FulfillmentStatuses.Partial, OrderStatuses.Packed),
        (OrderActions.Complete, OrderStatuses.Shipped, FulfillmentStatuses.Full, OrderStatuses.Completed),
        (OrderActions.Complete, OrderStatuses.Shipped, FulfillmentStatuses.Partial, OrderStatuses.Completed),
        (OrderActions.Cancel, OrderStatuses.Draft, FulfillmentStatuses.None, OrderStatuses.Cancelled),
        (OrderActions.Cancel, OrderStatuses.PendingApproval, FulfillmentStatuses.None, OrderStatuses.Cancelled),
        (OrderActions.Cancel, OrderStatuses.Approved, FulfillmentStatuses.None, OrderStatuses.Cancelled),
        (OrderActions.Cancel, OrderStatuses.Picking, FulfillmentStatuses.None, OrderStatuses.Cancelled),
        (OrderActions.Cancel, OrderStatuses.Packed, FulfillmentStatuses.None, OrderStatuses.Cancelled),
    };

    [Fact]
    public void Exactly_the_documented_transitions_are_legal()
    {
        foreach (var action in OrderActions.Transitions)
        {
            foreach (var (status, fulfillment) in Reachable)
            {
                string? expected = Legal.FirstOrDefault(l => l.Action == action && l.From == status && l.Fulfillment == fulfillment).To;
                var actual = OrderStateMachine.Next(action, status, fulfillment);
                Assert.True(expected == actual, $"{action} from {status}/{fulfillment}: expected {expected ?? "illegal"}, got {actual ?? "illegal"}");
            }
        }
    }

    [Theory]
    [InlineData(OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Packed)]
    public void A_shipment_that_leaves_nothing_open_ships_the_order(string status) =>
        Assert.Equal(OrderStatuses.Shipped, OrderStateMachine.Next(OrderActions.Ship, status, FulfillmentStatuses.Partial, shipsEverything: true));

    [Fact]
    public void Terminal_states_have_no_way_out_and_partial_shipments_cannot_be_cancelled()
    {
        foreach (var action in OrderActions.Transitions)
        {
            Assert.Null(OrderStateMachine.Next(action, OrderStatuses.Cancelled, FulfillmentStatuses.None));
            Assert.Null(OrderStateMachine.Next(action, OrderStatuses.Completed, FulfillmentStatuses.Full));
        }

        Assert.Null(OrderStateMachine.Next(OrderActions.Cancel, OrderStatuses.Picking, FulfillmentStatuses.Partial));
        Assert.Null(OrderStateMachine.Next(OrderActions.Cancel, OrderStatuses.Shipped, FulfillmentStatuses.Full));
        Assert.Null(OrderStateMachine.Next(OrderActions.Ship, OrderStatuses.Approved, FulfillmentStatuses.None));
        Assert.True(OrderStateMachine.IsEditable(OrderStatuses.Draft));
        Assert.All(OrderStatuses.All.Where(s => s != OrderStatuses.Draft), s => Assert.False(OrderStateMachine.IsEditable(s)));
    }

    private static decimal D(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal? N(string? value) => value is null ? null : D(value);

    private static Line Priced(decimal rate, decimal total, decimal discount = 0m) => new(rate, total + discount, discount, total);

    private static Line Unpriced(decimal rate) => new(rate, null, null, null);

    private sealed record Line(decimal TaxRate, decimal? LineSubtotal, decimal? LineDiscount, decimal? LineTotal) : IPricedLine;
}

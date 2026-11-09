namespace BayiOS.Modules.Orders.Domain;

/// <summary>The priced part of an order line, as the totals calculation sees it.</summary>
internal interface IPricedLine
{
    decimal TaxRate { get; }

    decimal? LineSubtotal { get; }

    decimal? LineDiscount { get; }

    decimal? LineTotal { get; }
}

internal sealed record LineAmounts(decimal Subtotal, decimal Discount, decimal Total);

/// <param name="TaxBase">Net of VAT for this rate.</param>
/// <param name="Total">Including VAT for this rate.</param>
internal sealed record TaxBucket(decimal Rate, decimal TaxBase, decimal Tax, decimal Total);

/// <param name="Subtotal">Σ line subtotals, in the order's tax mode.</param>
/// <param name="DiscountTotal">Σ line discounts, in the order's tax mode.</param>
/// <param name="NetTotal">Σ tax bases (excluding VAT).</param>
/// <param name="GrandTotal">Amount payable (including VAT).</param>
internal sealed record OrderTotals(
    decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal NetTotal, decimal GrandTotal, IReadOnlyList<TaxBucket> TaxBreakdown);

/// <summary>
/// Money rules for orders (PRD ORD-03/04; documented in inventory-and-orders §6.1). All amounts are <c>decimal</c>;
/// "round2" is 2 decimals, half away from zero (standard "yarım yukarı", not banker's rounding).
/// <list type="number">
/// <item>Line subtotal = round2(quantity × unit price).</item>
/// <item>Line discount = round2(subtotal × percent / 100), or the fixed amount (≤ subtotal); one type per line.</item>
/// <item>Line total = subtotal − discount, in the order's tax mode (VAT included or excluded).</item>
/// <item>VAT is computed per rate on the summed line totals (one rounding per rate, matching the per-rate VAT
/// breakdown): excluded → tax = round2(base × rate / 100); included → base = round2(total × 100 / (100 + rate)),
/// tax = total − base.</item>
/// <item>Order: subtotal = Σ subtotals, discount = Σ discounts, tax = Σ per-rate tax, grand total = Σ per-rate totals
/// incl. VAT (excluded mode: subtotal − discount + tax; included mode: subtotal − discount).</item>
/// <item>A list price in the other tax mode is converted with 4-decimal rounding: price × (100 + rate) / 100 or
/// price × 100 / (100 + rate).</item>
/// </list>
/// Unpriced lines (no price resolved) contribute nothing; an order with such lines cannot be submitted or approved.
/// </summary>
internal static class OrderMath
{
    /// <summary>numeric(18,4) holds values below 10^14.</summary>
    public const decimal MaxAmount = 100_000_000_000_000m;

    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>Converts a unit price between VAT-included and VAT-excluded modes (4 decimals).</summary>
    public static decimal ConvertUnitPrice(decimal unitPrice, decimal taxRate, bool fromIncludesTax, bool toIncludesTax)
    {
        if (fromIncludesTax == toIncludesTax)
        {
            return unitPrice;
        }

        return toIncludesTax
            ? Round4(unitPrice * (100m + taxRate) / 100m)
            : Round4(unitPrice * 100m / (100m + taxRate));
    }

    /// <summary>Line amounts, or null when they overflow or the fixed discount exceeds the subtotal.</summary>
    public static LineAmounts? Line(decimal quantity, decimal unitPrice, decimal? discountPercent, decimal? discountAmount)
    {
        try
        {
            var subtotal = Round2(quantity * unitPrice);
            var discount = discountPercent is { } percent
                ? Round2(subtotal * percent / 100m)
                : discountAmount ?? 0m;
            if (subtotal >= MaxAmount || discount > subtotal)
            {
                return null;
            }

            return new LineAmounts(subtotal, discount, subtotal - discount);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static OrderTotals Totals(IEnumerable<IPricedLine> lines, bool pricesIncludeTax)
    {
        var priced = lines.Where(l => l.LineTotal is not null).ToList();
        var buckets = priced
            .GroupBy(l => l.TaxRate)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var rate = g.Key;
                var sum = g.Sum(l => l.LineTotal!.Value);
                if (pricesIncludeTax)
                {
                    var taxBase = Round2(sum * 100m / (100m + rate));
                    return new TaxBucket(rate, taxBase, sum - taxBase, sum);
                }

                var tax = Round2(sum * rate / 100m);
                return new TaxBucket(rate, sum, tax, sum + tax);
            })
            .ToList();

        return new OrderTotals(
            priced.Sum(l => l.LineSubtotal!.Value),
            priced.Sum(l => l.LineDiscount!.Value),
            buckets.Sum(b => b.Tax),
            buckets.Sum(b => b.TaxBase),
            buckets.Sum(b => b.Total),
            buckets);
    }
}

using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Domain;

namespace BayiOS.Modules.Pricing.Application;

/// <summary>A price list in the resolution order, labelled with the assignment it came from (<see cref="PriceSources"/>).</summary>
internal sealed record PriceCandidate(string Source, PriceList List);

/// <summary>
/// The pure resolution rule (see <see cref="IPriceResolver"/>): no I/O, so it is unit-testable and Orders gets the same
/// answer as the quote endpoint for the same data.
/// </summary>
internal static class PriceResolution
{
    /// <param name="candidates">Customer list, dealer group list, tenant default list — in that order, any may be absent.</param>
    /// <param name="lists">Every loaded list by id (candidates and their base lists).</param>
    /// <param name="items">Items of the loaded lists for the requested variants.</param>
    public static ResolvedPrice Resolve(
        PricedVariantInfo variant,
        decimal quantity,
        string currency,
        DateOnly date,
        IReadOnlyList<PriceCandidate> candidates,
        IReadOnlyDictionary<Guid, PriceList> lists,
        ILookup<(Guid PriceListId, Guid VariantId), PriceListItem> items)
    {
        foreach (var (source, list) in candidates)
        {
            if (!IsEligible(list, currency, date))
            {
                continue;
            }

            if (BestTier(items, list.Id, variant.Id, quantity) is { } item)
            {
                return Priced(variant, quantity, source, PriceRules.Item, list, item, item.UnitPrice, basePrice: null, discount: null);
            }

            if (list.BasePriceListId is { } baseId
                && list.DiscountPercent is { } discount
                && lists.TryGetValue(baseId, out var baseList)
                && IsEligible(baseList, currency, date)
                && BestTier(items, baseId, variant.Id, quantity) is { } baseItem)
            {
                var price = PricingNumbers.ApplyDiscount(baseItem.UnitPrice, discount);
                return Priced(variant, quantity, source, PriceRules.BaseDiscount, list, baseItem, price, baseItem.UnitPrice, discount);
            }
        }

        return new ResolvedPrice(
            variant.Id, variant.Sku, quantity, PriceStatuses.NoPrice, null, null, null, null, null, null, null, null, null, null, null, variant.TaxRate);
    }

    /// <summary>Only active sales lists in the requested currency whose validity window contains the date.</summary>
    public static bool IsEligible(PriceList list, string currency, DateOnly date) =>
        list.Kind == PriceListKinds.Sales && list.Currency == currency && list.IsValidOn(date);

    /// <summary>The item with the largest <c>min_quantity ≤ quantity</c>; null when every tier starts above it.</summary>
    public static PriceListItem? BestTier(ILookup<(Guid, Guid), PriceListItem> items, Guid priceListId, Guid variantId, decimal quantity) =>
        items[(priceListId, variantId)].Where(i => i.MinQuantity <= quantity).MaxBy(i => i.MinQuantity);

    private static ResolvedPrice Priced(
        PricedVariantInfo variant,
        decimal quantity,
        string source,
        string rule,
        PriceList list,
        PriceListItem item,
        decimal unitPrice,
        decimal? basePrice,
        decimal? discount) =>
        new(
            variant.Id,
            variant.Sku,
            quantity,
            PriceStatuses.Priced,
            unitPrice,
            source,
            rule,
            list.Id,
            list.Name,
            item.Id,
            item.PriceListId,
            item.MinQuantity,
            basePrice,
            discount,
            list.PricesIncludeTax,
            variant.TaxRate);
}

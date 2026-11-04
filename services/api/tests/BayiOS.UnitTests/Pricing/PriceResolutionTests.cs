using BayiOS.Modules.Pricing.Application;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Domain;

namespace BayiOS.UnitTests.Pricing;

/// <summary>The pure resolution rule (#78): order, quantity tiers, base-list discount, eligibility, explicit no-price.</summary>
public class PriceResolutionTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 5);
    private static readonly PricedVariantInfo Balata = new(Guid.CreateVersion7(), "FRN-CG125-ON", true, 20m);

    [Fact]
    public void Best_tier_is_the_largest_min_quantity_not_above_the_quantity()
    {
        var list = List("Perakende");
        var items = Items(Item(list, 1m, 100m), Item(list, 10m, 90m), Item(list, 50m, 80m));

        Assert.Equal(100m, Resolve(9.999m, [new(PriceSources.DefaultPriceList, list)], items, list).UnitPrice);
        Assert.Equal(90m, Resolve(10m, [new(PriceSources.DefaultPriceList, list)], items, list).UnitPrice);
        Assert.Equal(80m, Resolve(500m, [new(PriceSources.DefaultPriceList, list)], items, list).UnitPrice);
        var below = Resolve(0.5m, [new(PriceSources.DefaultPriceList, list)], items, list);
        Assert.Equal((PriceStatuses.NoPrice, (decimal?)null), (below.Status, below.UnitPrice));
    }

    [Fact]
    public void Customer_list_wins_over_group_list_which_wins_over_default()
    {
        var customer = List("Özel");
        var group = List("Bayi A");
        var fallback = List("Perakende");
        var items = Items(Item(customer, 1m, 70m), Item(group, 1m, 80m), Item(fallback, 1m, 100m));
        PriceCandidate[] candidates =
        [
            new(PriceSources.CustomerPriceList, customer), new(PriceSources.DealerGroupPriceList, group), new(PriceSources.DefaultPriceList, fallback),
        ];

        var price = Resolve(1m, candidates, items, customer, group, fallback);

        Assert.Equal((70m, PriceSources.CustomerPriceList, customer.Id), (price.UnitPrice!.Value, price.Source, price.PriceListId!.Value));
        Assert.Equal(80m, Resolve(1m, candidates[1..], items, group, fallback).UnitPrice);
    }

    [Fact]
    public void A_list_without_the_item_falls_through_to_the_next_source()
    {
        var group = List("Bayi A");
        var fallback = List("Perakende");
        var items = Items(Item(fallback, 1m, 100m));

        var price = Resolve(1m, [new(PriceSources.DealerGroupPriceList, group), new(PriceSources.DefaultPriceList, fallback)], items, group, fallback);

        Assert.Equal((100m, PriceSources.DefaultPriceList, PriceRules.Item), (price.UnitPrice!.Value, price.Source, price.Rule));
    }

    [Fact]
    public void Derived_list_applies_its_discount_to_the_base_item_and_own_items_override_it()
    {
        var retail = List("Perakende");
        var dealer = List("Bayi B", baseList: retail, discount: 12.5m);
        var other = Guid.CreateVersion7();
        var items = Items(
            Item(retail, 1m, 1234.5678m),
            Item(retail, 1m, 50m, other),
            Item(dealer, 1m, 40m, other));
        PriceCandidate[] candidates = [new(PriceSources.DealerGroupPriceList, dealer)];

        var derived = Resolve(1m, candidates, items, dealer, retail);
        // 1234.5678 × 0.875 = 1080.246825 → 1080.2468 (half away from zero, 4 decimals).
        Assert.Equal(
            (1080.2468m, PriceRules.BaseDiscount, retail.Id, 1234.5678m, 12.5m, dealer.Id),
            (derived.UnitPrice!.Value, derived.Rule, derived.ItemPriceListId!.Value, derived.BaseUnitPrice!.Value, derived.DiscountPercent!.Value, derived.PriceListId!.Value));

        var own = PriceResolution.Resolve(Balata with { Id = other }, 1m, "TRY", Today, candidates, Lists(dealer, retail), items);
        Assert.Equal((40m, PriceRules.Item), (own.UnitPrice!.Value, own.Rule));
    }

    [Theory]
    [InlineData(0.00005, 0, 0.0001)]
    [InlineData(0.00015, 0, 0.0002)]
    [InlineData(10, 33.33, 6.667)]
    [InlineData(99.99, 100, 0)]
    public void Discount_rounds_half_away_from_zero_to_four_decimals(double basePrice, double percent, double expected) =>
        Assert.Equal((decimal)expected, PricingNumbers.ApplyDiscount((decimal)basePrice, (decimal)percent));

    [Fact]
    public void Inactive_out_of_window_or_foreign_currency_lists_are_skipped()
    {
        var inactive = List("Pasif");
        inactive.SetStatus(PriceListStatuses.Inactive, Now);
        var expired = List("Eski", validTo: Today.AddDays(-1));
        var future = List("Gelecek", validFrom: Today.AddDays(1));
        var euro = List("Euro", currency: "EUR");
        var fallback = List("Perakende", validFrom: Today, validTo: Today);
        var items = Items(Item(inactive, 1m, 1m), Item(expired, 1m, 2m), Item(future, 1m, 3m), Item(euro, 1m, 4m), Item(fallback, 1m, 5m));
        PriceCandidate[] candidates =
        [
            new(PriceSources.CustomerPriceList, inactive), new(PriceSources.CustomerPriceList, expired),
            new(PriceSources.DealerGroupPriceList, future), new(PriceSources.DealerGroupPriceList, euro), new(PriceSources.DefaultPriceList, fallback),
        ];

        Assert.Equal(5m, Resolve(1m, candidates, items, inactive, expired, future, euro, fallback).UnitPrice);
    }

    [Fact]
    public void Derived_list_with_an_inactive_base_yields_no_price_rather_than_zero()
    {
        var retail = List("Perakende");
        retail.SetStatus(PriceListStatuses.Inactive, Now);
        var dealer = List("Bayi B", baseList: retail, discount: 10m);

        var price = Resolve(1m, [new(PriceSources.DealerGroupPriceList, dealer)], Items(Item(retail, 1m, 100m)), dealer, retail);

        Assert.Equal(PriceStatuses.NoPrice, price.Status);
        Assert.Null(price.UnitPrice);
        Assert.Null(price.Source);
        Assert.Equal(20m, price.TaxRate);
    }

    private static ResolvedPrice Resolve(
        decimal quantity, PriceCandidate[] candidates, ILookup<(Guid, Guid), PriceListItem> items, params PriceList[] lists) =>
        PriceResolution.Resolve(Balata, quantity, "TRY", Today, candidates, Lists(lists), items);

    private static Dictionary<Guid, PriceList> Lists(params PriceList[] lists) => lists.ToDictionary(l => l.Id);

    private static ILookup<(Guid, Guid), PriceListItem> Items(params PriceListItem[] items) =>
        items.ToLookup(i => (i.PriceListId, i.VariantId));

    private static PriceListItem Item(PriceList list, decimal minQuantity, decimal unitPrice, Guid? variantId = null) =>
        PriceListItem.Create(Tenant, list.Id, variantId ?? Balata.Id, minQuantity, unitPrice, User, Now);

    private static PriceList List(
        string name, PriceList? baseList = null, decimal? discount = null, DateOnly? validFrom = null, DateOnly? validTo = null, string currency = "TRY") =>
        PriceList.Create(Tenant, name, PriceListKinds.Sales, currency, false, validFrom, validTo, baseList?.Id, discount, User, Now);
}

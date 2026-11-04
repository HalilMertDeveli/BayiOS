using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Pricing.Domain;

internal static class PriceListKinds
{
    public const string Sales = "sales";
    public const string Purchase = "purchase";

    public static readonly IReadOnlyList<string> All = [Sales, Purchase];
}

internal static class PriceListStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Active, Inactive, Archived];
}

/// <summary>Shared columns (data-model §1): tenant, audit stamps, optimistic concurrency (ETag source).</summary>
internal abstract class PricingEntity : ITenantOwned
{
    public Guid Id { get; protected init; }

    public Guid TenantId { get; protected init; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; protected init; }

    public Guid? CreatedBy { get; protected init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Touch(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>
/// pricing.price_lists (data-model §5.4 + §5.13): a named list in one currency. A sales list may derive from a base
/// list with a percentage discount (CAT-08); its own items override the rule. At most one default sales list per
/// tenant and currency.
/// </summary>
internal sealed class PriceList : PricingEntity
{
    private PriceList()
    {
    }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; private init; } = PriceListKinds.Sales;

    /// <summary>ISO 4217, immutable: items carry no currency of their own.</summary>
    public string Currency { get; private init; } = "TRY";

    public bool IsDefault { get; set; }

    public bool PricesIncludeTax { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public Guid? BasePriceListId { get; set; }

    /// <summary>0–100, two decimals; set exactly when <see cref="BasePriceListId"/> is set.</summary>
    public decimal? DiscountPercent { get; set; }

    public string Status { get; private set; } = PriceListStatuses.Active;

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsValidOn(DateOnly date) =>
        Status == PriceListStatuses.Active && (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || date <= ValidTo);

    public void SetStatus(string status, DateTimeOffset now)
    {
        Status = status;
        ArchivedAt = status == PriceListStatuses.Archived ? ArchivedAt ?? now : null;
    }

    public static PriceList Create(
        Guid tenantId,
        string name,
        string kind,
        string currency,
        bool pricesIncludeTax,
        DateOnly? validFrom,
        DateOnly? validTo,
        Guid? basePriceListId,
        decimal? discountPercent,
        Guid userId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            Kind = kind,
            Currency = currency,
            PricesIncludeTax = pricesIncludeTax,
            ValidFrom = validFrom,
            ValidTo = validTo,
            BasePriceListId = basePriceListId,
            DiscountPercent = discountPercent,
            CreatedAt = now,
            CreatedBy = userId,
        };
}

/// <summary>pricing.price_list_items: unit price of a Catalog variant from a quantity tier on (no cross-module FK).</summary>
internal sealed class PriceListItem : PricingEntity
{
    private PriceListItem()
    {
    }

    public Guid PriceListId { get; private init; }

    public Guid VariantId { get; private init; }

    /// <summary>Tier start, numeric(18,3) &gt; 0. The best tier is the largest one ≤ the ordered quantity.</summary>
    public decimal MinQuantity { get; private init; } = 1m;

    /// <summary>numeric(18,4) ≥ 0, in the list's currency.</summary>
    public decimal UnitPrice { get; set; }

    public static PriceListItem Create(
        Guid tenantId, Guid priceListId, Guid variantId, decimal minQuantity, decimal unitPrice, Guid userId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PriceListId = priceListId,
            VariantId = variantId,
            MinQuantity = minQuantity,
            UnitPrice = unitPrice,
            CreatedAt = now,
            CreatedBy = userId,
        };
}

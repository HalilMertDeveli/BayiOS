namespace BayiOS.Modules.Pricing.Contracts;

/// <summary>Keyset page metadata (api-strategy §5.1). <c>nextCursor</c> is opaque to clients.</summary>
public sealed record PricingPageInfo(string? NextCursor, bool HasMore, int Limit);

public sealed record PriceListResponse(
    Guid Id,
    string Name,
    string Kind,
    string Currency,
    bool IsDefault,
    bool PricesIncludeTax,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    Guid? BasePriceListId,
    decimal? DiscountPercent,
    string Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Unpaged reference list (≤ 1000, api-strategy §5.1), default first then by name.</summary>
public sealed record PriceListListResponse(IReadOnlyList<PriceListResponse> Items);

/// <param name="Name">1–100 characters, unique per tenant among lists that are not archived.</param>
/// <param name="Kind">sales (default) or purchase. Purchase lists are visible only with <c>prices.read.cost</c>.</param>
/// <param name="Currency">ISO 4217 (e.g. TRY); default the tenant currency. Immutable.</param>
/// <param name="IsDefault">Sales lists only: the tenant default list for its currency. The first sales list of a currency becomes default.</param>
/// <param name="PricesIncludeTax">Whether the list's prices include VAT. Default false.</param>
/// <param name="BasePriceListId">Derive from this sales list (same currency and VAT mode, itself not derived).</param>
/// <param name="DiscountPercent">0–100 with ≤ 2 decimals; required exactly when <paramref name="BasePriceListId"/> is set.</param>
public sealed record CreatePriceListRequest(
    string? Name,
    string? Kind,
    string? Currency,
    bool? IsDefault,
    bool? PricesIncludeTax,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    Guid? BasePriceListId,
    decimal? DiscountPercent);

/// <summary>JSON merge patch; <c>null</c> clears validFrom/validTo/basePriceListId/discountPercent.</summary>
/// <param name="Status">active, inactive or archived. The default list must stay active.</param>
/// <param name="IsDefault">Only <c>true</c>: makes this the default sales list of its currency.</param>
public sealed record UpdatePriceListRequest(
    string? Name,
    bool? IsDefault,
    bool? PricesIncludeTax,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    Guid? BasePriceListId,
    decimal? DiscountPercent,
    string? Status);

public sealed record PriceListItemResponse(
    Guid Id,
    Guid PriceListId,
    Guid VariantId,
    decimal MinQuantity,
    decimal UnitPrice,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record PriceListItemPageResponse(IReadOnlyList<PriceListItemResponse> Items, PricingPageInfo Page);

/// <summary>Bulk upsert (≤ 500): each (variantId, minQuantity) is inserted or its unit price replaced.</summary>
public sealed record UpsertPriceListItemsRequest(IReadOnlyList<PriceListItemInput>? Items);

/// <param name="MinQuantity">Tier start &gt; 0 with ≤ 3 decimals; default 1.</param>
/// <param name="UnitPrice">≥ 0 with ≤ 4 decimals, in the list currency.</param>
public sealed record PriceListItemInput(Guid? VariantId, decimal? MinQuantity, decimal? UnitPrice);

public sealed record UpsertPriceListItemsResponse(int Created, int Updated, IReadOnlyList<PriceListItemResponse> Items);

/// <param name="CustomerId">Optional; without it only the tenant default list applies.</param>
/// <param name="Date">Price date (yyyy-MM-dd); default today (Europe/Istanbul).</param>
/// <param name="Currency">Optional; default the customer's, else the tenant's currency.</param>
/// <param name="Lines">1–500 lines.</param>
public sealed record PriceQuoteApiRequest(Guid? CustomerId, DateOnly? Date, string? Currency, IReadOnlyList<PriceQuoteApiLine>? Lines);

public sealed record PriceQuoteApiLine(Guid? VariantId, decimal? Quantity);

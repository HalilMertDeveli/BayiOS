namespace BayiOS.Modules.Pricing.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Public pricing contract (domain-model §4 PriceResolver). Orders, the quote endpoint and (later) the WhatsApp
// assistant call it inside their own request UnitOfWork; it only reads, on the shared connection and transaction.
// ------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Resolves sales unit prices for a customer. Deterministic order per line — the first step that yields a price wins:
/// <list type="number">
/// <item>The customer's own price list (<c>customers.price_list_id</c>, CUS-03), then</item>
/// <item>the customer's dealer group list (<c>dealer_groups.price_list_id</c>, CUS-02), then</item>
/// <item>the tenant's default sales list for the currency.</item>
/// </list>
/// Within each list: the item with the largest <c>min_quantity ≤ quantity</c> (rule <c>item</c>); otherwise, if the
/// list derives from a base list, that base list's best item minus the list's discount percent, rounded half away
/// from zero to 4 decimals (rule <c>base_discount</c>). Lists that are not active, not valid on the date, not
/// <c>sales</c> or in another currency are skipped. When no step yields a price the line is <c>no_price</c> with a
/// null unit price — never 0. Prices are <c>decimal</c> end to end (numeric(18,4) in the database).
/// </summary>
public interface IPriceResolver
{
    Task<PricingResult<PriceQuote>> QuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken);
}

/// <summary>What other modules may ask about price lists (e.g. Customers validating an assignment).</summary>
public interface IPriceListDirectory
{
    Task<PriceListInfo?> FindAsync(Guid priceListId, CancellationToken cancellationToken);
}

public sealed record PriceListInfo(Guid Id, string Name, string Kind, string Currency, string Status)
{
    /// <summary>Assignable to customers and dealer groups: a sales list that is not archived.</summary>
    public bool IsAssignable => Kind == "sales" && Status != "archived";
}

/// <param name="CustomerId">Null = no customer: only the tenant default list applies.</param>
/// <param name="Date">Price date (validity windows are checked against it), normally the order date.</param>
/// <param name="Lines">1–500 lines; quantities &gt; 0 with at most 3 decimals, in the variant's base unit.</param>
/// <param name="Currency">Null = the customer's currency, else the tenant default currency.</param>
public sealed record PriceQuoteRequest(Guid? CustomerId, DateOnly Date, IReadOnlyList<PriceQuoteLine> Lines, string? Currency = null);

public sealed record PriceQuoteLine(Guid VariantId, decimal Quantity);

/// <param name="DealerGroupDiscountPercent">
/// The customer's dealer group default line discount. Informational: it is <b>not</b> applied to unit prices;
/// Orders may pre-fill the line discount with it.
/// </param>
public sealed record PriceQuote(
    Guid? CustomerId,
    string Currency,
    DateOnly Date,
    decimal? DealerGroupDiscountPercent,
    IReadOnlyList<ResolvedPrice> Lines);

/// <summary>One resolved line, in request order.</summary>
/// <param name="Status"><see cref="PriceStatuses"/>: <c>priced</c> or <c>no_price</c>.</param>
/// <param name="UnitPrice">Null exactly when <paramref name="Status"/> is <c>no_price</c>.</param>
/// <param name="Source"><see cref="PriceSources"/>: which assignment produced the price.</param>
/// <param name="Rule"><see cref="PriceRules"/>: <c>item</c> or <c>base_discount</c>.</param>
/// <param name="PriceListId">The list whose rule produced the price (the customer/group/default list).</param>
/// <param name="ItemPriceListId">The list holding the item that supplied the amount (the base list for <c>base_discount</c>).</param>
/// <param name="MinQuantity">Quantity tier of that item.</param>
/// <param name="BaseUnitPrice">Base list amount before the discount (<c>base_discount</c> only).</param>
/// <param name="DiscountPercent">Discount applied to <paramref name="BaseUnitPrice"/> (<c>base_discount</c> only).</param>
/// <param name="PricesIncludeTax">Whether <paramref name="UnitPrice"/> includes VAT (the list's setting).</param>
/// <param name="TaxRate">VAT % of the variant's product (Catalog), independent of the price.</param>
public sealed record ResolvedPrice(
    Guid VariantId,
    string Sku,
    decimal Quantity,
    string Status,
    decimal? UnitPrice,
    string? Source,
    string? Rule,
    Guid? PriceListId,
    string? PriceListName,
    Guid? PriceListItemId,
    Guid? ItemPriceListId,
    decimal? MinQuantity,
    decimal? BaseUnitPrice,
    decimal? DiscountPercent,
    bool? PricesIncludeTax,
    decimal TaxRate);

public static class PriceStatuses
{
    public const string Priced = "priced";
    public const string NoPrice = "no_price";
}

public static class PriceSources
{
    public const string CustomerPriceList = "customer_price_list";
    public const string DealerGroupPriceList = "dealer_group_price_list";
    public const string DefaultPriceList = "default_price_list";
}

public static class PriceRules
{
    public const string Item = "item";
    public const string BaseDiscount = "base_discount";
}

public sealed record PricingResult<T>(T? Value, PricingError? Error)
{
    public bool Succeeded => Error is null;

    public static implicit operator PricingResult<T>(T value) => new(value, null);

    public static implicit operator PricingResult<T>(PricingError error) => new(default, error);
}

/// <summary>A stable Problem Details code (api-strategy §6).</summary>
public sealed record PricingError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null)
{
    public static PricingError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);
}

// ------------------------------------------------------------------------------------------------------------------
// Ports: Pricing references no other module. Customers implements ICustomerPricingDirectory; the host wires the
// Catalog (variants) and Identity (tenant currency) adapters. All run on the request's UnitOfWork connection.
// ------------------------------------------------------------------------------------------------------------------

/// <param name="Currency">The customer's currency; null = tenant default.</param>
public sealed record CustomerPricingProfile(
    Guid CustomerId,
    string Status,
    string? Currency,
    Guid? PriceListId,
    Guid? DealerGroupId,
    Guid? DealerGroupPriceListId,
    decimal? DealerGroupDiscountPercent);

/// <summary>Implemented by the Customers module.</summary>
public interface ICustomerPricingDirectory
{
    Task<CustomerPricingProfile?> FindAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed record PricedVariantInfo(Guid Id, string Sku, bool IsActive, decimal TaxRate);

/// <summary>Implemented over the Catalog module (variant + its product's VAT rate).</summary>
public interface IPricingVariantDirectory
{
    Task<IReadOnlyDictionary<Guid, PricedVariantInfo>> FindVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <summary>Implemented over Identity: the tenant's default currency (TEN-05, 'TRY' unless changed).</summary>
public interface ITenantCurrencyProvider
{
    Task<string> GetDefaultCurrencyAsync(CancellationToken cancellationToken);
}

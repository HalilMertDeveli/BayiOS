namespace BayiOS.Modules.Customers.Contracts;

/// <summary>Keyset page metadata (api-strategy §5.1). <c>nextCursor</c> is opaque to clients.</summary>
public sealed record CustomerPageInfo(string? NextCursor, bool HasMore, int Limit);

// ---- Dealer groups ----

public sealed record DealerGroupResponse(
    Guid Id,
    string Name,
    Guid? PriceListId,
    decimal? DefaultDiscountPercent,
    string Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Unpaged reference list (≤ 1000), by name.</summary>
public sealed record DealerGroupListResponse(IReadOnlyList<DealerGroupResponse> Items);

/// <param name="Name">1–100 characters, unique per tenant (case-insensitive) among non-archived groups.</param>
/// <param name="PriceListId">A sales price list; assigning one also needs <c>pricelists.assign</c>.</param>
/// <param name="DefaultDiscountPercent">Suggested line discount 0–100 (≤ 2 decimals); not applied to resolved prices.</param>
public sealed record CreateDealerGroupRequest(string? Name, Guid? PriceListId, decimal? DefaultDiscountPercent);

/// <summary>JSON merge patch; <c>null</c> clears priceListId/defaultDiscountPercent.</summary>
/// <param name="Status">active or archived.</param>
public sealed record UpdateDealerGroupRequest(string? Name, Guid? PriceListId, decimal? DefaultDiscountPercent, string? Status);

// ---- Customers ----

/// <param name="TaxNumber">VKN (10 digits) or TCKN (11 digits). In list responses an 11-digit number is masked (data-policy §2).</param>
/// <param name="AnonymizedAt">Set when the personal fields were erased on a data-subject request.</param>
public sealed record CustomerResponse(
    Guid Id,
    string Code,
    string Name,
    string? ContactName,
    string? TaxNumber,
    string? TaxOffice,
    string? Phone,
    string? Email,
    IReadOnlyList<string> WhatsappNumbers,
    Guid? DealerGroupId,
    Guid? PriceListId,
    int? PaymentTermDays,
    decimal? CreditLimit,
    string? Currency,
    string? Notes,
    string Status,
    DateTimeOffset? AnonymizedAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CustomerDetailResponse(CustomerResponse Customer, IReadOnlyList<AddressResponse> Addresses);

public sealed record CustomerPageResponse(IReadOnlyList<CustomerResponse> Items, CustomerPageInfo Page);

/// <param name="Code">Account code (cari kodu): 1–30 characters without whitespace, stored upper-case, unique per tenant.</param>
/// <param name="Name">Trade name (ünvan), 1–200 characters.</param>
/// <param name="TaxNumber">Optional VKN (10) / TCKN (11) digits.</param>
/// <param name="WhatsappNumbers">Up to 5 E.164 numbers (e.g. +905321234567) for sender matching.</param>
/// <param name="DealerGroupId">Dealer/price group; assigning needs <c>pricelists.assign</c>.</param>
/// <param name="PriceListId">Customer-specific sales list (wins over the group's); assigning needs <c>pricelists.assign</c>.</param>
/// <param name="PaymentTermDays">0–3650.</param>
/// <param name="CreditLimit">≥ 0 with ≤ 4 decimals.</param>
/// <param name="Currency">ISO 4217; omit to use the tenant currency.</param>
public sealed record CreateCustomerRequest(
    string? Code,
    string? Name,
    string? ContactName,
    string? TaxNumber,
    string? TaxOffice,
    string? Phone,
    string? Email,
    IReadOnlyList<string>? WhatsappNumbers,
    Guid? DealerGroupId,
    Guid? PriceListId,
    int? PaymentTermDays,
    decimal? CreditLimit,
    string? Currency,
    string? Notes);

/// <summary>JSON merge patch: absent = unchanged, <c>null</c> = clear (code, name and status cannot be cleared).</summary>
/// <param name="Status">active, inactive or archived.</param>
public sealed record UpdateCustomerRequest(
    string? Code,
    string? Name,
    string? ContactName,
    string? TaxNumber,
    string? TaxOffice,
    string? Phone,
    string? Email,
    IReadOnlyList<string>? WhatsappNumbers,
    Guid? DealerGroupId,
    Guid? PriceListId,
    int? PaymentTermDays,
    decimal? CreditLimit,
    string? Currency,
    string? Notes,
    string? Status);

// ---- Addresses ----

public sealed record AddressResponse(
    Guid Id,
    Guid CustomerId,
    string Type,
    bool IsDefault,
    string? Title,
    string Line1,
    string? Line2,
    string? District,
    string City,
    string? PostalCode,
    string Country,
    string? ContactName,
    string? ContactPhone,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record AddressListResponse(IReadOnlyList<AddressResponse> Items);

/// <param name="Type">billing or shipping (default shipping).</param>
/// <param name="IsDefault">The first address of a type becomes its default; true moves the default here.</param>
/// <param name="Country">ISO 3166-1 alpha-2, default TR.</param>
public sealed record CreateAddressRequest(
    string? Type,
    bool? IsDefault,
    string? Title,
    string? Line1,
    string? Line2,
    string? District,
    string? City,
    string? PostalCode,
    string? Country,
    string? ContactName,
    string? ContactPhone);

/// <summary>JSON merge patch; <c>null</c> clears optional fields. <c>isDefault</c> accepts only true.</summary>
public sealed record UpdateAddressRequest(
    string? Type,
    bool? IsDefault,
    string? Title,
    string? Line1,
    string? Line2,
    string? District,
    string? City,
    string? PostalCode,
    string? Country,
    string? ContactName,
    string? ContactPhone);

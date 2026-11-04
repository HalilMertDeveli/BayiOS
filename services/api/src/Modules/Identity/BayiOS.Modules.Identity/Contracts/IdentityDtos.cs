namespace BayiOS.Modules.Identity.Contracts;

public sealed record MeResponse(Guid UserId, string? Email, string? FullName, IReadOnlyList<MembershipResponse> Memberships);

public sealed record MembershipResponse(Guid TenantId, string TenantName, string TenantSlug, string Role, string Status);

/// <summary>Create a tenant. The caller becomes its owner. No tenant id is accepted from the client.</summary>
public sealed record CreateTenantRequest(string? Name, string? Slug, string? Sector);

public sealed record TenantResponse(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    string Sector,
    string DefaultCurrency,
    string Locale,
    string TimeZone,
    string Role,
    string? LogoObjectPath,
    string? TaxNumber,
    string? TaxOffice,
    string? Phone,
    long Version);

/// <summary>
/// Business profile (PRD TEN-03/04/05) as a JSON merge patch: omitted fields are unchanged; <c>null</c> clears
/// <c>logoObjectPath</c>, <c>taxNumber</c>, <c>taxOffice</c> and <c>phone</c>. <c>sector</c>: motorcycle_parts,
/// led_electronics, general (changing it does not apply a template; see <c>/sector-templates</c>).
/// <c>defaultCurrency</c>: ISO 4217 code. <c>logoObjectPath</c>: a storage object path under the tenant's prefix,
/// <c>{tenantId}/…</c>, ending in .png, .jpg, .jpeg or .svg (upload is not part of this endpoint).
/// </summary>
public sealed record UpdateTenantRequest(
    string? Name,
    string? Sector,
    string? DefaultCurrency,
    string? LogoObjectPath,
    string? TaxNumber,
    string? TaxOffice,
    string? Phone);

public sealed record TenantSettingsResponse(
    string NegativeStockPolicy,
    bool SalesCanApproveOrders,
    decimal SalesMaxDiscountPercent,
    bool ShowCostToManager,
    bool ShowCostToViewer,
    bool StaffCanAssignBarcode,
    bool StaffCanApproveReceipt,
    bool Require2fa,
    decimal DefaultTaxRate,
    bool PricesIncludeTaxDisplay);

/// <summary>JSON merge patch: omitted (null) fields are left unchanged.</summary>
public sealed record UpdateTenantSettingsRequest(
    string? NegativeStockPolicy,
    bool? SalesCanApproveOrders,
    decimal? SalesMaxDiscountPercent,
    bool? ShowCostToManager,
    bool? ShowCostToViewer,
    bool? StaffCanAssignBarcode,
    bool? StaffCanApproveReceipt,
    bool? Require2fa,
    decimal? DefaultTaxRate = null,
    bool? PricesIncludeTaxDisplay = null);

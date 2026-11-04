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
    string Role);

public sealed record TenantSettingsResponse(
    string NegativeStockPolicy,
    bool SalesCanApproveOrders,
    decimal SalesMaxDiscountPercent,
    bool ShowCostToManager,
    bool ShowCostToViewer,
    bool StaffCanAssignBarcode,
    bool StaffCanApproveReceipt,
    bool Require2fa);

/// <summary>JSON merge patch: omitted (null) fields are left unchanged.</summary>
public sealed record UpdateTenantSettingsRequest(
    string? NegativeStockPolicy,
    bool? SalesCanApproveOrders,
    decimal? SalesMaxDiscountPercent,
    bool? ShowCostToManager,
    bool? ShowCostToViewer,
    bool? StaffCanAssignBarcode,
    bool? StaffCanApproveReceipt,
    bool? Require2fa);

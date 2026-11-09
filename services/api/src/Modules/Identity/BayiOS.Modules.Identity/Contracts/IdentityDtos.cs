namespace BayiOS.Modules.Identity.Contracts;

/// <param name="ActiveTenant">
/// The tenant selected with the optional <c>X-Tenant-Id</c> header and the caller's effective permissions there; null when
/// the header is absent or names no active membership.
/// </param>
public sealed record MeResponse(Guid UserId, string? Email, string? FullName, IReadOnlyList<MembershipResponse> Memberships, ActiveTenantResponse? ActiveTenant);

/// <summary>Effective permissions in the active tenant: the role matrix with tenant rules (roles-permissions §4) applied.</summary>
/// <param name="Entitlements">
/// What the tenant's plan, add-ons and overrides grant now (#51), so clients can show gates; the API enforces them
/// regardless. Shown to every role (no prices, no billing details).
/// </param>
public sealed record ActiveTenantResponse(
    Guid TenantId,
    string Role,
    IReadOnlyList<PermissionGrantResponse> Permissions,
    TenantEntitlementsResponse Entitlements);

/// <summary>The active tenant's effective entitlements.</summary>
/// <param name="Plan">The plan that applies now (the trial plan while <c>trialing</c>).</param>
/// <param name="Status"><c>trialing</c>, <c>active</c>, <c>expired</c> (fallback plan) or <c>cancelled</c>.</param>
/// <param name="TrialEndsAt">Only while <c>trialing</c>.</param>
/// <param name="Features">Granted feature codes (e.g. <c>warehouse_locations</c>, <c>ai_order_parsing</c>).</param>
/// <param name="Limits">Numeric limits; a null value is unlimited.</param>
public sealed record TenantEntitlementsResponse(
    string Plan,
    string Status,
    DateTimeOffset? TrialEndsAt,
    string CatalogVersion,
    IReadOnlyList<string> Features,
    IReadOnlyList<EntitlementLimitResponse> Limits);

/// <param name="Value">null = unlimited.</param>
public sealed record EntitlementLimitResponse(string Code, long? Value);

/// <param name="Scope">
/// <c>all</c> = every record; <c>conditional</c> = K grant limited per record by <paramref name="Condition"/>;
/// <c>own</c> = O grant, own records only. Permissions the caller does not hold (or whose tenant rule is off) are absent.
/// </param>
/// <param name="Condition">Stable condition code (roles-permissions §3 footnote), for <c>conditional</c> / <c>own</c>.</param>
/// <param name="Parameters">Tenant values of the condition (e.g. the sales discount limit); null when it has none.</param>
public sealed record PermissionGrantResponse(string Permission, string Scope, string? Condition, GrantParametersResponse? Parameters);

/// <summary>Values a client may show with a conditional grant; a null member does not apply to this condition.</summary>
/// <param name="MaxDiscountPercent">
/// <c>orders.discount_within_limit</c> (footnote 10): the largest line discount as a percentage of the line subtotal.
/// </param>
public sealed record GrantParametersResponse(decimal? MaxDiscountPercent);

/// <param name="Status">The caller's membership status: invited, active or disabled.</param>
/// <param name="TenantStatus">
/// The tenant's own status: active, suspended or closed. Only an active tenant can be selected (others answer
/// <c>403 tenant.inactive</c>); clients list the rest greyed out.
/// </param>
public sealed record MembershipResponse(Guid TenantId, string TenantName, string TenantSlug, string Role, string Status, string TenantStatus);

/// <summary>A member of the active tenant (<c>GET /members</c>, permission <c>members.read</c>).</summary>
/// <param name="DisplayName">Full name, else the e-mail local part (<see cref="UserDisplayName"/>); null when neither is known.</param>
/// <param name="FullName">As entered in the profile; usually null right after sign-up.</param>
/// <param name="Email">
/// Not masked: only members.read holders (owner, admin, manager) see the list, and they administer the team
/// (data-policy §2: staff e-mail is processed for authentication, authorization and notification).
/// </param>
/// <param name="Role">owner, admin, manager, sales, warehouse_staff or viewer.</param>
/// <param name="Status">invited, active or disabled.</param>
/// <param name="CreatedAt">When the membership was created.</param>
public sealed record MemberResponse(
    Guid UserId,
    string? DisplayName,
    string? FullName,
    string? Email,
    string Role,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record MemberPageInfo(string? NextCursor, bool HasMore, int Limit);

public sealed record MemberPageResponse(IReadOnlyList<MemberResponse> Items, MemberPageInfo Page);

/// <summary>Invite a person to the active tenant (<c>members.invite</c>). No e-mail is sent: share the returned token.</summary>
/// <param name="Email">The address the invitee will sign in with (Supabase Auth); compared case-insensitively.</param>
/// <param name="Role">
/// admin, manager, sales, warehouse_staff or viewer. Only an owner may invite an admin; an admin invites manager and
/// lower roles (roles-permissions footnote 1). owner is never invited (ownership transfer).
/// </param>
public sealed record CreateInvitationRequest(string? Email, string? Role);

/// <summary>An invitation of the active tenant. The token is never part of it.</summary>
/// <param name="Status">pending, accepted, revoked or expired (a pending invitation past <paramref name="ExpiresAt"/> reads as expired).</param>
/// <param name="TokenCount">How many tokens were issued (each resend rotates the token; earlier ones stop working).</param>
public sealed record InvitationResponse(
    Guid Id,
    string Email,
    string Role,
    string Status,
    DateTimeOffset ExpiresAt,
    int TokenCount,
    DateTimeOffset CreatedAt,
    Guid InvitedBy,
    DateTimeOffset? AcceptedAt,
    Guid? AcceptedBy,
    DateTimeOffset? RevokedAt);

/// <summary>
/// A newly issued invitation token. Shown once: the API keeps only its SHA-256 and this response is never stored for
/// replay. Share it with the invitee (e.g. as <c>https://&lt;web&gt;/davet#&lt;token&gt;</c> — a URL fragment reaches
/// neither servers nor referrers); they accept it with <c>POST /invitations/accept</c> after signing in with the
/// invited, verified e-mail address.
/// </summary>
public sealed record IssuedInvitationResponse(InvitationResponse Invitation, string Token);

public sealed record InvitationListResponse(IReadOnlyList<InvitationResponse> Items);

/// <param name="Token">The invitation token (43 characters, base64url).</param>
public sealed record AcceptInvitationRequest(string? Token);

/// <summary>The membership the invitation created; select the tenant with <c>X-Tenant-Id</c> from now on.</summary>
public sealed record AcceptInvitationResponse(Guid TenantId, string TenantName, string TenantSlug, string Role, string Status);

/// <param name="Role">admin, manager, sales, warehouse_staff or viewer (owner moves only by ownership transfer).</param>
public sealed record ChangeMemberRoleRequest(string? Role);

/// <param name="UserId">An active member of the tenant who becomes the owner; the caller becomes admin.</param>
public sealed record TransferOwnershipRequest(Guid? UserId);

public sealed record OwnershipTransferResponse(MemberResponse NewOwner, MemberResponse PreviousOwner);

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

/// <param name="NegativeStockPolicy">Always <c>block</c>: stock never goes below zero (owner decision OD-09).</param>
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
    bool PricesIncludeTaxDisplay,
    bool RequireOrderApproval,
    string OrderNumberPrefix);

/// <summary>JSON merge patch: omitted (null) fields are left unchanged.</summary>
/// <param name="NegativeStockPolicy">
/// Fixed rule (owner decision OD-09): only <c>block</c> is accepted — stock never goes below zero; <c>warn</c> and
/// <c>allow</c> answer 422 <c>validation.failed</c>. Kept for compatibility; clients need not send it.
/// </param>
/// <param name="RequireOrderApproval">
/// False: a submitted order is approved (and reserved) at once when the submitter may approve and the source is not
/// whatsapp_ai; otherwise it waits in pending_approval as with true (default).
/// </param>
/// <param name="OrderNumberPrefix">Prefix of new order numbers (<c>{prefix}-YYYY-NNNNNN</c>): 1–10 of A–Z and 0–9, starting with a letter (lowercase is upper-cased).</param>
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
    bool? PricesIncludeTaxDisplay = null,
    bool? RequireOrderApproval = null,
    string? OrderNumberPrefix = null);

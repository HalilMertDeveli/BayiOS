using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Identity.Domain;

internal static class TenantStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Closed = "closed";
}

internal static class MembershipStatuses
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Disabled = "disabled";
}

internal static class Sectors
{
    public const string MotorcycleParts = "motorcycle_parts";
    public const string LedElectronics = "led_electronics";
    public const string General = "general";

    public static readonly IReadOnlyList<string> All = [MotorcycleParts, LedElectronics, General];
}

/// <summary>
/// Owner decision OD-09 (2026-11-09): stock never goes below zero, so <c>block</c> is the only policy. The former
/// <c>warn</c>/<c>allow</c> values are refused on update and were converted by migration <c>NegativeStockBlockOnly</c>
/// (the DB CHECK allows only <c>block</c>). The field stays in the settings contract as a fixed, read-only rule.
/// </summary>
internal static class NegativeStockPolicies
{
    public const string Block = "block";

    public static readonly IReadOnlyList<string> All = [Block];
}

/// <summary>identity.tenants — RLS exception (data-model §2.3): visible to its members and inside its own context.</summary>
internal sealed class Tenant
{
    private Tenant()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public string Status { get; private set; } = TenantStatuses.Active;

    public string Sector { get; private set; } = Sectors.General;

    public string DefaultCurrency { get; private set; } = "TRY";

    public string Locale { get; private set; } = "tr-TR";

    public string TimeZone { get; private set; } = "Europe/Istanbul";

    /// <summary>Storage object path of the logo (<c>{tenantId}/…png|jpg|jpeg|svg</c>); the upload itself is not built yet.</summary>
    public string? LogoObjectPath { get; private set; }

    public string? TaxNumber { get; private set; }

    public string? TaxOffice { get; private set; }

    public string? Phone { get; private set; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public static Tenant Create(string name, string slug, string sector, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        Slug = slug,
        Sector = sector,
        CreatedAt = now,
    };

    /// <summary>Business profile (PRD TEN-03/04/05). Values are validated by the caller.</summary>
    public void UpdateProfile(
        string name, string sector, string defaultCurrency, string? logoObjectPath, string? taxNumber, string? taxOffice, string? phone, DateTimeOffset now)
    {
        Name = name;
        Sector = sector;
        DefaultCurrency = defaultCurrency;
        LogoObjectPath = logoObjectPath;
        TaxNumber = taxNumber;
        TaxOffice = taxOffice;
        Phone = phone;
        RowVersion++;
        UpdatedAt = now;
    }
}

/// <summary>identity.memberships — the only source of a user's tenant access and role.</summary>
internal sealed class Membership : ITenantOwned
{
    private Membership()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Role { get; private set; } = Roles.Viewer;

    public string Status { get; private set; } = MembershipStatuses.Invited;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static Membership CreateOwner(Guid tenantId, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        UserId = userId,
        Role = Roles.Owner,
        Status = MembershipStatuses.Active,
        CreatedAt = now,
        CreatedBy = userId,
    };

    /// <summary>The invitee's membership, created active when they accept (the invitation carried the role).</summary>
    public static Membership Join(Guid tenantId, Guid userId, string role, Guid invitedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = MembershipStatuses.Active,
        CreatedAt = now,
        CreatedBy = invitedBy,
    };

    public bool IsActive => Status == MembershipStatuses.Active;

    public void ChangeRole(string role, Guid by, DateTimeOffset now)
    {
        Role = role;
        Touch(by, now);
    }

    public void Disable(Guid by, DateTimeOffset now)
    {
        Status = MembershipStatuses.Disabled;
        Touch(by, now);
    }

    public void Enable(Guid by, DateTimeOffset now)
    {
        Status = MembershipStatuses.Active;
        Touch(by, now);
    }

    private void Touch(Guid by, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = by;
    }
}

/// <summary>
/// Who may grant which role (roles-permissions §3.1 + footnote 1, domain-model §1, user-flows AF-09). Pure rules; the
/// application service combines them with the caller's permissions.
/// </summary>
internal static class MemberRoleRules
{
    /// <summary>Roles an invitation or a role change may set. <c>owner</c> only moves by ownership transfer.</summary>
    public static readonly IReadOnlyList<string> Grantable = [Roles.Admin, Roles.Manager, Roles.Sales, Roles.WarehouseStaff, Roles.Viewer];

    /// <summary>Footnote 1: the roles an admin manages (manager and lower).</summary>
    public static bool IsBelowAdmin(string role) => role is Roles.Manager or Roles.Sales or Roles.WarehouseStaff or Roles.Viewer;

    /// <summary>
    /// Whether <paramref name="callerRole"/> may hand out <paramref name="role"/>: nobody grants a role above their own,
    /// only an owner grants <c>admin</c>, nobody grants <c>owner</c> (that is an ownership transfer).
    /// </summary>
    public static bool CanGrant(string? callerRole, string role) => callerRole switch
    {
        Roles.Owner => Grantable.Contains(role),
        Roles.Admin => IsBelowAdmin(role),
        _ => false,
    };
}

internal static class InvitationStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Revoked = "revoked";

    /// <summary>Stored only when a pending invitation past its expiry is superseded; otherwise derived from <c>expires_at</c>.</summary>
    public const string Expired = "expired";

    public static readonly IReadOnlyList<string> All = [Pending, Accepted, Revoked, Expired];
}

/// <summary>
/// identity.invitations — standard tenant RLS. An invitation names an e-mail address and a role; the membership is only
/// created when a user signed in with that (verified) address accepts it with the token. The token itself is never
/// stored: only its SHA-256 (data-model §5.1, domain-model "Invitation"). Single use; at most one pending invitation per
/// e-mail per tenant.
/// </summary>
internal sealed class Invitation : ITenantOwned
{
    private Invitation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Normalised (trimmed, lower-case invariant).</summary>
    public string Email { get; private set; } = string.Empty;

    public string Role { get; private set; } = Roles.Viewer;

    public byte[] TokenHash { get; private set; } = [];

    public string Status { get; private set; } = InvitationStatuses.Pending;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>How many times a token was issued (1 = the original; each resend rotates the token).</summary>
    public int TokenCount { get; private set; } = 1;

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static Invitation Create(Guid tenantId, string email, string role, byte[] tokenHash, DateTimeOffset expiresAt, Guid by, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Email = email,
        Role = role,
        TokenHash = tokenHash,
        ExpiresAt = expiresAt,
        CreatedAt = now,
        CreatedBy = by,
    };

    /// <summary>Pending and not yet expired: it holds a seat and can be accepted.</summary>
    public bool IsOpen(DateTimeOffset now) => Status == InvitationStatuses.Pending && ExpiresAt > now;

    /// <summary>The status a client sees: a pending invitation past its expiry reads as <c>expired</c>.</summary>
    public string EffectiveStatus(DateTimeOffset now) =>
        Status == InvitationStatuses.Pending && ExpiresAt <= now ? InvitationStatuses.Expired : Status;

    /// <summary>Resend: a new token (the old one stops working) and a fresh expiry.</summary>
    public void Rotate(byte[] tokenHash, DateTimeOffset expiresAt, Guid by, DateTimeOffset now)
    {
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        TokenCount++;
        Touch(by, now);
    }

    public void Accept(Guid userId, DateTimeOffset now)
    {
        Status = InvitationStatuses.Accepted;
        AcceptedAt = now;
        AcceptedBy = userId;
        Touch(userId, now);
    }

    public void Revoke(Guid by, DateTimeOffset now)
    {
        Status = InvitationStatuses.Revoked;
        RevokedAt = now;
        RevokedBy = by;
        Touch(by, now);
    }

    /// <summary>A pending invitation past its expiry, closed so a new one for the same address can be created.</summary>
    public void Expire(Guid by, DateTimeOffset now)
    {
        Status = InvitationStatuses.Expired;
        Touch(by, now);
    }

    private void Touch(Guid by, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = by;
    }
}

/// <summary>Invitation tokens: 32 random bytes as base64url (43 characters); only the SHA-256 is stored.</summary>
internal static class InvitationTokens
{
    public const int Length = 43;

    public static (string Token, byte[] Hash) Generate()
    {
        var token = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    /// <summary>The stored form of <paramref name="token"/>; null when it cannot be a token we issued.</summary>
    public static byte[]? TryHash(string? token) =>
        token is { Length: Length } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? Hash(token) : null;

    private static byte[] Hash(string token) => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
}

/// <summary>
/// The rule for "the invitee's e-mail is verified" (#129). Supabase access tokens carry <c>email</c> and
/// <c>user_metadata.email_verified</c> (GoTrue sets it on confirmation) but no top-level <c>email_verified</c>; the
/// metadata object is writable by its user, so it is accepted only as configured (<c>Invitations:TrustUserMetadataEmailVerified</c>).
/// What makes it sound is the project setting "Confirm email": an unconfirmed address gets no session at all.
/// A top-level <c>email_verified</c> (OIDC; or a Supabase custom access-token hook reading <c>email_confirmed_at</c>)
/// is server-asserted and always accepted. Anonymous sessions never count.
/// </summary>
internal static class EmailVerification
{
    public static bool IsVerified(bool? topLevelEmailVerified, bool? userMetadataEmailVerified, bool isAnonymous, bool trustUserMetadata) =>
        !isAnonymous && (topLevelEmailVerified == true || (trustUserMetadata && userMetadataEmailVerified == true));

    /// <summary>Trimmed, lower-case invariant; null for anything that is not a plausible single address.</summary>
    public static string? Normalize(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (value is null or { Length: < 3 or > 254 })
        {
            return null;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && value.IndexOf('.', at) > at + 1
            && !value.EndsWith('.')
            && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or ';' or '<' or '>' or '"')
            ? value
            : null;
    }
}

/// <summary>identity.user_profiles — keyed by the Supabase auth user id (JWT <c>sub</c>); no FK to auth schema.</summary>
internal sealed class UserProfile
{
    private UserProfile()
    {
    }

    public Guid UserId { get; private set; }

    public string? Email { get; private set; }

    public string? FullName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public static UserProfile Create(Guid userId, string? email, DateTimeOffset now) => new()
    {
        UserId = userId,
        Email = email,
        CreatedAt = now,
    };
}

/// <summary>identity.tenant_settings — standard tenant RLS; holds the adjustable rules of roles-permissions §4.</summary>
internal sealed class TenantSettings : ITenantOwned
{
    public const string DefaultOrderNumberPrefix = "SIP";
    public const int MaxOrderNumberPrefixLength = 10;

    /// <summary>The database CHECK of <see cref="OrderNumberPrefix"/> (same rule).</summary>
    public const string OrderNumberPrefixPattern = "^[A-Z][A-Z0-9]{0,9}$";

    private TenantSettings()
    {
    }

    /// <summary>1–10 of A–Z and 0–9, starting with a letter (ASCII only: the number goes into documents and file names).</summary>
    public static bool IsValidOrderNumberPrefix(string? prefix) =>
        prefix is { Length: > 0 and <= MaxOrderNumberPrefixLength }
        && char.IsAsciiLetterUpper(prefix[0])
        && prefix.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));

    public Guid TenantId { get; private set; }

    public string NegativeStockPolicy { get; set; } = NegativeStockPolicies.Block;

    public bool SalesCanApproveOrders { get; set; } = true;

    public decimal SalesMaxDiscountPercent { get; set; }

    public bool ShowCostToManager { get; set; }

    public bool ShowCostToViewer { get; set; }

    public bool StaffCanAssignBarcode { get; set; }

    public bool StaffCanApproveReceipt { get; set; }

    public bool Require2fa { get; set; }

    /// <summary>Default VAT rate (%) for new products (PRD TEN-05).</summary>
    public decimal DefaultTaxRate { get; set; } = 20m;

    /// <summary>Show prices VAT-inclusive (PRD TEN-05, AQ-09: default exclusive).</summary>
    public bool PricesIncludeTaxDisplay { get; set; }

    /// <summary>
    /// inventory-and-orders §3.1 #1a: false = submit approves (and reserves) at once when the submitter may approve
    /// and the order is not an AI draft; true (default) = every order waits in pending_approval.
    /// </summary>
    public bool RequireOrderApproval { get; set; } = true;

    /// <summary>Prefix of new order numbers (PRD ORD-14), <see cref="IsValidOrderNumberPrefix"/>; existing numbers never change.</summary>
    public string OrderNumberPrefix { get; set; } = DefaultOrderNumberPrefix;

    /// <summary>
    /// WHT-01 (owner decision 2026-11-09, #98): true (default) = order approval creates the picking task in the same
    /// transaction; false = a supervisor creates it (<c>POST /picking-tasks</c>).
    /// </summary>
    public bool AutoCreatePickingTask { get; set; } = true;

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static TenantSettings CreateDefault(Guid tenantId, Guid createdBy, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        CreatedAt = now,
        CreatedBy = createdBy,
    };

    public void MarkUpdated(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

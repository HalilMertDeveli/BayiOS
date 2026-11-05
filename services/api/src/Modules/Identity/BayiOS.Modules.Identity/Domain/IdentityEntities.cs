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

internal static class NegativeStockPolicies
{
    public static readonly IReadOnlyList<string> All = ["block", "warn", "allow"];
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

    public string NegativeStockPolicy { get; set; } = "block";

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

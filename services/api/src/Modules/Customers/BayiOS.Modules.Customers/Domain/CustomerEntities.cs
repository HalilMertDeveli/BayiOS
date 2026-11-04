using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Customers.Domain;

internal static class CustomerStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Active, Inactive, Archived];
}

internal static class DealerGroupStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Active, Archived];
}

internal static class AddressTypes
{
    public const string Billing = "billing";
    public const string Shipping = "shipping";

    public static readonly IReadOnlyList<string> All = [Billing, Shipping];
}

/// <summary>Shared columns (data-model §1): tenant, audit stamps, optimistic concurrency (ETag source).</summary>
internal abstract class CustomersEntity : ITenantOwned
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

/// <summary>customers.dealer_groups (data-model §5.5, CUS-02): a price group with an assigned sales price list.</summary>
internal sealed class DealerGroup : CustomersEntity
{
    private DealerGroup()
    {
    }

    public string Name { get; set; } = string.Empty;

    /// <summary>→ pricing.price_lists (no cross-module FK; validated through the Pricing contract).</summary>
    public Guid? PriceListId { get; set; }

    /// <summary>Suggested line discount (0–100). Not applied by price resolution.</summary>
    public decimal? DefaultDiscountPercent { get; set; }

    public string Status { get; private set; } = DealerGroupStatuses.Active;

    public DateTimeOffset? ArchivedAt { get; private set; }

    public void SetStatus(string status, DateTimeOffset now)
    {
        Status = status;
        ArchivedAt = status == DealerGroupStatuses.Archived ? ArchivedAt ?? now : null;
    }

    public static DealerGroup Create(Guid tenantId, string name, Guid? priceListId, decimal? defaultDiscountPercent, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Name = name,
        PriceListId = priceListId,
        DefaultDiscountPercent = defaultDiscountPercent,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>
/// customers.customers (data-model §5.5, CUS-01): a business customer / dealer. Personal data (data-policy §2):
/// contact name, phone, e-mail, WhatsApp numbers and — for sole proprietors — an 11-digit tax number (TCKN), which
/// list responses mask. Never hard-deleted: archived, or anonymized on a data-subject request (data-policy §5).
/// </summary>
internal sealed class Customer : CustomersEntity
{
    private Customer()
    {
    }

    /// <summary>Tenant-unique account code (cari kodu), stored upper-case.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ContactName { get; set; }

    /// <summary>VKN (10 digits) or, for sole proprietors, TCKN (11 digits).</summary>
    public string? TaxNumber { get; set; }

    public string? TaxOffice { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    /// <summary>E.164 numbers used to match WhatsApp senders (CUS-05).</summary>
    public string[] WhatsappNumbers { get; set; } = [];

    public Guid? DealerGroupId { get; set; }

    /// <summary>Customer-specific list (CUS-03): wins over the dealer group's list. → pricing, no FK.</summary>
    public Guid? PriceListId { get; set; }

    public int? PaymentTermDays { get; set; }

    public decimal? CreditLimit { get; set; }

    /// <summary>ISO 4217; null = tenant default currency.</summary>
    public string? Currency { get; set; }

    public string? Notes { get; set; }

    public string Status { get; private set; } = CustomerStatuses.Active;

    public DateTimeOffset? ArchivedAt { get; private set; }

    public DateTimeOffset? AnonymizedAt { get; private set; }

    public void SetStatus(string status, DateTimeOffset now)
    {
        Status = status;
        ArchivedAt = status == CustomerStatuses.Archived ? ArchivedAt ?? now : null;
    }

    /// <summary>Data-subject erasure (data-policy §5): identity kept for order history, personal fields cleared.</summary>
    public void Anonymize(DateTimeOffset now)
    {
        Name = $"Silinmiş müşteri #{Id.ToString("N")[^8..]}";
        ContactName = null;
        TaxNumber = null;
        TaxOffice = null;
        Phone = null;
        Email = null;
        WhatsappNumbers = [];
        Notes = null;
        AnonymizedAt = now;
        SetStatus(CustomerStatuses.Archived, now);
    }

    public static Customer Create(Guid tenantId, string code, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Code = code,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>customers.customer_addresses: billing/shipping addresses; at most one default per customer and type.</summary>
internal sealed class CustomerAddress : CustomersEntity
{
    private CustomerAddress()
    {
    }

    public Guid CustomerId { get; private init; }

    public string Type { get; set; } = AddressTypes.Shipping;

    public bool IsDefault { get; set; }

    public string? Title { get; set; }

    public string Line1 { get; set; } = string.Empty;

    public string? Line2 { get; set; }

    public string? District { get; set; }

    public string City { get; set; } = string.Empty;

    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2, default TR.</summary>
    public string Country { get; set; } = "TR";

    public string? ContactName { get; set; }

    public string? ContactPhone { get; set; }

    public static CustomerAddress Create(Guid tenantId, Guid customerId, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        CustomerId = customerId,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

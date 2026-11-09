namespace BayiOS.Modules.Orders.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Ports: Orders references only the Inventory and Pricing contracts. Customer status/currency come from Pricing's
// ICustomerPricingDirectory (implemented by Customers); the host wires the Catalog adapter below. All implementations
// run on the request's UnitOfWork connection, inside the same transaction.
// ------------------------------------------------------------------------------------------------------------------

/// <param name="Name">Variant name, else the product name — stored on the line as a snapshot.</param>
/// <param name="Unit">Unit code of the variant (snapshot).</param>
/// <param name="IsActive">Variant and its product are active.</param>
public sealed record OrderVariantInfo(Guid Id, string Sku, string Name, string Unit, bool IsActive, bool AllowsFraction);

/// <summary>Implemented over the Catalog module.</summary>
public interface IOrderVariantDirectory
{
    Task<IReadOnlyDictionary<Guid, OrderVariantInfo>> FindVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <summary>Tenant order settings (identity.tenant_settings; PRD ORD-14, inventory-and-orders §3.1 #1a).</summary>
/// <param name="RequireApproval">False: submit approves at once when allowed (§3.1 #1a).</param>
/// <param name="NumberPrefix">Prefix of new order numbers, validated by the settings owner.</param>
public sealed record OrderSettings(bool RequireApproval, string NumberPrefix)
{
    public static OrderSettings Defaults { get; } = new(true, "SIP");
}

/// <summary>Implemented by the host over Identity's tenant settings (request tenant, RLS).</summary>
public interface IOrderSettingsProvider
{
    Task<OrderSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>A customer address as Orders snapshots it (customers.customer_addresses).</summary>
/// <param name="Type"><c>shipping</c> or <c>billing</c>.</param>
/// <param name="Country">ISO 3166-1 alpha-2.</param>
public sealed record OrderCustomerAddress(
    Guid Id,
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
    string? ContactPhone);

/// <summary>Implemented by the host over the Customers module (tenant query filter + RLS, request transaction).</summary>
public interface IOrderCustomerAddresses
{
    /// <summary>Every address of the customer (a customer has a handful); empty for an unknown or foreign customer.</summary>
    Task<IReadOnlyList<OrderCustomerAddress>> ListAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by the Warehouse module: which orders a warehouse_staff user may read (roles-permissions footnote 8 —
/// orders behind picking tasks assigned to them or waiting in the active pool).
/// </summary>
public interface IOrderTaskVisibility
{
    Task<IReadOnlyList<Guid>> OrdersVisibleToStaffAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Implemented by Orders for other modules: orders a user created (footnote 12 — sales sees their tasks).</summary>
public interface IOrderOwnership
{
    Task<IReadOnlyList<Guid>> OrdersCreatedByAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by Orders for the Customers module (#115, data-policy §5): KVKK erasure of the personal fields Orders
/// keeps about a customer. Runs on the request UnitOfWork connection, inside the caller's transaction.
/// </summary>
public interface IOrderPersonalData
{
    /// <summary>
    /// Masks the personal fields of every shipping / billing address snapshot on the customer's orders, whatever the
    /// order status (the one exception the post-approval immutability trigger allows). Locks all of the customer's
    /// order headers first so a concurrent submit / approve cannot write a fresh, unmasked snapshot afterwards.
    /// Idempotent: returns the number of orders changed by this call (0 when everything was already masked).
    /// </summary>
    Task<int> AnonymizeCustomerSnapshotsAsync(Guid customerId, CancellationToken cancellationToken);
}

/// <summary>
/// Display names for order responses, implemented by the host over Customers, Warehouse and Identity. Each call is one
/// batch query for every id of a response (no N+1); unknown or foreign ids are simply absent.
/// </summary>
public interface IOrderReferenceNames
{
    Task<IReadOnlyDictionary<Guid, string>> CustomerNamesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> WarehouseNamesAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken);

    /// <summary>Identity's display-name rule (full name, else the e-mail local part).</summary>
    Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

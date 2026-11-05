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

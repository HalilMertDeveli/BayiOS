namespace BayiOS.Modules.Inventory.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Public stock commands (inventory-and-orders §1.5): Orders/Warehouse call these inside their own request
// UnitOfWork. Every command is all-or-nothing: it runs under a savepoint and leaves no trace when it returns an error.
// ------------------------------------------------------------------------------------------------------------------

/// <summary>The inventory engine. All stock changes go through here; nothing else writes balances or the ledger.</summary>
public interface IInventoryService
{
    /// <summary>Goods receipt (§4.1): one <c>receipt</c> movement per line, on_hand increases.</summary>
    Task<InventoryResult<ReceiptResult>> ReceiveAsync(ReceiveStock command, CancellationToken cancellationToken);

    /// <summary>Manual adjustment (§4.2) with a mandatory reason; decreases may not consume reserved stock.</summary>
    Task<InventoryResult<MovementResult>> AdjustAsync(AdjustStock command, CancellationToken cancellationToken);

    /// <summary>Order approval reservation (§4.3): every line or none. Not a movement.</summary>
    Task<InventoryResult<ReservationResult>> ReserveAsync(ReserveStock command, CancellationToken cancellationToken);

    /// <summary>Releases the open quantity of an order's active reservations (§4.4). No-op when nothing is open.</summary>
    Task<InventoryResult<ReservationResult>> ReleaseAsync(ReleaseReservations command, CancellationToken cancellationToken);

    /// <summary>Shipment deduction (§4.6): consumes reservations and writes <c>shipment_out</c>; available is unchanged.</summary>
    Task<InventoryResult<IssueResult>> IssueAsync(IssueStock command, CancellationToken cancellationToken);
}

/// <summary>Read-side questions other modules may ask.</summary>
public interface IStockQueries
{
    /// <summary>True when any balance row of the warehouse has on_hand or reserved different from zero.</summary>
    Task<bool> WarehouseHasStockAsync(Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>
    /// The order's active reservations with their <b>open</b> quantity (quantity − consumed − released) in
    /// <see cref="ReservationAllocation.Quantity"/>, ordered by order line and location. Unlocked read: commands lock
    /// and re-check before changing anything.
    /// </summary>
    Task<IReadOnlyList<ReservationAllocation>> GetOpenReservationsAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>Movement types (ADR 0004 §1). Quantities are signed; the sign must match the type.</summary>
public static class MovementTypes
{
    public const string Receipt = "receipt";
    public const string AdjustmentIn = "adjustment_in";
    public const string AdjustmentOut = "adjustment_out";
    public const string CountCorrection = "count_correction";
    public const string TransferOut = "transfer_out";
    public const string TransferIn = "transfer_in";
    public const string ShipmentOut = "shipment_out";
    public const string ReturnIn = "return_in";
    public const string DamageOut = "damage_out";

    public static readonly IReadOnlyList<string> All =
        [Receipt, AdjustmentIn, AdjustmentOut, CountCorrection, TransferOut, TransferIn, ShipmentOut, ReturnIn, DamageOut];

    public static readonly IReadOnlyList<string> Inbound = [Receipt, AdjustmentIn, TransferIn, ReturnIn];

    public static readonly IReadOnlyList<string> Outbound = [AdjustmentOut, TransferOut, ShipmentOut, DamageOut];

    /// <summary>Types the adjustment command accepts (§4.2).</summary>
    public static readonly IReadOnlyList<string> Adjustments = [AdjustmentIn, AdjustmentOut, DamageOut, ReturnIn];
}

/// <summary>What a movement points back to (data-model §5.7 <c>source_type</c>).</summary>
public static class MovementSourceTypes
{
    public const string Receipt = "receipt";
    public const string Adjustment = "adjustment";
    public const string StockCount = "stock_count";
    public const string Transfer = "transfer";
    public const string Shipment = "shipment";
    public const string Return = "return";
    public const string Import = "import";

    public static readonly IReadOnlyList<string> All = [Receipt, Adjustment, StockCount, Transfer, Shipment, Return, Import];
}

/// <summary>Tenant setting <c>negative_stock_policy</c> (PRD TEN-12): block (default) / warn / allow.</summary>
public static class NegativeStockPolicies
{
    public const string Block = "block";
    public const string Warn = "warn";
    public const string Allow = "allow";
}

// ---- Commands ----

/// <param name="WarehouseId">Null = the tenant's default warehouse (created on first use).</param>
/// <param name="ReceiptId">Source document id; generated when null.</param>
/// <param name="Reference">Free text (supplier delivery note no. etc.), stored as the movement reason.</param>
public sealed record ReceiveStock(
    Guid? WarehouseId,
    IReadOnlyList<ReceiveLine> Lines,
    Guid? ReceiptId = null,
    string? Reference = null,
    string? CorrelationId = null);

/// <param name="LineId">Source line id; generated when null.</param>
public sealed record ReceiveLine(Guid VariantId, Guid? LocationId, decimal Quantity, decimal? UnitCost = null, Guid? LineId = null);

/// <param name="Quantity">Positive; the direction comes from <paramref name="Type"/>.</param>
/// <param name="ConfirmNegativeStock">Explicit user confirmation under the <c>warn</c> policy (§2.4).</param>
public sealed record AdjustStock(
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    string Type,
    decimal Quantity,
    string Reason,
    bool ConfirmNegativeStock = false,
    string? CorrelationId = null);

public sealed record ReserveStock(
    Guid OrderId,
    Guid WarehouseId,
    IReadOnlyList<ReserveLine> Lines,
    bool ConfirmNegativeStock = false,
    string? CorrelationId = null);

public sealed record ReserveLine(Guid OrderLineId, Guid VariantId, decimal Quantity);

/// <param name="OrderLineIds">Null = every active reservation of the order.</param>
public sealed record ReleaseReservations(Guid OrderId, IReadOnlyList<Guid>? OrderLineIds = null, string? CorrelationId = null);

public sealed record IssueStock(Guid ShipmentId, Guid WarehouseId, IReadOnlyList<IssueLine> Lines, string? CorrelationId = null);

public sealed record IssueLine(Guid ShipmentLineId, Guid OrderLineId, Guid VariantId, Guid? LocationId, decimal Quantity);

// ---- Results ----

public sealed record InventoryResult<T>(T? Value, InventoryError? Error)
{
    public bool Succeeded => Error is null;

    public static implicit operator InventoryResult<T>(T value) => new(value, null);

    public static implicit operator InventoryResult<T>(InventoryError error) => new(default, error);
}

/// <summary>A stable Problem Details code (api-strategy §6) plus inventory-specific extensions.</summary>
public sealed record InventoryError(
    int Status,
    string Code,
    string Title,
    IDictionary<string, string[]>? Errors = null,
    IReadOnlyList<StockShortage>? Shortages = null,
    bool ConfirmationRequired = false)
{
    public static InventoryError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static InventoryError Busy() =>
        new(409, "inventory.busy", "Stock rows are locked by another operation; retry shortly");
}

/// <summary>One line that cannot be served from available stock.</summary>
public sealed record StockShortage(Guid VariantId, string? Sku, Guid WarehouseId, Guid? LocationId, decimal Requested, decimal Available);

public sealed record MovementResult(
    Guid Id,
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    string Type,
    decimal Quantity,
    decimal? UnitCost,
    string SourceType,
    Guid? SourceId,
    Guid? SourceLineId,
    string? Reason,
    decimal BalanceAfter,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    Guid? CreatedBy);

public sealed record ReceiptResult(Guid ReceiptId, Guid WarehouseId, IReadOnlyList<MovementResult> Movements);

public sealed record ReservationAllocation(
    Guid ReservationId,
    Guid OrderLineId,
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    decimal Quantity,
    string Status);

public sealed record ReservationResult(Guid OrderId, IReadOnlyList<ReservationAllocation> Reservations);

public sealed record IssueResult(Guid ShipmentId, IReadOnlyList<MovementResult> Movements);

// ------------------------------------------------------------------------------------------------------------------
// Ports: Inventory is a pure stock engine (ADR 0004 §8). The owning modules implement these so Inventory never
// references them: Warehouse implements IWarehouseDirectory, the host wires Catalog/Identity adapters.
// All implementations run on the request's UnitOfWork connection, inside the same transaction.
// ------------------------------------------------------------------------------------------------------------------

public sealed record WarehouseInfo(Guid Id, string Code, bool IsActive, bool UsesLocations);

public sealed record LocationInfo(Guid Id, Guid WarehouseId, bool IsActive, int PickSequence);

/// <summary>Implemented by the Warehouse module.</summary>
public interface IWarehouseDirectory
{
    Task<WarehouseInfo?> FindWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>The tenant's default warehouse; created ("Ana Depo", code MAIN) when the tenant has none.</summary>
    Task<WarehouseInfo> GetOrCreateDefaultAsync(CancellationToken cancellationToken);

    /// <summary>Locations by id, any warehouse of the tenant (callers check <see cref="LocationInfo.WarehouseId"/>).</summary>
    Task<IReadOnlyDictionary<Guid, LocationInfo>> FindLocationsAsync(IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken);
}

/// <summary>Stock identity is the Catalog variant (data-model §5.3).</summary>
public sealed record VariantInfo(Guid Id, string Sku, bool IsActive, bool AllowsFraction);

/// <summary>Implemented over the Catalog module.</summary>
public interface IVariantDirectory
{
    Task<IReadOnlyDictionary<Guid, VariantInfo>> FindVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>
    /// Text search for stock screens: variants (any status) whose SKU, variant name or product name contains
    /// <paramref name="term"/> (case-insensitive) or whose barcode equals it. Returns at most <paramref name="max"/> ids
    /// (callers pass their limit + 1 to detect an over-broad term).
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchVariantIdsAsync(string term, int max, CancellationToken cancellationToken);
}

/// <summary>Implemented over the tenant settings (Identity). Returns one of <see cref="NegativeStockPolicies"/>.</summary>
public interface INegativeStockPolicyProvider
{
    Task<string> GetPolicyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by the Warehouse module: the variants in a user's task context (roles-permissions footnote 5 —
/// lines of active picking tasks assigned to them or waiting in the pool).
/// </summary>
public interface ITaskContextVariants
{
    Task<IReadOnlyList<Guid>> VariantsForUserAsync(Guid userId, CancellationToken cancellationToken);
}

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

    /// <summary>
    /// Transfer legs (§4.7, OD-01): each move takes stock out of one point (<c>transfer_out</c>) and into another
    /// (<c>transfer_in</c>) under one <c>transfer_group_id</c>, or writes it off (<c>adjustment_out</c>, no group) when
    /// <see cref="TransferMove.To"/> is null. A point is a (warehouse, location) or the in-transit stock of the transfer
    /// (<see cref="StockPoint.InTransit"/>). Never goes below zero, whatever the negative-stock policy: a real point
    /// gives only its <b>available</b> stock (reserved stock never moves), the in-transit point only what it holds.
    /// </summary>
    Task<InventoryResult<TransferResult>> TransferAsync(TransferStock command, CancellationToken cancellationToken);

    /// <summary>
    /// Count correction (§4.8): per counted line one <c>count_correction</c> movement of
    /// counted − (system balance when the line was counted), so movements after the count are kept and movements
    /// before it are not double counted. Never leaves on_hand below zero or below reserved (409 <c>count.below_reserved</c>).
    /// </summary>
    Task<InventoryResult<CountResult>> ApplyCountAsync(ApplyCount command, CancellationToken cancellationToken);
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

    /// <summary>
    /// Balance rows of one warehouse (never in-transit stock), optionally narrowed to locations and/or variants,
    /// ordered by (variant, location). At most <paramref name="max"/> rows (callers pass their limit + 1).
    /// </summary>
    Task<IReadOnlyList<StockLevel>> LevelsAsync(
        Guid warehouseId, IReadOnlyCollection<Guid>? locationIds, IReadOnlyCollection<Guid>? variantIds, int max, CancellationToken cancellationToken);

    /// <summary>The given keys of one warehouse that have any ledger movement after their <c>Since</c> instant.</summary>
    Task<IReadOnlySet<(Guid VariantId, Guid? LocationId)>> MovedSinceAsync(
        Guid warehouseId, IReadOnlyCollection<(Guid VariantId, Guid? LocationId, DateTimeOffset Since)> keys, CancellationToken cancellationToken);
}

/// <summary>One balance row: physical (on_hand) and reserved quantity at a (variant, warehouse, location).</summary>
public sealed record StockLevel(Guid VariantId, Guid? LocationId, decimal OnHand, decimal Reserved);

/// <summary>
/// Minimum stock (critical stock) questions other modules may ask (PRD CAT-12, REP-03; data-model §5.13). A variant's
/// <b>effective minimum</b> depends on the scope:
/// <list type="bullet">
/// <item>one warehouse: the warehouse override of the variant, else its tenant-wide default (warehouse NULL);</item>
/// <item>all warehouses (<c>warehouseId</c> null): the tenant-wide default, else the sum of the variant's warehouse
/// overrides.</item>
/// </list>
/// Available = Σ on_hand − Σ reserved over the scope's balance rows; a variant without any balance row has 0.
/// A variant is <b>critical</b> when it has an effective minimum and available ≤ minimum.
/// Unlocked reads in the request transaction (RLS + explicit tenant filter).
/// </summary>
public interface IStockThresholdQueries
{
    /// <summary>Effective minimum of each given variant that has one in the scope.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> EffectiveMinimumsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>Variants (any catalog status) that are critical in the scope.</summary>
    Task<IReadOnlyList<Guid>> CriticalVariantsAsync(Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>
    /// The complement used when variants without a minimum also count when out of stock: variants whose available is
    /// above their effective minimum, or above 0 when they have none. Every other active variant is critical or out of stock.
    /// </summary>
    Task<IReadOnlyList<Guid>> VariantsAboveMinimumOrZeroAsync(Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>True when any variant has an effective minimum in the scope.</summary>
    Task<bool> AnyThresholdAsync(Guid? warehouseId, CancellationToken cancellationToken);
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
public sealed record AdjustStock(
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    string Type,
    decimal Quantity,
    string Reason,
    string? CorrelationId = null);

public sealed record ReserveStock(
    Guid OrderId,
    Guid WarehouseId,
    IReadOnlyList<ReserveLine> Lines,
    string? CorrelationId = null);

public sealed record ReserveLine(Guid OrderLineId, Guid VariantId, decimal Quantity);

/// <param name="OrderLineIds">Null = every active reservation of the order.</param>
public sealed record ReleaseReservations(Guid OrderId, IReadOnlyList<Guid>? OrderLineIds = null, string? CorrelationId = null);

public sealed record IssueStock(Guid ShipmentId, Guid WarehouseId, IReadOnlyList<IssueLine> Lines, string? CorrelationId = null);

public sealed record IssueLine(Guid ShipmentLineId, Guid OrderLineId, Guid VariantId, Guid? LocationId, decimal Quantity);

/// <summary>
/// Where stock sits for a transfer leg: a warehouse (+ location in located warehouses), or — <see cref="IsTransit"/> —
/// the virtual in-transit point of one transfer ("Yolda"). In-transit stock is a balance row keyed by the transfer id
/// in place of a warehouse id and flagged <c>is_transit</c>: it belongs to no warehouse, so it is never available or
/// reservable anywhere, yet the ledger still accounts for every unit.
/// </summary>
public sealed record StockPoint(Guid WarehouseId, Guid? LocationId, bool IsTransit = false)
{
    public static StockPoint InTransit(Guid transferId) => new(transferId, null, true);

    public static StockPoint At(Guid warehouseId, Guid? locationId) => new(warehouseId, locationId);
}

/// <param name="TransferId">The source document: <c>source_id</c> of every movement and key of its in-transit point.</param>
/// <param name="Reason">Stored on the movements (e.g. the loss reason of a write-off).</param>
public sealed record TransferStock(
    Guid TransferId,
    IReadOnlyList<TransferMove> Moves,
    string? Reason = null,
    string? CorrelationId = null);

/// <param name="MoveId">Unique per leg (<c>source_line_id</c>): the ledger rejects applying the same leg twice.</param>
/// <param name="To">Null = write-off of <paramref name="Quantity"/> from <paramref name="From"/> (<c>adjustment_out</c>).</param>
public sealed record TransferMove(Guid MoveId, Guid VariantId, StockPoint From, StockPoint? To, decimal Quantity);

/// <param name="Lines">Counted lines only; uncounted lines are not applied.</param>
public sealed record ApplyCount(Guid StockCountId, Guid WarehouseId, IReadOnlyList<CountLine> Lines, string? CorrelationId = null);

/// <param name="CountedAt">When the quantity was counted: movements after it are added to the counted quantity.</param>
public sealed record CountLine(Guid CountLineId, Guid VariantId, Guid? LocationId, decimal CountedQuantity, DateTimeOffset CountedAt);

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
    IReadOnlyList<StockShortage>? Shortages = null)
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

public sealed record TransferResult(Guid TransferId, IReadOnlyList<MovementResult> Movements);

/// <param name="SystemAtCount">on_hand when the line was counted (current on_hand − movements after the count).</param>
/// <param name="Delta">counted − <paramref name="SystemAtCount"/>: the movement quantity (0 = no movement written).</param>
public sealed record CountLineResult(Guid CountLineId, decimal SystemAtCount, decimal OnHandBefore, decimal Delta, Guid? MovementId);

public sealed record CountResult(Guid StockCountId, IReadOnlyList<CountLineResult> Lines, IReadOnlyList<MovementResult> Movements);

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

/// <summary>
/// Implemented by the Warehouse module: the variants in a user's task context (roles-permissions footnote 5 —
/// lines of active picking tasks assigned to them or waiting in the pool).
/// </summary>
public interface ITaskContextVariants
{
    Task<IReadOnlyList<Guid>> VariantsForUserAsync(Guid userId, CancellationToken cancellationToken);
}

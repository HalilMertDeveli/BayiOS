namespace BayiOS.Modules.Inventory.Contracts;

/// <summary>Keyset page metadata (api-strategy §5.1). <c>nextCursor</c> is opaque to clients.</summary>
public sealed record InventoryPageInfo(string? NextCursor, bool HasMore, int Limit);

/// <summary>A stock balance row: <c>available = onHand − reserved</c> (never stored).</summary>
/// <param name="MinQuantity">
/// Effective minimum stock of the variant in the row's warehouse (warehouse override, else the variant's default);
/// null when none is set.
/// </param>
/// <param name="BelowMinimum">
/// True when the variant's available stock summed over every bin of the row's warehouse is ≤ <paramref name="MinQuantity"/>
/// ("kritik"). Always false without a minimum.
/// </param>
public sealed record BalanceResponse(
    Guid Id,
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    long Version,
    DateTimeOffset? UpdatedAt,
    decimal? MinQuantity,
    bool BelowMinimum);

public sealed record BalancePageResponse(IReadOnlyList<BalanceResponse> Items, InventoryPageInfo Page);

public sealed record MovementPageResponse(IReadOnlyList<MovementResult> Items, InventoryPageInfo Page);

/// <summary>
/// Minimum stock of a variant (#137). <paramref name="WarehouseId"/> null = the variant's tenant-wide default; a
/// warehouse row overrides the default in that warehouse.
/// </summary>
/// <param name="Version">Weak ETag value for If-Match.</param>
public sealed record StockThresholdResponse(
    Guid Id,
    Guid VariantId,
    Guid? WarehouseId,
    decimal MinQuantity,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record StockThresholdPageResponse(IReadOnlyList<StockThresholdResponse> Items, InventoryPageInfo Page);

/// <summary>Creates or replaces the threshold of (variant, warehouse); last write wins (use PUT /{id} + If-Match to guard).</summary>
/// <param name="WarehouseId">Omit for the variant's tenant-wide default.</param>
/// <param name="MinQuantity">0 or more, at most 3 decimals (whole numbers for units without fractions).</param>
public sealed record SetStockThresholdRequest(Guid? VariantId, Guid? WarehouseId, decimal? MinQuantity);

public sealed record UpdateStockThresholdRequest(decimal? MinQuantity);

/// <param name="Items">1–500 rows, each (variantId, warehouseId) at most once. All or nothing.</param>
public sealed record BulkStockThresholdRequest(IReadOnlyList<BulkStockThresholdItem>? Items);

/// <param name="WarehouseId">Omit for the variant's tenant-wide default.</param>
/// <param name="MinQuantity">Required unless <paramref name="Remove"/>.</param>
/// <param name="Remove">True removes the threshold (no-op when there is none); minQuantity must then be omitted.</param>
public sealed record BulkStockThresholdItem(Guid? VariantId, Guid? WarehouseId, decimal? MinQuantity, bool? Remove);

/// <param name="Items">The thresholds that exist after the request for the rows that set one (removed rows are absent).</param>
public sealed record BulkStockThresholdResponse(int Created, int Updated, int Removed, int Unchanged, IReadOnlyList<StockThresholdResponse> Items);

/// <param name="WarehouseId">Omit to receive into the tenant's default warehouse (created on first use).</param>
/// <param name="Reference">Delivery note / supplier reference, stored on every movement.</param>
public sealed record CreateReceiptRequest(Guid? WarehouseId, string? Reference, IReadOnlyList<ReceiptLineRequest>? Lines);

/// <param name="LocationId">Required in warehouses that use locations; must be omitted otherwise.</param>
public sealed record ReceiptLineRequest(Guid? VariantId, Guid? LocationId, decimal? Quantity, decimal? UnitCost);

/// <param name="Type">adjustment_in, adjustment_out, damage_out or return_in.</param>
/// <param name="Quantity">Positive; the direction comes from the type.</param>
/// <param name="Reason">Mandatory audit reason.</param>
/// <param name="ConfirmNegativeStock">
/// Deprecated and ignored: stock never goes below zero (owner decision OD-09), so there is nothing to confirm. Kept so
/// existing clients that still send it are not rejected.
/// </param>
public sealed record CreateAdjustmentRequest(
    Guid? VariantId,
    Guid? WarehouseId,
    Guid? LocationId,
    string? Type,
    decimal? Quantity,
    string? Reason,
    bool? ConfirmNegativeStock = null);

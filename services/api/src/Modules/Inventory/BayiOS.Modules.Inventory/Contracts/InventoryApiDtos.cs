namespace BayiOS.Modules.Inventory.Contracts;

/// <summary>Keyset page metadata (api-strategy §5.1). <c>nextCursor</c> is opaque to clients.</summary>
public sealed record InventoryPageInfo(string? NextCursor, bool HasMore, int Limit);

/// <summary>A stock balance row: <c>available = onHand − reserved</c> (never stored).</summary>
public sealed record BalanceResponse(
    Guid Id,
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    long Version,
    DateTimeOffset? UpdatedAt);

public sealed record BalancePageResponse(IReadOnlyList<BalanceResponse> Items, InventoryPageInfo Page);

public sealed record MovementPageResponse(IReadOnlyList<MovementResult> Items, InventoryPageInfo Page);

/// <param name="WarehouseId">Omit to receive into the tenant's default warehouse (created on first use).</param>
/// <param name="Reference">Delivery note / supplier reference, stored on every movement.</param>
public sealed record CreateReceiptRequest(Guid? WarehouseId, string? Reference, IReadOnlyList<ReceiptLineRequest>? Lines);

/// <param name="LocationId">Required in warehouses that use locations; must be omitted otherwise.</param>
public sealed record ReceiptLineRequest(Guid? VariantId, Guid? LocationId, decimal? Quantity, decimal? UnitCost);

/// <param name="Type">adjustment_in, adjustment_out, damage_out or return_in.</param>
/// <param name="Quantity">Positive; the direction comes from the type.</param>
/// <param name="Reason">Mandatory audit reason.</param>
/// <param name="ConfirmNegativeStock">Confirms going below available under the tenant's <c>warn</c> policy.</param>
public sealed record CreateAdjustmentRequest(
    Guid? VariantId,
    Guid? WarehouseId,
    Guid? LocationId,
    string? Type,
    decimal? Quantity,
    string? Reason,
    bool? ConfirmNegativeStock);

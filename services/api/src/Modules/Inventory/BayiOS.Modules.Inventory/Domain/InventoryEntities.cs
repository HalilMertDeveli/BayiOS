using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Inventory.Domain;

// EF Core read models of the inventory schema (data-model §5.7). Writes go through the stock engine's SQL
// (Application/StockStore.cs): row locks, INSERT … ON CONFLICT and RETURNING are not expressible through tracking.

/// <summary>inventory.inventory_movements — append-only ledger; the single source of truth for on_hand.</summary>
internal sealed class InventoryMovement : ITenantOwned
{
    private InventoryMovement()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid? LocationId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal? UnitCost { get; private set; }

    public string SourceType { get; private set; } = string.Empty;

    public Guid? SourceId { get; private set; }

    public Guid? SourceLineId { get; private set; }

    public Guid? TransferGroupId { get; private set; }

    public string? Reason { get; private set; }

    public decimal BalanceAfter { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? CreatedBy { get; private set; }
}

/// <summary>inventory.inventory_balances — derived from the ledger, updated in the movement's transaction.</summary>
internal sealed class InventoryBalance : ITenantOwned
{
    private InventoryBalance()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid? LocationId { get; private set; }

    public decimal OnHand { get; private set; }

    public decimal Reserved { get; private set; }

    /// <summary>
    /// Exemption from the DB CHECK (on_hand &gt;= 0 AND reserved &lt;= on_hand). Always false for new rows and after any
    /// worsening change (OD-09: stock never goes below zero); true only on a row still negative from before OD-09.
    /// </summary>
    public bool AllowNegative { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }
}

internal static class ReservationStatuses
{
    public const string Active = "active";
    public const string Consumed = "consumed";
    public const string Released = "released";

    public static readonly IReadOnlyList<string> All = [Active, Consumed, Released];
}

/// <summary>inventory.stock_reservations — per order line (and location in located warehouses).</summary>
internal sealed class StockReservation : ITenantOwned
{
    private StockReservation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid OrderLineId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid? LocationId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal ConsumedQuantity { get; private set; }

    public decimal ReleasedQuantity { get; private set; }

    public string Status { get; private set; } = ReservationStatuses.Active;

    public string? CorrelationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }
}

/// <summary>
/// inventory.stock_thresholds — minimum stock of a variant (PRD CAT-12, REP-03). <see cref="WarehouseId"/> null is the
/// tenant-wide default of the variant; a row with a warehouse overrides the default in that warehouse. Configuration,
/// not stock: changing it writes no movement. Written by <c>ThresholdStore</c> SQL (version + 1 per change).
/// </summary>
internal sealed class StockThreshold : ITenantOwned
{
    private StockThreshold()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid? WarehouseId { get; private set; }

    public decimal MinQuantity { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }
}

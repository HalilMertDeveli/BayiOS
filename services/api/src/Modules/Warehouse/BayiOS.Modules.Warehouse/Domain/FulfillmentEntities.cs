using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Warehouse.Domain;

/// <summary>A Warehouse document that belongs to one order in one warehouse.</summary>
internal interface IHasOrder
{
    string OrderNumber { get; }

    Guid WarehouseId { get; }
}

/// <summary>pick: collect an order's reserved goods; putaway: return picked goods of a cancelled/closed order (ORD-07).</summary>
internal static class PickingTaskTypes
{
    public const string Pick = "pick";
    public const string Putaway = "putaway";

    public static readonly IReadOnlyList<string> All = [Pick, Putaway];
}

/// <summary>data-model §5.6 / WHT-06. Active = open or in_progress; the rest are terminal.</summary>
internal static class PickingTaskStatuses
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string CompletedWithShortage = "completed_with_shortage";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Open, InProgress, Completed, CompletedWithShortage, Cancelled];

    public static readonly IReadOnlyList<string> Active = [Open, InProgress];

    public static bool IsActive(string status) => status is Open or InProgress;
}

/// <summary>WHT-06 shortage reasons: not on the shelf, damaged, or fewer than expected.</summary>
internal static class ShortageReasons
{
    public const string NotFound = "not_found";
    public const string Damaged = "damaged";
    public const string Insufficient = "insufficient";

    public static readonly IReadOnlyList<string> All = [NotFound, Damaged, Insufficient];
}

/// <summary>draft (from completed picking) → packed (optional pack check, WHT-07) → confirmed (stock issued); or cancelled.</summary>
internal static class ShipmentStatuses
{
    public const string Draft = "draft";
    public const string Packed = "packed";
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Draft, Packed, Confirmed, Cancelled];

    public static readonly IReadOnlyList<string> Unconfirmed = [Draft, Packed];
}

internal static class FulfillmentActions
{
    public const string Assign = "assign";
    public const string Claim = "claim";
    public const string Start = "start";
    public const string Pick = "pick";
    public const string ReportShortage = "report_shortage";
    public const string Complete = "complete";
    public const string Cancel = "cancel";
    public const string Pack = "pack";
    public const string Confirm = "confirm";
}

/// <summary>
/// The single place that decides picking-task and shipment transitions. A null answer is
/// <c>409 picking.invalid_transition</c> / <c>shipment.invalid_transition</c>.
/// <para>Task: open → in_progress (start, first scan or shortage) → completed | completed_with_shortage (complete,
/// every line picked or reported short); open / in_progress → cancelled (order cancelled or short-closed).
/// Assignment changes only while active. Putaway tasks have no scans: open / in_progress → completed.</para>
/// <para>Shipment: draft → packed (pack check) → confirmed; draft → confirmed (pack check is optional, WHT-07 P1);
/// shortages are reported on draft only; draft / packed → cancelled.</para>
/// </summary>
internal static class FulfillmentStateMachine
{
    /// <param name="withShortage">For <c>complete</c> of a pick task: some line was reported short.</param>
    public static string? NextTask(string action, string type, string status, bool withShortage = false) => (action, type, status) switch
    {
        (FulfillmentActions.Assign or FulfillmentActions.Claim, _, PickingTaskStatuses.Open or PickingTaskStatuses.InProgress) => status,
        (FulfillmentActions.Start or FulfillmentActions.Pick or FulfillmentActions.ReportShortage, PickingTaskTypes.Pick,
            PickingTaskStatuses.Open or PickingTaskStatuses.InProgress) => PickingTaskStatuses.InProgress,
        (FulfillmentActions.Start, PickingTaskTypes.Putaway, PickingTaskStatuses.Open or PickingTaskStatuses.InProgress) => PickingTaskStatuses.InProgress,
        (FulfillmentActions.Complete, PickingTaskTypes.Pick, PickingTaskStatuses.Open or PickingTaskStatuses.InProgress) =>
            withShortage ? PickingTaskStatuses.CompletedWithShortage : PickingTaskStatuses.Completed,
        (FulfillmentActions.Complete, PickingTaskTypes.Putaway, PickingTaskStatuses.Open or PickingTaskStatuses.InProgress) => PickingTaskStatuses.Completed,
        (FulfillmentActions.Cancel, PickingTaskTypes.Pick, PickingTaskStatuses.Open or PickingTaskStatuses.InProgress) => PickingTaskStatuses.Cancelled,
        _ => null,
    };

    public static string? NextShipment(string action, string status) => (action, status) switch
    {
        (FulfillmentActions.Pack, ShipmentStatuses.Draft) => ShipmentStatuses.Packed,
        (FulfillmentActions.ReportShortage, ShipmentStatuses.Draft) => ShipmentStatuses.Draft,
        (FulfillmentActions.Confirm, ShipmentStatuses.Draft or ShipmentStatuses.Packed) => ShipmentStatuses.Confirmed,
        (FulfillmentActions.Cancel, ShipmentStatuses.Draft or ShipmentStatuses.Packed) => ShipmentStatuses.Cancelled,
        _ => null,
    };
}

/// <summary>
/// warehouse.picking_tasks (data-model §5.6): one order's picking (or putaway) work in one warehouse. At most one
/// active pick task per order (partial unique index). <see cref="AssignedTo"/> null = in the shared pool (WHT-02).
/// </summary>
internal sealed class PickingTask : WarehouseModuleEntity, IHasOrder
{
    private PickingTask()
    {
    }

    public string Type { get; private init; } = PickingTaskTypes.Pick;

    /// <summary>→ orders.orders (no cross-module FK).</summary>
    public Guid OrderId { get; private init; }

    /// <summary>Order number snapshot for the scanner screen (staff cannot read orders, roles-permissions K⁸).</summary>
    public string OrderNumber { get; private init; } = string.Empty;

    public Guid WarehouseId { get; private init; }

    public string Status { get; set; } = PickingTaskStatuses.Open;

    public Guid? AssignedTo { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CompletedBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public List<PickingTaskLine> Lines { get; private init; } = [];

    public static PickingTask Create(
        Guid tenantId, string type, Guid orderId, string orderNumber, Guid warehouseId, Guid? userId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Type = type,
            OrderId = orderId,
            OrderNumber = orderNumber,
            WarehouseId = warehouseId,
            CreatedAt = now,
            CreatedBy = userId,
        };
}

/// <summary>
/// warehouse.picking_task_lines: one per reservation (order line + location), in walking order
/// (<see cref="Sequence"/> = location pick_sequence, location code, order line no). Invariant (CHECK):
/// <c>qty_picked + qty_short ≤ qty_to_pick</c>.
/// </summary>
internal sealed class PickingTaskLine : ITenantOwned
{
    private PickingTaskLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid PickingTaskId { get; private init; }

    public int Sequence { get; private init; }

    /// <summary>→ orders.order_lines (no cross-module FK).</summary>
    public Guid OrderLineId { get; private init; }

    public Guid VariantId { get; private init; }

    public string Sku { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;

    public string Unit { get; private init; } = string.Empty;

    /// <summary>The reserved location (null in a warehouse without locations).</summary>
    public Guid? LocationId { get; private init; }

    public decimal QtyToPick { get; private init; }

    public decimal QtyPicked { get; set; }

    public decimal QtyShort { get; set; }

    public string? ShortageReason { get; set; }

    public decimal Remaining => QtyToPick - QtyPicked - QtyShort;

    public static PickingTaskLine Create(
        PickingTask task, int sequence, Guid orderLineId, Guid variantId, string sku, string name, string unit, Guid? locationId, decimal quantity) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = task.TenantId,
            PickingTaskId = task.Id,
            Sequence = sequence,
            OrderLineId = orderLineId,
            VariantId = variantId,
            Sku = sku,
            Name = name,
            Unit = unit,
            LocationId = locationId,
            QtyToPick = quantity,
        };
}

/// <summary>
/// warehouse.shipments (data-model §5.6): created as a draft when a pick task completes, holding the picked
/// quantities. Confirming it is the only way stock leaves the building: Orders issues exactly these lines
/// (<c>shipment_out</c>, source_id = this id). At most one unconfirmed shipment per order (partial unique index).
/// </summary>
internal sealed class Shipment : WarehouseModuleEntity, IHasOrder
{
    private Shipment()
    {
    }

    /// <summary>Tenant-unique: <c>{order number}-S{n}</c>.</summary>
    public string Number { get; private init; } = string.Empty;

    public Guid OrderId { get; private init; }

    public string OrderNumber { get; private init; } = string.Empty;

    public Guid? PickingTaskId { get; private init; }

    public Guid WarehouseId { get; private init; }

    public string Status { get; set; } = ShipmentStatuses.Draft;

    public string? Carrier { get; set; }

    public string? TrackingNumber { get; set; }

    public int? PackageCount { get; set; }

    public DateTimeOffset? PackedAt { get; set; }

    public Guid? PackedBy { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    public Guid? ConfirmedBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public List<ShipmentLine> Lines { get; private init; } = [];

    public List<ShipmentPackage> Packages { get; private init; } = [];

    public static Shipment Create(
        Guid tenantId, string number, Guid orderId, string orderNumber, Guid? pickingTaskId, Guid warehouseId, Guid userId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Number = number,
            OrderId = orderId,
            OrderNumber = orderNumber,
            PickingTaskId = pickingTaskId,
            WarehouseId = warehouseId,
            CreatedAt = now,
            CreatedBy = userId,
        };
}

/// <summary>
/// warehouse.shipment_lines: one per picked (order line, location). <see cref="Quantity"/> is what will ship
/// (picked minus shortages found while packing); id = the movement's <c>source_line_id</c>.
/// </summary>
internal sealed class ShipmentLine : ITenantOwned
{
    private ShipmentLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid ShipmentId { get; private init; }

    public Guid OrderLineId { get; private init; }

    public Guid VariantId { get; private init; }

    public string Sku { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;

    public string Unit { get; private init; } = string.Empty;

    public Guid? LocationId { get; private init; }

    public decimal PickedQuantity { get; private init; }

    public decimal Quantity { get; set; }

    /// <summary>Set by the pack check; equals <see cref="Quantity"/> once packed.</summary>
    public decimal? PackedQuantity { get; set; }

    public decimal ShortQuantity { get; set; }

    public string? ShortageReason { get; set; }

    public static ShipmentLine Create(Shipment shipment, PickingTaskLine picked) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = shipment.TenantId,
        ShipmentId = shipment.Id,
        OrderLineId = picked.OrderLineId,
        VariantId = picked.VariantId,
        Sku = picked.Sku,
        Name = picked.Name,
        Unit = picked.Unit,
        LocationId = picked.LocationId,
        PickedQuantity = picked.QtyPicked,
        Quantity = picked.QtyPicked,
    };
}

/// <summary>warehouse.shipment_packages: a parcel recorded by the pack check (ORD-15 package count).</summary>
internal sealed class ShipmentPackage : ITenantOwned
{
    private ShipmentPackage()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid ShipmentId { get; private init; }

    public int PackageNo { get; private init; }

    public List<ShipmentPackageItem> Items { get; private init; } = [];

    public static ShipmentPackage Create(Shipment shipment, int packageNo) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = shipment.TenantId,
        ShipmentId = shipment.Id,
        PackageNo = packageNo,
    };
}

/// <summary>warehouse.shipment_package_items: quantity of one shipment line inside one package.</summary>
internal sealed class ShipmentPackageItem : ITenantOwned
{
    private ShipmentPackageItem()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid PackageId { get; private init; }

    public Guid ShipmentLineId { get; private init; }

    public decimal Quantity { get; private init; }

    public static ShipmentPackageItem Create(ShipmentPackage package, Guid shipmentLineId, decimal quantity) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = package.TenantId,
        PackageId = package.Id,
        ShipmentLineId = shipmentLineId,
        Quantity = quantity,
    };
}

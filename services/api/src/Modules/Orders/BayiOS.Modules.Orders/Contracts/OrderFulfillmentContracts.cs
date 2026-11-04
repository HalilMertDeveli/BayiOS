namespace BayiOS.Modules.Orders.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Fulfilment seam between Orders and Warehouse (inventory-and-orders §3.1 #4–#6, domain-model §12).
// Warehouse owns picking tasks and shipments; Orders owns the order status and is the ONLY caller of
// IInventoryService.IssueAsync. Both directions run on the request UnitOfWork, inside the same transaction.
// ------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Implemented by Orders, called by Warehouse. Every method locks the order header first (lock hierarchy step 1)
/// and decides transitions through the order state machine — Warehouse never writes order tables.
/// </summary>
public interface IOrderFulfillment
{
    /// <summary>Locks the order header (<c>FOR UPDATE</c>) and returns its fulfilment view; 404 when unknown.</summary>
    Task<FulfillmentResult<FulfillmentOrder>> LockAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>approved → picking (§3.1 #4). No-op when the order is already picking or packed.</summary>
    Task<FulfillmentResult<FulfillmentOrder>> StartPickingAsync(Guid orderId, string? correlationId, CancellationToken cancellationToken);

    /// <summary>picking → packed (§3.1 #5, picking completed). No-op when already packed.</summary>
    Task<FulfillmentResult<FulfillmentOrder>> MarkPackedAsync(Guid orderId, string? correlationId, CancellationToken cancellationToken);

    /// <summary>Flags the order (has_shortage + timeline entry) while it holds reservations; status is unchanged.</summary>
    Task<FulfillmentResult<FulfillmentOrder>> ReportShortageAsync(Guid orderId, string reason, string? correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// §4.6: issues exactly the given quantities from the order's reservations (<c>shipment_out</c>, source = the
    /// Warehouse shipment), records the order-side shipment and moves the order (shipped when nothing stays open,
    /// else fulfillment <c>partial</c>).
    /// </summary>
    Task<FulfillmentResult<OrderShipmentRecorded>> ShipAsync(ShipOrderCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by Warehouse, called by Orders inside the order transition (domain-model §12 "same transaction").
/// </summary>
public interface IOrderFulfillmentListener
{
    /// <summary>OrderApproved: create the picking task from the reservations just made (WHT-01).</summary>
    Task OnApprovedAsync(OrderApprovedNotice notice, CancellationToken cancellationToken);

    /// <summary>
    /// The order's reservations were released (cancel §4.4 or short-close 6s): cancel open picking tasks and
    /// unconfirmed shipments; picked goods get a putaway task (PRD ORD-07, no ledger movement).
    /// </summary>
    Task OnReservationsReleasedAsync(Guid orderId, string reason, CancellationToken cancellationToken);
}

/// <summary>Why <see cref="IOrderFulfillmentListener.OnReservationsReleasedAsync"/> is called.</summary>
public static class FulfillmentReleaseReasons
{
    /// <summary>The order was cancelled (§4.4).</summary>
    public const string Cancelled = "order_cancelled";

    /// <summary>The rest of a partially shipped order was closed (short-close, §3.1 6s).</summary>
    public const string Closed = "order_closed";
}

public sealed record OrderApprovedNotice(Guid OrderId, string Number, Guid WarehouseId, IReadOnlyList<FulfillmentOrderLine> Lines);

/// <param name="IsFulfillable">approved, picking or packed: the order holds reservations and may be picked/shipped.</param>
public sealed record FulfillmentOrder(
    Guid Id,
    string Number,
    Guid WarehouseId,
    string Status,
    string FulfillmentStatus,
    bool IsFulfillable,
    long Version,
    IReadOnlyList<FulfillmentOrderLine> Lines);

public sealed record FulfillmentOrderLine(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string Sku,
    string Name,
    string Unit,
    decimal Quantity,
    decimal Reserved,
    decimal Shipped,
    decimal Cancelled);

/// <param name="ShipmentId">The Warehouse shipment: movement <c>source_id</c> and order-side shipment id.</param>
public sealed record ShipOrderCommand(
    Guid OrderId,
    Guid ShipmentId,
    IReadOnlyList<ShipOrderLine> Lines,
    string? Carrier,
    string? TrackingNumber,
    int? PackageCount,
    string? CorrelationId);

/// <param name="ShipmentLineId">Movement <c>source_line_id</c>.</param>
public sealed record ShipOrderLine(Guid ShipmentLineId, Guid OrderLineId, Guid? LocationId, decimal Quantity);

/// <param name="MovementIds">The <c>shipment_out</c> movements written for this shipment.</param>
public sealed record OrderShipmentRecorded(Guid OrderId, string Status, string FulfillmentStatus, long Version, IReadOnlyList<Guid> MovementIds);

public sealed record FulfillmentError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null);

public sealed record FulfillmentResult<T>(T? Value, FulfillmentError? Error)
{
    public static implicit operator FulfillmentResult<T>(T value) => new(value, null);

    public static implicit operator FulfillmentResult<T>(FulfillmentError error) => new(default, error);
}

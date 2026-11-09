namespace BayiOS.Modules.Warehouse.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Picking, packing and shipment API (api-strategy §12.8) — small payloads for a one-handed mobile scanner client.
// No prices anywhere: warehouse_staff may not read them (roles-permissions §3.2).
// ------------------------------------------------------------------------------------------------------------------

public sealed record FulfillmentPageInfo(string? NextCursor, bool HasMore, int Limit);

/// <param name="AssignedTo">null = in the shared pool (claimable).</param>
/// <param name="AssigneeName">Display name of <paramref name="AssignedTo"/> (full name, else e-mail local part).</param>
/// <param name="QuantityToPick">Σ over the lines (base units).</param>
public sealed record PickingTaskSummaryResponse(
    Guid Id,
    string Type,
    string Status,
    Guid OrderId,
    string OrderNumber,
    Guid WarehouseId,
    Guid? AssignedTo,
    string? AssigneeName,
    int LineCount,
    decimal QuantityToPick,
    decimal QuantityPicked,
    decimal QuantityShort,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long Version);

/// <param name="Sequence">Walking order: location pick sequence, then location code, then order line.</param>
/// <param name="Barcode">Current Catalog barcode of the variant (scan target), if any.</param>
public sealed record PickingTaskLineResponse(
    Guid Id,
    int Sequence,
    Guid OrderLineId,
    Guid VariantId,
    string Sku,
    string Name,
    string Unit,
    string? Barcode,
    Guid? LocationId,
    string? LocationCode,
    string? LocationBarcode,
    decimal QuantityToPick,
    decimal QuantityPicked,
    decimal QuantityShort,
    decimal QuantityRemaining,
    string? ShortageReason);

/// <param name="ShipmentId">The draft/confirmed shipment created when this pick task completed.</param>
public sealed record PickingTaskResponse(PickingTaskSummaryResponse Task, IReadOnlyList<PickingTaskLineResponse> Lines, Guid? ShipmentId);

public sealed record PickingTaskPageResponse(IReadOnlyList<PickingTaskSummaryResponse> Items, FulfillmentPageInfo Page);

/// <summary>The caller's active tasks first, then the claimable pool; oldest first within each.</summary>
public sealed record MyPickingTasksResponse(IReadOnlyList<PickingTaskSummaryResponse> Items);

/// <summary>Creates the pick task of an approved order (normally automatic on approval; this re-creates for the remainder).</summary>
public sealed record CreatePickingTaskRequest(Guid? OrderId);

/// <param name="UserId">An active member of the tenant whose role may pick.</param>
public sealed record AssignPickingTaskRequest(Guid? UserId);

/// <summary>
/// One scan. <paramref name="Code"/> = variant barcode (or SKU typed by hand). <paramref name="Location"/> = shelf
/// barcode or code, required in warehouses that use locations. <paramref name="Quantity"/> defaults to 1 (+1 per scan).
/// </summary>
/// <param name="LineId">Optional: the task line the picker has on screen; otherwise the first open matching line.</param>
public sealed record PickScanRequest(string? Code, string? Location, decimal? Quantity, Guid? LineId);

public sealed record PickLineProgress(
    Guid Id,
    Guid VariantId,
    string Sku,
    Guid? LocationId,
    decimal QuantityToPick,
    decimal QuantityPicked,
    decimal QuantityShort,
    decimal QuantityRemaining);

/// <param name="Lines">Only the lines this scan changed.</param>
/// <param name="OpenLineCount">Lines still waiting for picks or a shortage report; 0 = ready to complete.</param>
public sealed record PickScanResponse(Guid TaskId, string Status, long Version, IReadOnlyList<PickLineProgress> Lines, int OpenLineCount);

/// <param name="Quantity">Default: everything still open on the line.</param>
/// <param name="Reason">not_found, damaged or insufficient (WHT-06).</param>
public sealed record ReportShortageRequest(decimal? Quantity, string? Reason);

/// <summary>Pack check (WHT-07): every packed item scanned again into numbered packages.</summary>
public sealed record PackShipmentRequest(IReadOnlyList<PackageInput>? Packages);

public sealed record PackageInput(IReadOnlyList<PackItemInput>? Items);

/// <summary>Either <paramref name="Code"/> (barcode/SKU as scanned) or <paramref name="ShipmentLineId"/>.</summary>
public sealed record PackItemInput(string? Code, Guid? ShipmentLineId, decimal? Quantity);

/// <param name="PackageCount">Default: the number of packages recorded by the pack check.</param>
public sealed record ConfirmShipmentRequest(string? Carrier, string? TrackingNumber, int? PackageCount);

/// <summary>Cancels an unconfirmed (draft or packed) shipment; the picked goods go back to the shelf via a putaway task.</summary>
/// <param name="Reason">Required, at most 500 characters (audit).</param>
public sealed record CancelShipmentRequest(string? Reason);

/// <param name="PutawayTaskId">The putaway task that returns the picked goods to the shelf; null when nothing was on the shipment.</param>
public sealed record CancelShipmentResponse(ShipmentResponse Shipment, Guid? PutawayTaskId);

/// <param name="Barcode">Current Catalog barcode of the variant (pack-check scan target), if any.</param>
/// <param name="Quantity">What ships: picked quantity minus shortages found while packing.</param>
public sealed record ShipmentLineResponse(
    Guid Id,
    Guid OrderLineId,
    Guid VariantId,
    string Sku,
    string Name,
    string Unit,
    string? Barcode,
    Guid? LocationId,
    string? LocationCode,
    decimal PickedQuantity,
    decimal Quantity,
    decimal? PackedQuantity,
    decimal ShortQuantity,
    string? ShortageReason);

public sealed record ShipmentPackageItemResponse(Guid ShipmentLineId, decimal Quantity);

public sealed record ShipmentPackageResponse(Guid Id, int PackageNo, IReadOnlyList<ShipmentPackageItemResponse> Items);

public sealed record ShipmentResponse(
    Guid Id,
    string Number,
    Guid OrderId,
    string OrderNumber,
    Guid? PickingTaskId,
    Guid WarehouseId,
    string Status,
    string? Carrier,
    string? TrackingNumber,
    int? PackageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PackedAt,
    DateTimeOffset? ShippedAt,
    Guid? ConfirmedBy,
    long Version,
    IReadOnlyList<ShipmentLineResponse> Lines,
    IReadOnlyList<ShipmentPackageResponse> Packages);

public sealed record ShipmentSummaryResponse(
    Guid Id,
    string Number,
    Guid OrderId,
    string OrderNumber,
    Guid WarehouseId,
    string Status,
    int LineCount,
    decimal Quantity,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ShippedAt,
    long Version);

public sealed record ShipmentPageResponse(IReadOnlyList<ShipmentSummaryResponse> Items, FulfillmentPageInfo Page);

/// <param name="MovementIds">The <c>shipment_out</c> ledger movements (also <c>GET /inventory/movements?sourceId={shipment id}</c>).</param>
public sealed record ConfirmShipmentResponse(
    ShipmentResponse Shipment,
    string OrderStatus,
    string OrderFulfillmentStatus,
    IReadOnlyList<Guid> MovementIds);

// ------------------------------------------------------------------------------------------------------------------
// Ports the host wires (Catalog / Identity); same request connection and transaction.
// ------------------------------------------------------------------------------------------------------------------

public sealed record FulfillmentVariantInfo(Guid Id, string Sku, string? Barcode, bool AllowsFraction);

/// <summary>Implemented over the Catalog module: what a scanned code means.</summary>
public interface IFulfillmentCatalog
{
    Task<IReadOnlyDictionary<Guid, FulfillmentVariantInfo>> FindVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>The variant whose barcode equals the trimmed code, else whose SKU equals it (SKU rules); null when unknown.</summary>
    Task<Guid?> ResolveCodeAsync(string code, CancellationToken cancellationToken);
}

/// <summary>Implemented over Identity memberships: the active role of a user in the current tenant, else null.</summary>
public interface ITaskAssigneeDirectory
{
    Task<string?> FindActiveRoleAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Display names (full name, else e-mail local part) of members of the current tenant; one batch query.</summary>
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

namespace BayiOS.Modules.Warehouse.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Two-stage warehouse transfers (OD-01, PRD INV-08, user-flows AF-07, US-INV-007) and stock counts (INV-06, AF-06).
// Built for the web and for a handheld: receipts and count entries accept a scanned product code (barcode or SKU)
// and a scanned shelf code instead of ids. Every POST requires an Idempotency-Key; If-Match is honoured when sent.
// ------------------------------------------------------------------------------------------------------------------

/// <param name="Lines">1–500 lines; each (variant, source shelf) once.</param>
public sealed record CreateStockTransferRequest(
    Guid? SourceWarehouseId, Guid? DestinationWarehouseId, string? Note, IReadOnlyList<StockTransferLineRequest>? Lines);

/// <param name="SourceLocationId">Required when the source warehouse uses locations; omitted otherwise.</param>
/// <param name="DestinationLocationId">Planned destination shelf (optional); the receiver may choose another.</param>
public sealed record StockTransferLineRequest(Guid? VariantId, decimal? Quantity, Guid? SourceLocationId, Guid? DestinationLocationId);

/// <param name="Lines">What arrived (1–500 entries). Several receipts are allowed until everything sent has arrived.</param>
public sealed record ReceiveStockTransferRequest(IReadOnlyList<ReceiveStockTransferLineRequest>? Lines);

/// <summary>
/// One received quantity. Address the transfer line by <paramref name="LineId"/>, or by product — <paramref name="VariantId"/>
/// or a scanned <paramref name="Code"/> (barcode, else SKU) — which fills the variant's lines in line order. The
/// destination shelf is <paramref name="LocationId"/> or a scanned <paramref name="LocationCode"/> (shelf barcode or
/// code), else the line's planned shelf; required when the destination uses locations.
/// </summary>
public sealed record ReceiveStockTransferLineRequest(
    Guid? LineId, Guid? VariantId, string? Code, decimal? Quantity, Guid? LocationId, string? LocationCode);

/// <param name="Resolution"><c>lost</c> (write-off from in-transit stock) or <c>return_to_source</c>.</param>
/// <param name="Reason">Required (1–500 characters).</param>
public sealed record CloseStockTransferRequest(string? Resolution, string? Reason);

/// <param name="Reason">Required (1–500 characters).</param>
public sealed record CancelStockTransferRequest(string? Reason);

/// <summary>
/// A line's quantities: <c>quantity</c> sent = received + inTransit + returned + lost. <c>discrepancyQuantity</c> =
/// sent − received once dispatched (the fark raporu: what has not arrived — still on the road, returned or lost).
/// </summary>
public sealed record StockTransferLineResponse(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string Sku,
    Guid? SourceLocationId,
    string? SourceLocationCode,
    Guid? DestinationLocationId,
    string? DestinationLocationCode,
    decimal Quantity,
    decimal ReceivedQuantity,
    decimal InTransitQuantity,
    decimal ReturnedQuantity,
    decimal LostQuantity,
    decimal DiscrepancyQuantity);

/// <param name="Status">draft, in_transit (Yolda), partially_received, received, closed, cancelled.</param>
/// <param name="IsOverdue">In transit for more than 7 days (AF-07 dashboard warning).</param>
/// <param name="HasDiscrepancy">Received or closed with less than was sent (partial receipts included).</param>
/// <param name="Version">Weak ETag value for If-Match.</param>
public sealed record StockTransferSummaryResponse(
    Guid Id,
    string Number,
    Guid SourceWarehouseId,
    string? SourceWarehouseName,
    Guid DestinationWarehouseId,
    string? DestinationWarehouseName,
    string Status,
    string? Note,
    int LineCount,
    decimal Quantity,
    decimal ReceivedQuantity,
    decimal InTransitQuantity,
    decimal ReturnedQuantity,
    decimal LostQuantity,
    decimal DiscrepancyQuantity,
    bool HasDiscrepancy,
    bool IsOverdue,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? LastReceivedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    long Version);

/// <param name="CloseResolution">lost or return_to_source once closed.</param>
/// <param name="Lines">In line order. Stock movements: <c>GET /inventory/movements?sourceId={id}</c>.</param>
public sealed record StockTransferResponse(
    StockTransferSummaryResponse Transfer,
    Guid? CreatedBy,
    Guid? DispatchedBy,
    string? CloseResolution,
    string? CloseReason,
    Guid? ClosedBy,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    IReadOnlyList<StockTransferLineResponse> Lines);

public sealed record StockTransferPageResponse(IReadOnlyList<StockTransferSummaryResponse> Items, FulfillmentPageInfo Page);

/// <param name="MovementIds">The ledger movements this action wrote.</param>
public sealed record StockTransferActionResponse(StockTransferResponse Transfer, IReadOnlyList<Guid> MovementIds);

// ---- Stock counts ----

/// <param name="Scope"><c>full</c> (every stocked shelf of the warehouse), <c>locations</c> or <c>variants</c>.</param>
/// <param name="LocationIds">Scope = locations: 1–500 shelves of the warehouse.</param>
/// <param name="VariantIds">Scope = variants: 1–500 variants.</param>
/// <param name="Blind">Kör sayım: counters without <c>inventory.count.manage</c> never see the system quantity.</param>
public sealed record CreateStockCountRequest(
    Guid? WarehouseId, string? Scope, IReadOnlyList<Guid>? LocationIds, IReadOnlyList<Guid>? VariantIds, bool? Blind, string? Note);

/// <param name="Entries">1–500 entries, applied in order.</param>
public sealed record StockCountEntriesRequest(IReadOnlyList<StockCountEntryRequest>? Entries);

/// <summary>
/// One counted quantity. Address a line by <paramref name="LineId"/>, or by product (<paramref name="VariantId"/> or a
/// scanned <paramref name="Code"/>: barcode, else SKU) and shelf (<paramref name="LocationId"/> or a scanned
/// <paramref name="LocationCode"/>; required in located warehouses). An item found outside the snapshot becomes a new
/// line (within the count's scope). <paramref name="Mode"/>: <c>set</c> (default, absolute) or <c>add</c> (scan += quantity).
/// </summary>
public sealed record StockCountEntryRequest(
    Guid? LineId, Guid? VariantId, string? Code, Guid? LocationId, string? LocationCode, decimal? Quantity, string? Mode);

/// <param name="LineIds">Lines to count again (their counted quantity is cleared); the count goes back to open.</param>
public sealed record RecountStockCountRequest(IReadOnlyList<Guid>? LineIds);

/// <param name="AcceptMovedLines">
/// Required as true when a counted line's stock moved after its snapshot (<c>movedSinceSnapshot</c>); otherwise
/// 409 <c>count.lines_moved</c> lists them. The correction always uses the system balance at the moment of counting.
/// </param>
public sealed record ApplyStockCountRequest(bool? AcceptMovedLines);

/// <param name="Reason">Required (1–500 characters).</param>
public sealed record CancelStockCountRequest(string? Reason);

/// <summary>
/// A count line. <c>expectedQuantity</c> (system on_hand at the line's snapshot) and <c>difference</c>
/// (counted − expected) are null for blind counts unless the caller holds <c>inventory.count.manage</c>.
/// </summary>
/// <param name="Status">pending, counted, applied, uncounted (not counted when the count was applied).</param>
/// <param name="MovedSinceSnapshot">Stock of this (variant, shelf) moved after the snapshot: review before applying.</param>
/// <param name="AppliedDelta">The count_correction quantity once applied (0 = no difference at that time).</param>
public sealed record StockCountLineResponse(
    Guid Id,
    Guid VariantId,
    string Sku,
    Guid? LocationId,
    string? LocationCode,
    decimal? ExpectedQuantity,
    decimal? CountedQuantity,
    decimal? Difference,
    DateTimeOffset SnapshotAt,
    DateTimeOffset? CountedAt,
    string Status,
    bool MovedSinceSnapshot,
    decimal? AppliedDelta);

/// <param name="Status">open, submitted, applied, cancelled.</param>
/// <param name="QuantitiesHidden">Blind count viewed without <c>inventory.count.manage</c>: system quantities are null.</param>
/// <param name="Version">Weak ETag value for If-Match.</param>
public sealed record StockCountSummaryResponse(
    Guid Id,
    string Number,
    Guid WarehouseId,
    string? WarehouseName,
    string Scope,
    bool IsBlind,
    bool QuantitiesHidden,
    string Status,
    string? Note,
    int LineCount,
    int CountedLineCount,
    DateTimeOffset SnapshotAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? AppliedAt,
    DateTimeOffset CreatedAt,
    long Version);

/// <param name="DifferenceCount">Counted lines whose counted quantity differs from the expected one (managers only, else null).</param>
/// <param name="MovedCount">Counted lines with movements after their snapshot (managers only, else null).</param>
/// <param name="Lines">By shelf code, then SKU.</param>
public sealed record StockCountResponse(
    StockCountSummaryResponse Count,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> VariantIds,
    int? DifferenceCount,
    int? MovedCount,
    Guid? CreatedBy,
    Guid? SubmittedBy,
    Guid? AppliedBy,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    IReadOnlyList<StockCountLineResponse> Lines);

public sealed record StockCountPageResponse(IReadOnlyList<StockCountSummaryResponse> Items, FulfillmentPageInfo Page);

/// <param name="MovementIds">The count_correction movements written (also <c>GET /inventory/movements?sourceId={id}</c>).</param>
public sealed record ApplyStockCountResponse(StockCountResponse Count, IReadOnlyList<Guid> MovementIds);

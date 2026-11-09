using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Warehouse.Domain;

/// <summary>
/// Warehouse-to-warehouse transfer states (OD-01, user-flows AF-07): draft → in_transit ("Yolda", dispatched) →
/// partially_received → received (everything sent arrived) or closed (the in-transit rest was written off as lost
/// or returned to the source, the discrepancy is recorded). Cancel only before dispatch.
/// </summary>
internal static class StockTransferStatuses
{
    public const string Draft = "draft";
    public const string InTransit = "in_transit";
    public const string PartiallyReceived = "partially_received";
    public const string Received = "received";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Draft, InTransit, PartiallyReceived, Received, Closed, Cancelled];

    /// <summary>Dispatched and not finished: goods are (partly) on the road.</summary>
    public static readonly IReadOnlyList<string> Open = [InTransit, PartiallyReceived];
}

/// <summary>How a manager closes what never arrived (AF-07 step 5: "kayıp" or "düzeltme").</summary>
internal static class TransferResolutions
{
    /// <summary>Written off from the in-transit stock (<c>adjustment_out</c>).</summary>
    public const string Lost = "lost";

    /// <summary>Back into the source warehouse/shelf (<c>transfer_out</c> transit → <c>transfer_in</c> source).</summary>
    public const string ReturnToSource = "return_to_source";

    public static readonly IReadOnlyList<string> All = [Lost, ReturnToSource];
}

internal static class StockTransferActions
{
    public const string Dispatch = "dispatch";
    public const string Receive = "receive";
    public const string Close = "close";
    public const string Cancel = "cancel";
}

/// <summary>The single place that decides transfer transitions; null = 409 <c>transfer.invalid_transition</c>.</summary>
internal static class StockTransferStateMachine
{
    /// <param name="outstandingAfter">For <c>receive</c>: the quantity still in transit after the receipt.</param>
    public static string? Next(string action, string status, decimal outstandingAfter = 0m) => (action, status) switch
    {
        (StockTransferActions.Dispatch, StockTransferStatuses.Draft) => StockTransferStatuses.InTransit,
        (StockTransferActions.Cancel, StockTransferStatuses.Draft) => StockTransferStatuses.Cancelled,
        (StockTransferActions.Receive, StockTransferStatuses.InTransit or StockTransferStatuses.PartiallyReceived) =>
            outstandingAfter > 0 ? StockTransferStatuses.PartiallyReceived : StockTransferStatuses.Received,
        (StockTransferActions.Close, StockTransferStatuses.InTransit or StockTransferStatuses.PartiallyReceived) => StockTransferStatuses.Closed,
        _ => null,
    };
}

/// <summary>
/// warehouse.stock_transfers (data-model §5.13, INV-08): moves stock from one warehouse to another in two stages.
/// The ledger holds every leg (source → in-transit → destination); this document holds the plan and the totals.
/// </summary>
internal sealed class StockTransfer : WarehouseModuleEntity
{
    private StockTransfer()
    {
    }

    public string Number { get; private init; } = string.Empty;

    public Guid SourceWarehouseId { get; private init; }

    public Guid DestinationWarehouseId { get; private init; }

    public string Status { get; set; } = StockTransferStatuses.Draft;

    public string? Note { get; private init; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public Guid? DispatchedBy { get; set; }

    public DateTimeOffset? LastReceivedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? CloseResolution { get; set; }

    public string? CloseReason { get; set; }

    public Guid? ClosedBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public List<StockTransferLine> Lines { get; private init; } = [];

    public static StockTransfer Create(
        Guid tenantId, Guid sourceWarehouseId, Guid destinationWarehouseId, string? note, Guid userId, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new StockTransfer
        {
            Id = id,
            TenantId = tenantId,
            // Readable and unique without a sequence: the random tail of the UUIDv7 (UQ per tenant backs it up).
            Number = "TRF-" + id.ToString("N")[^8..].ToUpperInvariant(),
            SourceWarehouseId = sourceWarehouseId,
            DestinationWarehouseId = destinationWarehouseId,
            Note = note,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }
}

/// <summary>warehouse.stock_transfer_lines — Σ received + returned + lost ≤ quantity (CHECK); the rest is in transit.</summary>
internal sealed class StockTransferLine : ITenantOwned
{
    private StockTransferLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid TransferId { get; private init; }

    public int LineNo { get; private init; }

    public Guid VariantId { get; private init; }

    public string Sku { get; private init; } = string.Empty;

    public Guid? SourceLocationId { get; private init; }

    /// <summary>Planned shelf in the destination; the receiver may choose another.</summary>
    public Guid? DestinationLocationId { get; private init; }

    /// <summary>Sent quantity (dispatched as a whole).</summary>
    public decimal Quantity { get; private init; }

    public decimal ReceivedQuantity { get; set; }

    public decimal ReturnedQuantity { get; set; }

    public decimal LostQuantity { get; set; }

    /// <summary>Still on the road (meaningful once dispatched).</summary>
    public decimal Outstanding => Quantity - ReceivedQuantity - ReturnedQuantity - LostQuantity;

    public static StockTransferLine Create(
        StockTransfer transfer, int lineNo, Guid variantId, string sku, Guid? sourceLocationId, Guid? destinationLocationId, decimal quantity) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = transfer.TenantId,
            TransferId = transfer.Id,
            LineNo = lineNo,
            VariantId = variantId,
            Sku = sku,
            SourceLocationId = sourceLocationId,
            DestinationLocationId = destinationLocationId,
            Quantity = quantity,
        };
}

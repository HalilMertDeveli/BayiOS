using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Warehouse.Application;

/// <param name="Statuses">Empty = all.</param>
/// <param name="WarehouseId">Transfers leaving or entering this warehouse.</param>
internal sealed record StockTransferListQuery(IReadOnlyList<string> Statuses, Guid? WarehouseId, int Limit, FulfillmentCursor? After);

/// <summary>Read side of transfers: tenant-filtered EF queries (RLS underneath), newest first, keyset paginated.</summary>
internal sealed class StockTransferQueries(WarehouseDbContext db, TimeProvider clock)
{
    /// <summary>A transfer on the road longer than this is overdue (AF-07: 7 days).</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromDays(7);

    public async Task<StockTransferPageResponse> ListAsync(StockTransferListQuery query, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var transfers = db.StockTransfers.AsNoTracking().Include(t => t.Lines).AsQueryable();
        if (query.Statuses.Count > 0)
        {
            var statuses = query.Statuses.ToList();
            transfers = transfers.Where(t => statuses.Contains(t.Status));
        }

        if (query.WarehouseId is { } warehouseId)
        {
            transfers = transfers.Where(t => t.SourceWarehouseId == warehouseId || t.DestinationWarehouseId == warehouseId);
        }

        if (query.After is { } after)
        {
            transfers = transfers.Where(t => t.CreatedAt < after.CreatedAt || (t.CreatedAt == after.CreatedAt && t.Id.CompareTo(after.Id) < 0));
        }

        var rows = await transfers.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).ToList();
        var names = await WarehouseNamesAsync(page, cancellationToken);
        var items = page.Select(t => Summary(t, names)).ToList();
        var next = hasMore ? new FulfillmentCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return new StockTransferPageResponse(items, new FulfillmentPageInfo(next, hasMore, query.Limit));
    }

    public async Task<StockTransferResponse?> DetailAsync(Guid id, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var transfer = await db.StockTransfers.AsNoTracking().Include(t => t.Lines).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (transfer is null)
        {
            return null;
        }

        var names = await WarehouseNamesAsync([transfer], cancellationToken);
        var locationIds = transfer.Lines.SelectMany(l => new[] { l.SourceLocationId, l.DestinationLocationId }).OfType<Guid>().Distinct().ToList();
        var codes = await db.Locations.AsNoTracking()
            .Where(l => locationIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);
        var dispatched = IsDispatched(transfer.Status);
        var open = StockTransferStatuses.Open.Contains(transfer.Status);
        var lines = transfer.Lines.OrderBy(l => l.LineNo).Select(l => new StockTransferLineResponse(
            l.Id,
            l.LineNo,
            l.VariantId,
            l.Sku,
            l.SourceLocationId,
            l.SourceLocationId is { } source ? codes.GetValueOrDefault(source) : null,
            l.DestinationLocationId,
            l.DestinationLocationId is { } destination ? codes.GetValueOrDefault(destination) : null,
            l.Quantity,
            l.ReceivedQuantity,
            open ? l.Outstanding : 0m,
            l.ReturnedQuantity,
            l.LostQuantity,
            dispatched ? l.Quantity - l.ReceivedQuantity : 0m)).ToList();
        return new StockTransferResponse(
            Summary(transfer, names),
            transfer.CreatedBy,
            transfer.DispatchedBy,
            transfer.CloseResolution,
            transfer.CloseReason,
            transfer.ClosedBy,
            transfer.CancelledAt,
            transfer.CancelReason,
            lines);
    }

    private StockTransferSummaryResponse Summary(StockTransfer t, IReadOnlyDictionary<Guid, string> names)
    {
        var dispatched = IsDispatched(t.Status);
        var open = StockTransferStatuses.Open.Contains(t.Status);
        var quantity = t.Lines.Sum(l => l.Quantity);
        var received = t.Lines.Sum(l => l.ReceivedQuantity);
        var discrepancy = dispatched ? quantity - received : 0m;
        return new StockTransferSummaryResponse(
            t.Id,
            t.Number,
            t.SourceWarehouseId,
            names.GetValueOrDefault(t.SourceWarehouseId),
            t.DestinationWarehouseId,
            names.GetValueOrDefault(t.DestinationWarehouseId),
            t.Status,
            t.Note,
            t.Lines.Count,
            quantity,
            received,
            open ? t.Lines.Sum(l => l.Outstanding) : 0m,
            t.Lines.Sum(l => l.ReturnedQuantity),
            t.Lines.Sum(l => l.LostQuantity),
            discrepancy,
            discrepancy > 0 && t.Status is StockTransferStatuses.PartiallyReceived or StockTransferStatuses.Closed,
            open && t.DispatchedAt is { } at && clock.GetUtcNow() - at > OverdueAfter,
            t.DispatchedAt,
            t.LastReceivedAt,
            t.CompletedAt,
            t.CreatedAt,
            t.RowVersion);
    }

    private static bool IsDispatched(string status) => status is not (StockTransferStatuses.Draft or StockTransferStatuses.Cancelled);

    private async Task<IReadOnlyDictionary<Guid, string>> WarehouseNamesAsync(IReadOnlyList<StockTransfer> transfers, CancellationToken cancellationToken)
    {
        var ids = transfers.SelectMany(t => new[] { t.SourceWarehouseId, t.DestinationWarehouseId }).Distinct().ToList();
        return await db.Warehouses.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);
    }
}

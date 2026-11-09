using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Warehouse.Application;

/// <param name="Statuses">Empty = all.</param>
internal sealed record StockCountListQuery(IReadOnlyList<string> Statuses, Guid? WarehouseId, int Limit, FulfillmentCursor? After);

/// <summary>
/// Read side of stock counts, newest first. Blind counts (INV-06 "kör sayım") hide every system quantity — expected,
/// difference, applied delta, moved flags and their totals — from callers without <c>inventory.count.manage</c>.
/// </summary>
internal sealed class StockCountQueries(WarehouseDbContext db, IStockQueries stock, IRequestContext requestContext)
{
    private bool IsManager => RolePermissions.IsGranted(requestContext.Role, Permissions.InventoryCountManage);

    public async Task<StockCountPageResponse> ListAsync(StockCountListQuery query, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var counts = db.StockCounts.AsNoTracking();
        if (query.Statuses.Count > 0)
        {
            var statuses = query.Statuses.ToList();
            counts = counts.Where(c => statuses.Contains(c.Status));
        }

        if (query.WarehouseId is { } warehouseId)
        {
            counts = counts.Where(c => c.WarehouseId == warehouseId);
        }

        if (query.After is { } after)
        {
            counts = counts.Where(c => c.CreatedAt < after.CreatedAt || (c.CreatedAt == after.CreatedAt && c.Id.CompareTo(after.Id) < 0));
        }

        var rows = await counts
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(query.Limit + 1)
            .Select(c => new
            {
                Count = c,
                Lines = db.StockCountLines.Count(l => l.StockCountId == c.Id),
                Counted = db.StockCountLines.Count(l => l.StockCountId == c.Id && l.CountedQuantity != null),
            })
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).ToList();
        var names = await WarehouseNamesAsync([.. page.Select(p => p.Count.WarehouseId)], cancellationToken);
        var items = page.Select(p => Summary(p.Count, names, p.Lines, p.Counted)).ToList();
        var next = hasMore ? new FulfillmentCursor(page[^1].Count.CreatedAt, page[^1].Count.Id).Encode() : null;
        return new StockCountPageResponse(items, new FulfillmentPageInfo(next, hasMore, query.Limit));
    }

    public async Task<StockCountResponse?> DetailAsync(Guid id, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var count = await db.StockCounts.AsNoTracking().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (count is null)
        {
            return null;
        }

        var hidden = count.IsBlind && !IsManager;
        var names = await WarehouseNamesAsync([count.WarehouseId], cancellationToken);
        var locationIds = count.Lines.Select(l => l.LocationId).OfType<Guid>().Distinct().ToList();
        var codes = await db.Locations.AsNoTracking()
            .Where(l => locationIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);
        var reviewable = count.Status is StockCountStatuses.Open or StockCountStatuses.Submitted;
        var moved = reviewable && !hidden
            ? await stock.MovedSinceAsync(count.WarehouseId, [.. count.Lines.Select(l => (l.VariantId, l.LocationId, l.SnapshotAt))], cancellationToken)
            : new HashSet<(Guid, Guid?)>();
        var lines = count.Lines
            .Select(l => (Line: l, Code: l.LocationId is { } location ? codes.GetValueOrDefault(location) : null))
            .OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Line.Sku, StringComparer.Ordinal).ThenBy(x => x.Line.Id)
            .Select(x => new StockCountLineResponse(
                x.Line.Id,
                x.Line.VariantId,
                x.Line.Sku,
                x.Line.LocationId,
                x.Code,
                hidden ? null : x.Line.ExpectedQuantity,
                x.Line.CountedQuantity,
                hidden || x.Line.CountedQuantity is not { } counted ? null : counted - x.Line.ExpectedQuantity,
                x.Line.SnapshotAt,
                x.Line.CountedAt,
                x.Line.LineStatus,
                moved.Contains((x.Line.VariantId, x.Line.LocationId)),
                hidden ? null : x.Line.AppliedDelta))
            .ToList();
        var countedLines = lines.Where(l => l.CountedQuantity is not null).ToList();
        return new StockCountResponse(
            Summary(count, names, count.Lines.Count, countedLines.Count),
            count.ScopeLocationIds,
            count.ScopeVariantIds,
            hidden ? null : countedLines.Count(l => l.Difference != 0),
            hidden ? null : countedLines.Count(l => l.MovedSinceSnapshot),
            count.CreatedBy,
            count.SubmittedBy,
            count.AppliedBy,
            count.CancelledAt,
            count.CancelReason,
            lines);
    }

    private StockCountSummaryResponse Summary(StockCount c, IReadOnlyDictionary<Guid, string> names, int lineCount, int countedCount) =>
        new(
            c.Id,
            c.Number,
            c.WarehouseId,
            names.GetValueOrDefault(c.WarehouseId),
            c.Scope,
            c.IsBlind,
            c.IsBlind && !IsManager,
            c.Status,
            c.Note,
            lineCount,
            countedCount,
            c.SnapshotAt,
            c.SubmittedAt,
            c.AppliedAt,
            c.CreatedAt,
            c.RowVersion);

    private async Task<IReadOnlyDictionary<Guid, string>> WarehouseNamesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        return await db.Warehouses.AsNoTracking().Where(w => distinct.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);
    }
}

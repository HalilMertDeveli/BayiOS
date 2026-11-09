using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Domain;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Inventory.Application;

/// <param name="Term">Text search (SKU / name contains, barcode equals), resolved through Catalog; null = no search.</param>
internal sealed record BalanceQuery(Guid? VariantId, Guid? WarehouseId, Guid? LocationId, int Limit, Guid? After, string? Term = null);

/// <param name="VariantIds">Empty = every variant.</param>
internal sealed record ThresholdQuery(IReadOnlyList<Guid> VariantIds, Guid? WarehouseId, int Limit, Guid? After);

internal sealed record MovementQuery(Guid? VariantId, Guid? WarehouseId, string? Type, Guid? SourceId, int Limit, InventoryCursor? After);

/// <summary>Read side: tenant-filtered EF queries (RLS underneath), keyset paginated.</summary>
internal sealed class InventoryQueries(
    InventoryDbContext db,
    IAuthorizationContext authorization,
    IRequestContext requestContext,
    ITaskContextVariants taskVariants,
    IVariantDirectory variants) : IStockQueries
{
    public const string BalanceSort = "id";
    public const string MovementSort = "-occurredAt";
    public const string ThresholdSort = "threshold-id";

    /// <summary>
    /// Most variants a balance text search may match. The variants come from Catalog through a port (no cross-schema
    /// join, ADR 0005) and filter the balances as one array parameter; a broader term is refused
    /// (<c>422 inventory.search_too_broad</c>) rather than silently truncated.
    /// </summary>
    public const int MaxSearchMatches = 10_000;

    /// <summary>Balances ordered by id (UUIDv7 ≈ creation order); keyset on id.</summary>
    /// <returns>Null when <see cref="BalanceQuery.Term"/> matches more than <see cref="MaxSearchMatches"/> variants.</returns>
    public async Task<BalancePageResponse?> ListBalancesAsync(BalanceQuery query, CancellationToken cancellationToken)
    {
        // In-transit stock (OD-01) belongs to no warehouse: it is shown on its transfer, never as a balance.
        var balances = db.Balances.AsNoTracking().Where(b => !b.IsTransit);
        if (query.Term is { } term)
        {
            var matches = await variants.SearchVariantIdsAsync(term, MaxSearchMatches + 1, cancellationToken);
            if (matches.Count > MaxSearchMatches)
            {
                return null;
            }

            var ids = matches.ToArray();
            balances = balances.Where(b => ids.Contains(b.VariantId));
        }

        if (query.VariantId is { } variantId)
        {
            balances = balances.Where(b => b.VariantId == variantId);
        }

        if (query.WarehouseId is { } warehouseId)
        {
            balances = balances.Where(b => b.WarehouseId == warehouseId);
        }

        if (query.LocationId is { } locationId)
        {
            balances = balances.Where(b => b.LocationId == locationId);
        }

        if (query.After is { } after)
        {
            balances = balances.Where(b => b.Id.CompareTo(after) > 0);
        }

        var rows = await balances.OrderBy(b => b.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).ToList();
        var minimums = await WarehouseMinimumsAsync(page, cancellationToken);
        var items = page.Select(b =>
        {
            var key = (b.VariantId, b.WarehouseId);
            var (min, available) = minimums.GetValueOrDefault(key);
            return ToResponse(b, min, min is { } m && available <= m);
        }).ToList();
        var next = hasMore ? new InventoryCursor(BalanceSort, string.Empty, items[^1].Id).Encode() : null;
        return new BalancePageResponse(items, new InventoryPageInfo(next, hasMore, query.Limit));
    }

    /// <summary>
    /// For each (variant, warehouse) of the page: the effective minimum in that warehouse (override, else default) and
    /// the variant's available stock summed over every bin of the warehouse (not only the bins on the page).
    /// </summary>
    private async Task<Dictionary<(Guid VariantId, Guid WarehouseId), (decimal? Min, decimal Available)>> WarehouseMinimumsAsync(
        List<InventoryBalance> page, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(Guid, Guid), (decimal?, decimal)>();
        if (page.Count == 0)
        {
            return result;
        }

        var variantIds = page.Select(b => b.VariantId).Distinct().ToList();
        var thresholds = await db.Thresholds.AsNoTracking()
            .Where(t => variantIds.Contains(t.VariantId))
            .Select(t => new { t.VariantId, t.WarehouseId, t.MinQuantity })
            .ToListAsync(cancellationToken);
        if (thresholds.Count == 0)
        {
            return result;
        }

        var warehouseIds = page.Select(b => b.WarehouseId).Distinct().ToList();
        var sums = await db.Balances.AsNoTracking()
            .Where(b => variantIds.Contains(b.VariantId) && warehouseIds.Contains(b.WarehouseId))
            .GroupBy(b => new { b.VariantId, b.WarehouseId })
            .Select(g => new { g.Key.VariantId, g.Key.WarehouseId, Available = g.Sum(b => b.OnHand) - g.Sum(b => b.Reserved) })
            .ToListAsync(cancellationToken);
        foreach (var sum in sums)
        {
            var min = thresholds.FirstOrDefault(t => t.VariantId == sum.VariantId && t.WarehouseId == sum.WarehouseId)?.MinQuantity
                ?? thresholds.FirstOrDefault(t => t.VariantId == sum.VariantId && t.WarehouseId == null)?.MinQuantity;
            result[(sum.VariantId, sum.WarehouseId)] = (min, sum.Available);
        }

        return result;
    }

    /// <summary>Thresholds ordered by id (UUIDv7); keyset on id. Filters: variants, warehouse (exact; null rows = defaults).</summary>
    public async Task<StockThresholdPageResponse> ListThresholdsAsync(ThresholdQuery query, CancellationToken cancellationToken)
    {
        var thresholds = db.Thresholds.AsNoTracking();
        if (query.VariantIds is { Count: > 0 } variantIds)
        {
            var ids = variantIds.ToList();
            thresholds = thresholds.Where(t => ids.Contains(t.VariantId));
        }

        if (query.WarehouseId is { } warehouseId)
        {
            thresholds = thresholds.Where(t => t.WarehouseId == warehouseId);
        }

        if (query.After is { } after)
        {
            thresholds = thresholds.Where(t => t.Id.CompareTo(after) > 0);
        }

        var rows = await thresholds.OrderBy(t => t.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var items = rows.Take(query.Limit).Select(ToResponse).ToList();
        var next = hasMore ? new InventoryCursor(ThresholdSort, string.Empty, items[^1].Id).Encode() : null;
        return new StockThresholdPageResponse(items, new InventoryPageInfo(next, hasMore, query.Limit));
    }

    public async Task<StockThresholdResponse?> GetThresholdAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Thresholds.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        return row is null ? null : ToResponse(row);
    }

    /// <summary>Movement history (stock card), newest first; keyset on (occurred_at, id) descending.</summary>
    public async Task<MovementPageResponse> ListMovementsAsync(MovementQuery query, CancellationToken cancellationToken)
    {
        var movements = await VisibleMovementsAsync(cancellationToken);
        if (query.VariantId is { } variantId)
        {
            movements = movements.Where(m => m.VariantId == variantId);
        }

        if (query.WarehouseId is { } warehouseId)
        {
            movements = movements.Where(m => m.WarehouseId == warehouseId);
        }

        if (query.Type is { } type)
        {
            movements = movements.Where(m => m.Type == type);
        }

        if (query.SourceId is { } sourceId)
        {
            movements = movements.Where(m => m.SourceId == sourceId);
        }

        if (query.After is { } after && InventoryCursor.TryParseTimestamp(after.Key, out var at))
        {
            var id = after.Id;
            movements = movements.Where(m => m.OccurredAt < at || (m.OccurredAt == at && m.Id.CompareTo(id) < 0));
        }

        var rows = await movements
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var items = rows.Take(query.Limit).Select(ToResponse).ToList();
        var next = hasMore
            ? new InventoryCursor(MovementSort, InventoryCursor.FormatTimestamp(items[^1].OccurredAt), items[^1].Id).Encode()
            : null;
        return new MovementPageResponse(items, new InventoryPageInfo(next, hasMore, query.Limit));
    }

    /// <returns>Null when missing or outside the caller's movement visibility (404, no existence hint).</returns>
    public async Task<MovementResult?> GetMovementAsync(Guid id, CancellationToken cancellationToken) =>
        await (await VisibleMovementsAsync(cancellationToken)).Where(m => m.Id == id).Select(m => ToResponse(m)).SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// <c>inventory.movements.read</c> is "own records" for warehouse_staff (footnote 5): movements they created plus
    /// movements of variants on their active assigned or pool tasks. Everyone else with the grant sees all.
    /// </summary>
    private async Task<IQueryable<InventoryMovement>> VisibleMovementsAsync(CancellationToken cancellationToken)
    {
        var movements = db.Movements.AsNoTracking();
        if (await authorization.ScopeAsync(Permissions.InventoryMovementsRead, cancellationToken) == PermissionScope.All)
        {
            return movements;
        }

        var me = requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");
        var variants = (await taskVariants.VariantsForUserAsync(me, cancellationToken)).ToList();
        return movements.Where(m => m.CreatedBy == me || variants.Contains(m.VariantId));
    }

    public Task<bool> WarehouseHasStockAsync(Guid warehouseId, CancellationToken cancellationToken) =>
        db.Balances.AnyAsync(b => b.WarehouseId == warehouseId && (b.OnHand != 0 || b.Reserved != 0), cancellationToken);

    public async Task<IReadOnlyList<StockLevel>> LevelsAsync(
        Guid warehouseId, IReadOnlyCollection<Guid>? locationIds, IReadOnlyCollection<Guid>? variantIds, int max, CancellationToken cancellationToken)
    {
        var balances = db.Balances.AsNoTracking().Where(b => b.WarehouseId == warehouseId && !b.IsTransit);
        if (locationIds is not null)
        {
            var locations = locationIds.ToList();
            balances = balances.Where(b => b.LocationId != null && locations.Contains(b.LocationId.Value));
        }

        if (variantIds is not null)
        {
            var variants = variantIds.ToList();
            balances = balances.Where(b => variants.Contains(b.VariantId));
        }

        return await balances
            .OrderBy(b => b.VariantId).ThenBy(b => b.LocationId)
            .Take(max)
            .Select(b => new StockLevel(b.VariantId, b.LocationId, b.OnHand, b.Reserved))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<(Guid VariantId, Guid? LocationId)>> MovedSinceAsync(
        Guid warehouseId, IReadOnlyCollection<(Guid VariantId, Guid? LocationId, DateTimeOffset Since)> keys, CancellationToken cancellationToken)
    {
        var moved = new HashSet<(Guid, Guid?)>();
        if (keys.Count == 0)
        {
            return moved;
        }

        var variantIds = keys.Select(k => k.VariantId).Distinct().ToList();
        var earliest = keys.Min(k => k.Since);
        var latest = await db.Movements.AsNoTracking()
            .Where(m => m.WarehouseId == warehouseId && variantIds.Contains(m.VariantId) && m.OccurredAt > earliest)
            .GroupBy(m => new { m.VariantId, m.LocationId })
            .Select(g => new { g.Key.VariantId, g.Key.LocationId, Last = g.Max(m => m.OccurredAt) })
            .ToListAsync(cancellationToken);
        var lastByKey = latest.ToDictionary(l => (l.VariantId, l.LocationId), l => l.Last);
        foreach (var (variantId, locationId, since) in keys)
        {
            if (lastByKey.TryGetValue((variantId, locationId), out var last) && last > since)
            {
                moved.Add((variantId, locationId));
            }
        }

        return moved;
    }

    public async Task<IReadOnlyList<ReservationAllocation>> GetOpenReservationsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var rows = await db.Reservations.AsNoTracking()
            .Where(r => r.OrderId == orderId && r.Status == ReservationStatuses.Active)
            .ToListAsync(cancellationToken);
        return [.. rows
            .OrderBy(r => r.OrderLineId)
            .ThenBy(r => r.LocationId is null ? 0 : 1)
            .ThenBy(r => r.LocationId)
            .Select(r => new ReservationAllocation(
                r.Id, r.OrderLineId, r.VariantId, r.WarehouseId, r.LocationId, r.Quantity - r.ConsumedQuantity - r.ReleasedQuantity, r.Status))];
    }

    private static BalanceResponse ToResponse(InventoryBalance b, decimal? minQuantity, bool belowMinimum) =>
        new(b.Id, b.VariantId, b.WarehouseId, b.LocationId, b.OnHand, b.Reserved, b.OnHand - b.Reserved, b.Version, b.UpdatedAt, minQuantity, belowMinimum);

    private static StockThresholdResponse ToResponse(StockThreshold t) =>
        new(t.Id, t.VariantId, t.WarehouseId, t.MinQuantity, t.Version, t.CreatedAt, t.UpdatedAt);

    private static MovementResult ToResponse(InventoryMovement m) =>
        new(m.Id, m.VariantId, m.WarehouseId, m.LocationId, m.Type, m.Quantity, m.UnitCost, m.SourceType, m.SourceId,
            m.SourceLineId, m.Reason, m.BalanceAfter, m.CorrelationId, m.OccurredAt, m.CreatedBy);
}

/// <summary>Opaque keyset cursor: base64url JSON of the sort name, the last row's sort key and id.</summary>
internal sealed record InventoryCursor(string Sort, string Key, Guid Id)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(Sort, Key, Id)));

    public static bool TryDecode(string value, string expectedSort, out InventoryCursor cursor)
    {
        cursor = new InventoryCursor(expectedSort, string.Empty, Guid.Empty);
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value));
            if (payload is null || payload.S != expectedSort || payload.K is null)
            {
                return false;
            }

            if (expectedSort == MovementSortName && !TryParseTimestamp(payload.K, out _))
            {
                return false;
            }

            cursor = new InventoryCursor(payload.S, payload.K, payload.I);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    public static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);

    private const string MovementSortName = InventoryQueries.MovementSort;

    private sealed record Payload(string S, string K, Guid I);
}

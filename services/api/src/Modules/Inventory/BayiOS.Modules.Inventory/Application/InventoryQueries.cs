using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Domain;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Inventory.Application;

internal sealed record BalanceQuery(Guid? VariantId, Guid? WarehouseId, Guid? LocationId, int Limit, Guid? After);

internal sealed record MovementQuery(Guid? VariantId, Guid? WarehouseId, string? Type, Guid? SourceId, int Limit, InventoryCursor? After);

/// <summary>Read side: tenant-filtered EF queries (RLS underneath), keyset paginated.</summary>
internal sealed class InventoryQueries(InventoryDbContext db) : IStockQueries
{
    public const string BalanceSort = "id";
    public const string MovementSort = "-occurredAt";

    /// <summary>Balances ordered by id (UUIDv7 ≈ creation order); keyset on id.</summary>
    public async Task<BalancePageResponse> ListBalancesAsync(BalanceQuery query, CancellationToken cancellationToken)
    {
        var balances = db.Balances.AsNoTracking();
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
        var items = rows.Take(query.Limit).Select(ToResponse).ToList();
        var next = hasMore ? new InventoryCursor(BalanceSort, string.Empty, items[^1].Id).Encode() : null;
        return new BalancePageResponse(items, new InventoryPageInfo(next, hasMore, query.Limit));
    }

    /// <summary>Movement history (stock card), newest first; keyset on (occurred_at, id) descending.</summary>
    public async Task<MovementPageResponse> ListMovementsAsync(MovementQuery query, CancellationToken cancellationToken)
    {
        var movements = db.Movements.AsNoTracking();
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

    public async Task<MovementResult?> GetMovementAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Movements.AsNoTracking().Where(m => m.Id == id).Select(m => ToResponse(m)).SingleOrDefaultAsync(cancellationToken);

    public Task<bool> WarehouseHasStockAsync(Guid warehouseId, CancellationToken cancellationToken) =>
        db.Balances.AnyAsync(b => b.WarehouseId == warehouseId && (b.OnHand != 0 || b.Reserved != 0), cancellationToken);

    private static BalanceResponse ToResponse(InventoryBalance b) =>
        new(b.Id, b.VariantId, b.WarehouseId, b.LocationId, b.OnHand, b.Reserved, b.OnHand - b.Reserved, b.Version, b.UpdatedAt);

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

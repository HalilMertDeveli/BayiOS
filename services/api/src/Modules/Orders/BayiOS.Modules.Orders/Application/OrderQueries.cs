using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Orders.Domain;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Orders.Application;

/// <param name="Statuses">Empty = every status.</param>
/// <param name="From">Inclusive, by creation date (UTC).</param>
/// <param name="To">Inclusive, by creation date (UTC).</param>
/// <param name="Number">Order number fragment (case-insensitive "contains").</param>
internal sealed record OrderListQuery(
    IReadOnlyList<string> Statuses,
    Guid? CustomerId,
    string? Source,
    DateOnly? From,
    DateOnly? To,
    string? Number,
    int Limit,
    OrderCursor? After);

/// <summary>Read side: tenant-filtered EF queries (RLS underneath); newest first, keyset on (created_at, id).</summary>
internal sealed class OrderQueries(OrdersDbContext db)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<OrderPageResponse> ListAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        var orders = db.Orders.AsNoTracking();
        if (query.Statuses.Count > 0)
        {
            orders = orders.Where(o => query.Statuses.Contains(o.Status));
        }

        if (query.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        if (query.Source is { } source)
        {
            orders = orders.Where(o => o.Source == source);
        }

        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            orders = orders.Where(o => o.CreatedAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            orders = orders.Where(o => o.CreatedAt < end);
        }

        if (query.Number is { } number)
        {
            var pattern = "%" + number.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            orders = orders.Where(o => EF.Functions.ILike(o.Number, pattern, "\\"));
        }

        if (query.After is { } after)
        {
            var at = after.CreatedAt;
            var id = after.Id;
            orders = orders.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(id) < 0));
        }

        var rows = await orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(query.Limit + 1)
            .Select(o => new OrderSummaryResponse(
                o.Id, o.Number, o.CustomerId, o.WarehouseId, o.Status, o.FulfillmentStatus, o.Source, o.Currency, o.GrandTotal,
                o.Lines.Count, o.CreatedAt, o.RowVersion))
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var items = rows.Take(query.Limit).ToList();
        var next = hasMore ? new OrderCursor(items[^1].CreatedAt, items[^1].Id).Encode() : null;
        return new OrderPageResponse(items, new OrderPageInfo(next, hasMore, query.Limit));
    }

    public async Task<OrderDetailResponse?> DetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var history = await db.History.AsNoTracking()
            .Where(h => h.OrderId == id)
            .OrderBy(h => h.ChangedAt).ThenBy(h => h.Id)
            .Select(h => new OrderHistoryResponse(h.Id, h.Action, h.FromStatus, h.ToStatus, h.FulfillmentStatus, h.Reason, h.ChangedBy, h.ChangedAt))
            .ToListAsync(cancellationToken);
        var shipments = await db.Shipments.AsNoTracking()
            .Include(s => s.Lines)
            .Where(s => s.OrderId == id)
            .OrderBy(s => s.ShippedAt).ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

        var lines = order.Lines.OrderBy(l => l.LineNo).ToList();
        return new OrderDetailResponse(
            ToResponse(order, lines),
            [.. lines.Select(ToResponse)],
            history,
            [.. shipments.Select(s => new OrderShipmentResponse(
                s.Id, s.Carrier, s.TrackingNumber, s.PackageCount, s.ShippedAt, s.ShippedBy,
                [.. s.Lines.OrderBy(l => l.Id).Select(l => new OrderShipmentLineResponse(l.Id, l.OrderLineId, l.VariantId, l.LocationId, l.Quantity))]))]);
    }

    private static OrderResponse ToResponse(Order o, IReadOnlyList<OrderLine> lines)
    {
        var totals = OrderMath.Totals(lines, o.PricesIncludeTax);
        return new OrderResponse(
            o.Id,
            o.Number,
            o.CustomerId,
            o.WarehouseId,
            o.Status,
            o.FulfillmentStatus,
            o.Source,
            o.SourceReferenceId,
            o.Currency,
            o.PriceDate,
            o.RequestedDeliveryDate,
            o.Notes,
            new OrderTotalsResponse(
                o.PricesIncludeTax,
                o.Subtotal,
                o.DiscountTotal,
                o.TaxTotal,
                totals.NetTotal,
                o.GrandTotal,
                [.. totals.TaxBreakdown.Select(b => new TaxBreakdownResponse(b.Rate, b.TaxBase, b.Tax, b.Total))],
                lines.Any(l => l.UnitPrice is null)),
            o.CreatedAt,
            o.CreatedBy,
            o.SubmittedAt,
            o.ApprovedAt,
            o.ApprovedBy,
            o.ShippedAt,
            o.CompletedAt,
            o.CancelledAt,
            o.CancelReason,
            o.RowVersion);
    }

    private static OrderLineResponse ToResponse(OrderLine l) => new(
        l.Id,
        l.LineNo,
        l.VariantId,
        l.Sku,
        l.Name,
        l.Unit,
        l.Quantity,
        l.UnitPrice is null ? PriceStatuses.NoPrice : PriceStatuses.Priced,
        l.UnitPrice,
        l.PriceSource,
        l.PriceListId,
        l.PriceOverridden,
        l.DiscountPercent,
        l.DiscountAmount,
        l.TaxRate,
        l.LineSubtotal,
        l.LineDiscount,
        l.LineTotal,
        l.QtyReserved,
        l.QtyShipped,
        l.QtyCancelled,
        l.Quantity - l.QtyShipped - l.QtyCancelled);
}

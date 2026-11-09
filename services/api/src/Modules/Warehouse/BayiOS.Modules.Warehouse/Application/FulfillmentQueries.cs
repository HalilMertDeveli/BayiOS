using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Warehouse.Application;

/// <param name="Statuses">Empty = every status.</param>
/// <param name="AssignedTo">A user id; <see cref="Guid.Empty"/> = unassigned (pool); null = anyone.</param>
internal sealed record PickingTaskListQuery(
    IReadOnlyList<string> Statuses,
    string? Type,
    Guid? WarehouseId,
    Guid? OrderId,
    Guid? AssignedTo,
    int Limit,
    FulfillmentCursor? After,
    string? Priority = null);

internal sealed record ShipmentListQuery(IReadOnlyList<string> Statuses, Guid? OrderId, int Limit, FulfillmentCursor? After);

/// <summary>
/// Read side of picking and shipping: tenant-filtered EF queries (RLS underneath), oldest first (work queue order),
/// keyset on (created_at, id). Visibility is applied here, not by the caller (roles-permissions §3.6):
/// <c>tasks.read.all</c> ✓ sees everything; sales (K¹²) sees the tasks and shipments of orders it created;
/// <c>tasks.read.own</c> (warehouse_staff) sees tasks assigned to them plus the active pool, and the shipments of
/// their tasks.
/// </summary>
internal sealed class FulfillmentQueries(
    WarehouseDbContext db,
    FulfillmentAccess access,
    IFulfillmentCatalog catalog,
    IAuthorizationContext authorization,
    IOrderOwnership ownership,
    ITaskAssigneeDirectory assignees,
    ITaskOrderDirectory orders)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<PickingTaskPageResponse> ListTasksAsync(PickingTaskListQuery query, CancellationToken cancellationToken)
    {
        var tasks = Visible(db.PickingTasks.AsNoTracking(), await ReadVisibilityAsync(cancellationToken));
        if (query.Statuses.Count > 0)
        {
            tasks = tasks.Where(t => query.Statuses.Contains(t.Status));
        }

        if (query.Type is { } type)
        {
            tasks = tasks.Where(t => t.Type == type);
        }

        if (query.WarehouseId is { } warehouseId)
        {
            tasks = tasks.Where(t => t.WarehouseId == warehouseId);
        }

        if (query.OrderId is { } orderId)
        {
            tasks = tasks.Where(t => t.OrderId == orderId);
        }

        if (query.Priority is { } priority)
        {
            tasks = tasks.Where(t => t.Priority == priority);
        }

        if (query.AssignedTo is { } assignedTo)
        {
            tasks = assignedTo == Guid.Empty ? tasks.Where(t => t.AssignedTo == null) : tasks.Where(t => t.AssignedTo == assignedTo);
        }

        if (query.After is { } after)
        {
            var at = after.CreatedAt;
            var id = after.Id;
            tasks = tasks.Where(t => t.CreatedAt > at || (t.CreatedAt == at && t.Id.CompareTo(id) > 0));
        }

        var rows = await Summaries(tasks.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(query.Limit + 1)).ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var items = await WithNamesAsync([.. rows.Take(query.Limit)], cancellationToken);
        var next = hasMore ? new FulfillmentCursor(items[^1].CreatedAt, items[^1].Id).Encode() : null;
        return new PickingTaskPageResponse(items, new FulfillmentPageInfo(next, hasMore, query.Limit));
    }

    /// <summary>Active tasks assigned to the caller, then the active unassigned pool (WHT-03).</summary>
    public async Task<MyPickingTasksResponse> MyTasksAsync(bool assigned, bool pool, Guid? warehouseId, int limit, CancellationToken cancellationToken)
    {
        var me = access.UserId;
        var tasks = db.PickingTasks.AsNoTracking().Where(t => PickingTaskStatuses.Active.Contains(t.Status));
        if (warehouseId is { } warehouse)
        {
            tasks = tasks.Where(t => t.WarehouseId == warehouse);
        }

        tasks = (assigned, pool) switch
        {
            (true, true) => tasks.Where(t => t.AssignedTo == me || t.AssignedTo == null),
            (true, false) => tasks.Where(t => t.AssignedTo == me),
            _ => tasks.Where(t => t.AssignedTo == null),
        };

        var rows = await Summaries(tasks
                .OrderBy(t => t.AssignedTo == null)
                .ThenByDescending(t => t.Priority == PickingTaskPriorities.Urgent ? 3
                    : t.Priority == PickingTaskPriorities.High ? 2
                    : t.Priority == PickingTaskPriorities.Normal ? 1
                    : 0)
                .ThenBy(t => t.CreatedAt)
                .ThenBy(t => t.Id)
                .Take(limit))
            .ToListAsync(cancellationToken);
        return new MyPickingTasksResponse(await WithNamesAsync(rows, cancellationToken));
    }

    /// <returns>Null when the task does not exist or the caller may not see it.</returns>
    public async Task<PickingTaskResponse?> TaskAsync(Guid id, CancellationToken cancellationToken)
    {
        var visible = Visible(db.PickingTasks.AsNoTracking(), await ReadVisibilityAsync(cancellationToken));
        var row = await Summaries(visible.Where(t => t.Id == id)).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var summary = (await WithNamesAsync([row], cancellationToken))[0];

        var lines = await (from line in db.PickingTaskLines.AsNoTracking()
                           where line.PickingTaskId == id && line.QtyToPick > 0 // lines emptied by a re-allocation (#98) are kept, not shown
                           join location in db.Locations.AsNoTracking() on line.LocationId equals (Guid?)location.Id into located
                           from location in located.DefaultIfEmpty()
                           select new
                           {
                               line,
                               LocationCode = location == null ? null : location.Code,
                               LocationBarcode = location == null ? null : location.Barcode,
                               LocationIsActive = location == null ? (bool?)null : location.IsActive,
                               PickSequence = location == null ? int.MinValue : location.PickSequence,
                           })
            .ToListAsync(cancellationToken);

        // Walking order on the current shelves (re-allocated lines join their new shelf's place).
        lines = [.. lines
            .OrderBy(l => l.PickSequence)
            .ThenBy(l => l.LocationCode ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(l => l.line.Sequence)];
        var variants = await catalog.FindVariantsAsync([.. lines.Select(l => l.line.VariantId).Distinct()], cancellationToken);
        var shipmentId = await db.Shipments.AsNoTracking()
            .Where(s => s.PickingTaskId == id && s.Status != ShipmentStatuses.Cancelled)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new PickingTaskResponse(
            summary,
            [.. lines.Select(l => new PickingTaskLineResponse(
                l.line.Id,
                l.line.Sequence,
                l.line.OrderLineId,
                l.line.VariantId,
                l.line.Sku,
                l.line.Name,
                l.line.Unit,
                variants.GetValueOrDefault(l.line.VariantId)?.Barcode,
                l.line.LocationId,
                l.LocationCode,
                l.LocationBarcode,
                l.line.QtyToPick,
                l.line.QtyPicked,
                l.line.QtyShort,
                l.line.QtyToPick - l.line.QtyPicked - l.line.QtyShort,
                l.line.ShortageReason,
                l.LocationIsActive))],
            shipmentId);
    }

    public async Task<ShipmentPageResponse> ListShipmentsAsync(ShipmentListQuery query, CancellationToken cancellationToken)
    {
        var shipments = await VisibleShipmentsAsync(cancellationToken);
        if (query.Statuses.Count > 0)
        {
            shipments = shipments.Where(s => query.Statuses.Contains(s.Status));
        }

        if (query.OrderId is { } orderId)
        {
            shipments = shipments.Where(s => s.OrderId == orderId);
        }

        if (query.After is { } after)
        {
            var at = after.CreatedAt;
            var id = after.Id;
            shipments = shipments.Where(s => s.CreatedAt > at || (s.CreatedAt == at && s.Id.CompareTo(id) > 0));
        }

        var rows = await shipments
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Take(query.Limit + 1)
            .Select(s => new ShipmentSummaryResponse(
                s.Id, s.Number, s.OrderId, s.OrderNumber, s.WarehouseId, s.Status, s.Lines.Count, s.Lines.Sum(l => l.Quantity), s.CreatedAt, s.ShippedAt, s.RowVersion))
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var items = rows.Take(query.Limit).ToList();
        var next = hasMore ? new FulfillmentCursor(items[^1].CreatedAt, items[^1].Id).Encode() : null;
        return new ShipmentPageResponse(items, new FulfillmentPageInfo(next, hasMore, query.Limit));
    }

    /// <returns>Null when the shipment does not exist or the caller may not see it.</returns>
    public async Task<ShipmentResponse?> ShipmentAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await (await VisibleShipmentsAsync(cancellationToken))
            .Include(s => s.Lines)
            .Include(s => s.Packages).ThenInclude(p => p.Items)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return null;
        }

        var locationIds = shipment.Lines.Where(l => l.LocationId is not null).Select(l => l.LocationId!.Value).Distinct().ToList();
        var codes = await db.Locations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);
        var variants = await catalog.FindVariantsAsync([.. shipment.Lines.Select(l => l.VariantId).Distinct()], cancellationToken);
        return new ShipmentResponse(
            shipment.Id,
            shipment.Number,
            shipment.OrderId,
            shipment.OrderNumber,
            shipment.PickingTaskId,
            shipment.WarehouseId,
            shipment.Status,
            shipment.Carrier,
            shipment.TrackingNumber,
            shipment.PackageCount,
            shipment.CreatedAt,
            shipment.PackedAt,
            shipment.ShippedAt,
            shipment.ConfirmedBy,
            shipment.RowVersion,
            [.. shipment.Lines.OrderBy(l => l.Sku, StringComparer.Ordinal).ThenBy(l => l.Id).Select(l => new ShipmentLineResponse(
                l.Id,
                l.OrderLineId,
                l.VariantId,
                l.Sku,
                l.Name,
                l.Unit,
                variants.GetValueOrDefault(l.VariantId)?.Barcode,
                l.LocationId,
                l.LocationId is { } locationId ? codes.GetValueOrDefault(locationId) : null,
                l.PickedQuantity,
                l.Quantity,
                l.PackedQuantity,
                l.ShortQuantity,
                l.ShortageReason))],
            [.. shipment.Packages.OrderBy(p => p.PackageNo).Select(p => new ShipmentPackageResponse(
                p.Id, p.PackageNo, [.. p.Items.OrderBy(i => i.Id).Select(i => new ShipmentPackageItemResponse(i.ShipmentLineId, i.Quantity))]))]);
    }

    /// <summary>
    /// Read visibility of the caller. Always evaluates both task-read permissions through the authorization context
    /// (this is the record-level check for sales' conditional <c>tasks.read.all</c>).
    /// </summary>
    private async Task<ReadVisibility> ReadVisibilityAsync(CancellationToken cancellationToken)
    {
        var all = await authorization.ScopeAsync(Permissions.TasksReadAll, cancellationToken);
        var own = await authorization.ScopeAsync(Permissions.TasksReadOwn, cancellationToken);
        if (all == PermissionScope.All)
        {
            return new ReadVisibility(true, access.UserId, [], false);
        }

        IReadOnlyList<Guid> ownOrders = all == PermissionScope.Restricted
            ? await ownership.OrdersCreatedByAsync(access.UserId, cancellationToken)
            : [];
        return new ReadVisibility(false, access.UserId, [.. ownOrders], own != PermissionScope.None);
    }

    private static IQueryable<PickingTask> Visible(IQueryable<PickingTask> tasks, ReadVisibility visibility)
    {
        if (visibility.All)
        {
            return tasks;
        }

        var (me, orders, ownTasks) = (visibility.UserId, visibility.OwnOrders, visibility.OwnTasks);
        return tasks.Where(t => orders.Contains(t.OrderId)
            || (ownTasks && (t.AssignedTo == me
                || (t.AssignedTo == null && (t.Status == PickingTaskStatuses.Open || t.Status == PickingTaskStatuses.InProgress)))));
    }

    /// <summary>Everything for <c>tasks.read.all</c>; sales: shipments of its orders; staff: shipments of their tasks.</summary>
    private async Task<IQueryable<Shipment>> VisibleShipmentsAsync(CancellationToken cancellationToken)
    {
        var shipments = db.Shipments.AsNoTracking();
        var visibility = await ReadVisibilityAsync(cancellationToken);
        if (visibility.All)
        {
            return shipments;
        }

        var (me, orders, ownTasks) = (visibility.UserId, visibility.OwnOrders, visibility.OwnTasks);
        return shipments.Where(s => orders.Contains(s.OrderId)
            || (ownTasks && db.PickingTasks.Any(t => t.Id == s.PickingTaskId && t.AssignedTo == me)));
    }

    private sealed record ReadVisibility(bool All, Guid UserId, List<Guid> OwnOrders, bool OwnTasks);

    /// <summary>
    /// Fills assignee name, customer name and requested delivery date with one batch lookup each for the whole list
    /// (WHT-03, #98). The customer name is the only customer field on a task and is shown to every task reader.
    /// </summary>
    private async Task<List<PickingTaskSummaryResponse>> WithNamesAsync(List<PickingTaskSummaryResponse> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.AssignedTo).OfType<Guid>().Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await assignees.DisplayNamesAsync(ids, cancellationToken);
        var info = await orders.DescribeAsync([.. rows.Select(r => r.OrderId).Distinct()], cancellationToken);
        return [.. rows.Select(r =>
        {
            var order = info.GetValueOrDefault(r.OrderId);
            return r with
            {
                AssigneeName = r.AssignedTo is { } assignee ? names.GetValueOrDefault(assignee) : null,
                CustomerName = order?.CustomerName,
                RequestedDeliveryDate = order?.RequestedDeliveryDate,
            };
        })];
    }

    private static IQueryable<PickingTaskSummaryResponse> Summaries(IQueryable<PickingTask> tasks) =>
        tasks.Select(t => new PickingTaskSummaryResponse(
            t.Id,
            t.Type,
            t.Status,
            t.OrderId,
            t.OrderNumber,
            t.WarehouseId,
            t.AssignedTo,
            null,
            t.Lines.Count(l => l.QtyToPick > 0),
            t.Lines.Sum(l => l.QtyToPick),
            t.Lines.Sum(l => l.QtyPicked),
            t.Lines.Sum(l => l.QtyShort),
            t.CreatedAt,
            t.StartedAt,
            t.CompletedAt,
            t.RowVersion,
            t.Priority));
}

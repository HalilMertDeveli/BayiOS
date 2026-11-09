using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>
/// Warehouse side of a reservation re-allocation (#98, inventory-and-orders §4.5), called by the Inventory engine in the
/// same transaction. <see cref="LockHeldAsync"/> is lock hierarchy step 2 (before balances); <see cref="OnReallocatedAsync"/>
/// re-points the open quantity of active pick task lines with <see cref="PickingReallocation"/>.
/// </summary>
internal sealed class ReservationHolders(
    WarehouseDbContext db,
    FulfillmentLocks locks,
    IRequestContext requestContext,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock) : IReservationHolders
{
    public const string TaskReallocatedEvent = "warehouse.picking_task_reallocated.v1";

    public async Task<IReadOnlyDictionary<(Guid OrderLineId, Guid? LocationId), decimal>> LockHeldAsync(
        IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        await locks.LockActiveDocumentsAsync(orderIds, cancellationToken);

        // Picked or reported missing on an active pick task; on its way back on an active putaway task.
        var onTasks = await (from line in db.PickingTaskLines.AsNoTracking()
                             join task in db.PickingTasks.AsNoTracking() on line.PickingTaskId equals task.Id
                             where orderIds.Contains(task.OrderId) && (task.Status == PickingTaskStatuses.Open || task.Status == PickingTaskStatuses.InProgress)
                             select new
                             {
                                 line.OrderLineId,
                                 line.LocationId,
                                 Held = task.Type == PickingTaskTypes.Pick ? line.QtyPicked + line.QtyShort : line.QtyToPick - line.QtyPicked,
                             })
            .ToListAsync(cancellationToken);

        // Picked goods waiting to ship.
        var onShipments = await (from line in db.ShipmentLines.AsNoTracking()
                                 join shipment in db.Shipments.AsNoTracking() on line.ShipmentId equals shipment.Id
                                 where orderIds.Contains(shipment.OrderId) && (shipment.Status == ShipmentStatuses.Draft || shipment.Status == ShipmentStatuses.Packed)
                                 select new { line.OrderLineId, line.LocationId, Held = line.Quantity })
            .ToListAsync(cancellationToken);

        return onTasks.Concat(onShipments)
            .Where(x => x.Held > 0)
            .GroupBy(x => (x.OrderLineId, x.LocationId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Held));
    }

    public async Task OnReallocatedAsync(IReadOnlyList<ReservationMoved> moves, string reason, CancellationToken cancellationToken)
    {
        var orderIds = moves.Select(m => m.OrderId).Distinct().ToList();
        var tasks = await db.PickingTasks.Include(t => t.Lines)
            .Where(t => orderIds.Contains(t.OrderId) && t.Type == PickingTaskTypes.Pick && PickingTaskStatuses.Active.Contains(t.Status))
            .ToListAsync(cancellationToken);
        var userId = requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");
        var changed = new Dictionary<Guid, decimal>();
        foreach (var move in moves)
        {
            if (tasks.FirstOrDefault(t => t.OrderId == move.OrderId) is not { } task)
            {
                continue; // reserved but no active pick task (auto-create off, or between tasks): nothing to re-point
            }

            var outcome = PickingReallocation.Apply(task, move.OrderLineId, move.FromLocationId, move.ToLocationId, move.Quantity);
            if (outcome.Moved == 0)
            {
                continue;
            }

            foreach (var line in outcome.Added)
            {
                db.PickingTaskLines.Add(line);
            }

            task.Touch(userId, clock.GetUtcNow());
            changed[task.Id] = changed.GetValueOrDefault(task.Id) + outcome.Moved;
        }

        foreach (var task in tasks.Where(t => changed.ContainsKey(t.Id)))
        {
            await audit.RecordAsync(
                new AuditEvent(AuditActions.PickingTaskReallocated, FulfillmentAuditActions.TaskEntityType, task.Id, new AuditChanges()
                    .Value("orderId", task.OrderId)
                    .Value("reason", reason)
                    .Value("quantity", changed[task.Id])
                    .ToDictionary()),
                cancellationToken);
            await outbox.PublishAsync(
                TaskReallocatedEvent,
                new { taskId = task.Id, orderId = task.OrderId, warehouseId = task.WarehouseId, assignedTo = task.AssignedTo, reason },
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Outcome of re-allocating some open task quantity away from shelves.</summary>
internal sealed record ReallocationSummary(decimal Moved, decimal Unmoved)
{
    public static readonly ReallocationSummary None = new(0m, 0m);

    public ReallocationSummary Add(ReallocationSummary other) => new(Moved + other.Moved, Unmoved + other.Unmoved);
}

/// <summary>
/// Plans re-allocations for #98: the open quantity of active pick task lines on a shelf that is unavailable (deactivated,
/// or named by a supervisor) goes to active shelves of the same warehouse with free stock, in walking order
/// (pick_sequence, code). One engine command per line, so a line that cannot move (raced away, no free stock) leaves the
/// others moved; what cannot move stays on the line and is reported as unmoved.
/// </summary>
internal sealed class PickingReallocator(WarehouseDbContext db, FulfillmentLocks locks, IInventoryService inventory, IStockQueries stock)
{
    private const int MaxLevels = 5000;

    /// <summary>Every active pick task's open lines on the (just deactivated) location.</summary>
    public async Task<ReallocationSummary> AwayFromLocationAsync(Guid locationId, string? correlationId, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);

        // Lock the affected tasks first (hierarchy step 2), then read their lines: a scan that got in before us is
        // seen, so the plan never asks to move picked quantity.
        var orderIds = await (from line in db.PickingTaskLines.AsNoTracking()
                              join task in db.PickingTasks.AsNoTracking() on line.PickingTaskId equals task.Id
                              where line.LocationId == locationId && task.Type == PickingTaskTypes.Pick
                                    && (task.Status == PickingTaskStatuses.Open || task.Status == PickingTaskStatuses.InProgress)
                              select task.OrderId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (orderIds.Count == 0)
        {
            return ReallocationSummary.None;
        }

        await locks.LockActiveDocumentsAsync(orderIds, cancellationToken);
        var lines = await (from line in db.PickingTaskLines.AsNoTracking()
                           join task in db.PickingTasks.AsNoTracking() on line.PickingTaskId equals task.Id
                           where line.LocationId == locationId && task.Type == PickingTaskTypes.Pick
                                 && (task.Status == PickingTaskStatuses.Open || task.Status == PickingTaskStatuses.InProgress)
                                 && line.QtyToPick - line.QtyPicked - line.QtyShort > 0
                           select new OpenLine(task.WarehouseId, line.OrderLineId, line.VariantId, locationId, line.QtyToPick - line.QtyPicked - line.QtyShort))
            .ToListAsync(cancellationToken);
        return await MoveAsync(lines, ReallocationReasons.LocationDeactivated, correlationId, cancellationToken);
    }

    /// <summary>One task's open lines on inactive shelves, or on <paramref name="fromLocationId"/> when given.</summary>
    public async Task<ReallocationSummary> TaskAsync(PickingTask task, Guid? fromLocationId, string? correlationId, CancellationToken cancellationToken)
    {
        var sources = fromLocationId is { } from
            ? [from]
            : await db.Locations.AsNoTracking()
                .Where(l => l.WarehouseId == task.WarehouseId && !l.IsActive)
                .Select(l => l.Id)
                .ToListAsync(cancellationToken);
        var lines = task.Lines
            .Where(l => l.LocationId is { } location && sources.Contains(location) && l.Remaining > 0)
            .Select(l => new OpenLine(task.WarehouseId, l.OrderLineId, l.VariantId, l.LocationId!.Value, l.Remaining))
            .ToList();
        return await MoveAsync(lines, ReallocationReasons.Manual, correlationId, cancellationToken);
    }

    private async Task<ReallocationSummary> MoveAsync(IReadOnlyList<OpenLine> lines, string reason, string? correlationId, CancellationToken cancellationToken)
    {
        var summary = ReallocationSummary.None;
        foreach (var group in lines.GroupBy(l => l.WarehouseId))
        {
            var warehouseId = group.Key;
            var variantIds = group.Select(l => l.VariantId).Distinct().ToList();
            var shelves = await db.Locations.AsNoTracking()
                .Where(l => l.WarehouseId == warehouseId && l.IsActive)
                .OrderBy(l => l.PickSequence).ThenBy(l => l.Code)
                .Select(l => l.Id)
                .ToListAsync(cancellationToken);
            var order = shelves.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

            // Unlocked read for planning; the engine locks and re-checks every row before moving anything.
            var free = (await stock.LevelsAsync(warehouseId, shelves, variantIds, MaxLevels, cancellationToken))
                .Where(l => l.LocationId is not null && l.OnHand - l.Reserved > 0)
                .ToDictionary(l => (l.VariantId, l.LocationId!.Value), l => l.OnHand - l.Reserved);

            foreach (var line in group)
            {
                var shifts = new List<ReservationShift>();
                var left = line.Open;
                foreach (var (key, available) in free
                    .Where(f => f.Key.VariantId == line.VariantId && f.Key.Item2 != line.FromLocationId && f.Value > 0)
                    .OrderBy(f => order[f.Key.Item2])
                    .ToList())
                {
                    var take = Math.Min(left, available);
                    shifts.Add(new ReservationShift(line.OrderLineId, line.VariantId, line.FromLocationId, key.Item2, take));
                    left -= take;
                    if (left == 0)
                    {
                        break;
                    }
                }

                if (shifts.Count == 0)
                {
                    summary = summary.Add(new ReallocationSummary(0m, line.Open));
                    continue;
                }

                var result = await inventory.ReallocateAsync(new ReallocateReservations(warehouseId, shifts, reason, correlationId), cancellationToken);
                if (result.Error is not null)
                {
                    summary = summary.Add(new ReallocationSummary(0m, line.Open));
                    continue;
                }

                foreach (var shift in shifts)
                {
                    free[(shift.VariantId, shift.ToLocationId)] -= shift.Quantity;
                }

                var moved = shifts.Sum(s => s.Quantity);
                summary = summary.Add(new ReallocationSummary(moved, line.Open - moved));
            }
        }

        return summary;
    }

    private sealed record OpenLine(Guid WarehouseId, Guid OrderLineId, Guid VariantId, Guid FromLocationId, decimal Open);
}

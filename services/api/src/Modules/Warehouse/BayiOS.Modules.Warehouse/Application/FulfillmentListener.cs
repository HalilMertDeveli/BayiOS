using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>Outbox event types of fulfilment (ids only — consumers read the rest; at-least-once delivery).</summary>
internal static class FulfillmentEvents
{
    public const string PickingTaskCreated = "warehouse.picking_task_created.v1";
    public const string PickingTaskAssigned = "warehouse.picking_task_assigned.v1";
    public const string PickingCompleted = "warehouse.picking_completed.v1";
    public const string ShortageReported = "warehouse.shortage_reported.v1";
    public const string ShipmentConfirmed = "warehouse.shipment_confirmed.v1";
    public const string ShipmentCancelled = "warehouse.shipment_cancelled.v1";
    public const string PutawayCreated = "warehouse.putaway_created.v1";
}

internal static class FulfillmentAuditActions
{
    public const string TaskEntityType = "picking_task";
    public const string ShipmentEntityType = "shipment";
}

/// <summary>
/// Builds a pick task from the order's open reservations: one line per (order line, reserved location), in walking
/// order (location pick_sequence, location code, order line no). Used on approval and for a follow-up task.
/// </summary>
internal sealed class PickingTaskBuilder(
    WarehouseDbContext db,
    IStockQueries stock,
    IRequestContext requestContext,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock)
{
    /// <returns>The new task, or null when nothing of the order is open and reserved.</returns>
    public async Task<PickingTask?> CreateAsync(
        Guid orderId, string orderNumber, Guid warehouseId, IReadOnlyList<FulfillmentOrderLine> orderLines, CancellationToken cancellationToken)
    {
        var reservations = (await stock.GetOpenReservationsAsync(orderId, cancellationToken))
            .Where(r => r.Quantity > 0)
            .GroupBy(r => (r.OrderLineId, r.LocationId))
            .Select(g => (g.Key.OrderLineId, g.Key.LocationId, Quantity: g.Sum(r => r.Quantity)))
            .ToList();
        if (reservations.Count == 0)
        {
            return null;
        }

        var locationIds = reservations.Where(r => r.LocationId is not null).Select(r => r.LocationId!.Value).Distinct().ToList();
        var locations = await db.Locations.AsNoTracking()
            .Where(l => locationIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PickSequence, l.Code), cancellationToken);
        var lines = orderLines.ToDictionary(l => l.Id);

        var task = PickingTask.Create(
            requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved."),
            PickingTaskTypes.Pick, orderId, orderNumber, warehouseId, requestContext.UserId, clock.GetUtcNow());
        var sequence = 0;
        foreach (var reservation in reservations
            .OrderBy(r => r.LocationId is { } id ? locations[id].PickSequence : int.MinValue)
            .ThenBy(r => r.LocationId is { } id ? locations[id].Code : string.Empty, StringComparer.Ordinal)
            .ThenBy(r => lines[r.OrderLineId].LineNo))
        {
            var line = lines[reservation.OrderLineId];
            task.Lines.Add(PickingTaskLine.Create(
                task, ++sequence, line.Id, line.VariantId, line.Sku, line.Name, line.Unit, reservation.LocationId, reservation.Quantity));
        }

        db.PickingTasks.Add(task);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.PickingTaskCreated, FulfillmentAuditActions.TaskEntityType, task.Id, new AuditChanges()
                .Value("orderId", orderId)
                .Value("lineCount", task.Lines.Count)
                .Value("quantity", task.Lines.Sum(l => l.QtyToPick))
                .ToDictionary()),
            cancellationToken);
        await outbox.PublishAsync(
            FulfillmentEvents.PickingTaskCreated, new { taskId = task.Id, orderId, warehouseId, type = task.Type }, cancellationToken);
        return task;
    }
}

/// <summary>
/// Warehouse side of the Orders transitions (domain-model §12, same transaction): approval creates the pick task;
/// a release (cancel / short-close) cancels open tasks and unconfirmed shipments and turns already picked goods into
/// a putaway task (PRD ORD-07 — goods go back to the shelf; no ledger movement, the stock never left).
/// </summary>
internal sealed class FulfillmentListener(
    WarehouseDbContext db,
    PickingTaskBuilder builder,
    FulfillmentLocks locks,
    IRequestContext requestContext,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock) : IOrderFulfillmentListener
{
    public async Task OnApprovedAsync(OrderApprovedNotice notice, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        await builder.CreateAsync(notice.OrderId, notice.Number, notice.WarehouseId, notice.Lines, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task OnReservationsReleasedAsync(Guid orderId, string reason, CancellationToken cancellationToken)
    {
        await locks.LockOrderDocumentsAsync(orderId, cancellationToken);
        var tasks = await db.PickingTasks.Include(t => t.Lines)
            .Where(t => t.OrderId == orderId && t.Type == PickingTaskTypes.Pick && PickingTaskStatuses.Active.Contains(t.Status))
            .ToListAsync(cancellationToken);
        var shipments = await db.Shipments.Include(s => s.Lines)
            .Where(s => s.OrderId == orderId && ShipmentStatuses.Unconfirmed.Contains(s.Status))
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0 && shipments.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var userId = requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

        // Picked but not shipped: still-active task lines plus lines of unconfirmed shipments (disjoint: a task's picks
        // move to its shipment only when the task completes).
        var back = tasks.SelectMany(t => t.Lines.Where(l => l.QtyPicked > 0)
                .Select(l => (l.OrderLineId, l.VariantId, l.Sku, l.Name, l.Unit, l.LocationId, Quantity: l.QtyPicked)))
            .Concat(shipments.SelectMany(s => s.Lines.Where(l => l.Quantity > 0)
                .Select(l => (l.OrderLineId, l.VariantId, l.Sku, l.Name, l.Unit, l.LocationId, l.Quantity))))
            .ToList();

        foreach (var task in tasks)
        {
            task.Status = FulfillmentStateMachine.NextTask(FulfillmentActions.Cancel, task.Type, task.Status)!;
            task.CancelledAt = now;
            task.CancelReason = reason;
            task.Touch(userId, now);
            await audit.RecordAsync(
                new AuditEvent(AuditActions.PickingTaskCancelled, FulfillmentAuditActions.TaskEntityType, task.Id, new AuditChanges()
                    .Value("orderId", orderId)
                    .Value("reason", reason)
                    .ToDictionary()),
                cancellationToken);
        }

        foreach (var shipment in shipments)
        {
            shipment.Status = FulfillmentStateMachine.NextShipment(FulfillmentActions.Cancel, shipment.Status)!;
            shipment.CancelledAt = now;
            shipment.Touch(userId, now);
        }

        if (back.Count > 0)
        {
            var first = (IHasOrder?)tasks.FirstOrDefault() ?? shipments.First();
            await Putaways.CreateAsync(
                db, outbox, requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved."),
                orderId, first.OrderNumber, first.WarehouseId, userId, now, back, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Picked-but-not-shipped goods go back to the shelf (PRD ORD-07): a putaway task, no ledger movement — the stock never
/// left on_hand, and reservations are the caller's business (released by Orders on cancel / short-close; kept when
/// only a shipment is cancelled).
/// </summary>
internal static class Putaways
{
    public static async Task<PickingTask> CreateAsync(
        WarehouseDbContext db,
        IOutbox outbox,
        Guid tenantId,
        Guid orderId,
        string orderNumber,
        Guid warehouseId,
        Guid userId,
        DateTimeOffset now,
        IReadOnlyList<(Guid OrderLineId, Guid VariantId, string Sku, string Name, string Unit, Guid? LocationId, decimal Quantity)> items,
        CancellationToken cancellationToken)
    {
        var putaway = PickingTask.Create(tenantId, PickingTaskTypes.Putaway, orderId, orderNumber, warehouseId, userId, now);
        var sequence = 0;
        foreach (var item in items.OrderBy(b => b.LocationId).ThenBy(b => b.Sku, StringComparer.Ordinal))
        {
            putaway.Lines.Add(PickingTaskLine.Create(
                putaway, ++sequence, item.OrderLineId, item.VariantId, item.Sku, item.Name, item.Unit, item.LocationId, item.Quantity));
        }

        db.PickingTasks.Add(putaway);
        await outbox.PublishAsync(
            FulfillmentEvents.PutawayCreated, new { taskId = putaway.Id, orderId, warehouseId = putaway.WarehouseId }, cancellationToken);
        return putaway;
    }
}

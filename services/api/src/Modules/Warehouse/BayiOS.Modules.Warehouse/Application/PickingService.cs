using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>
/// Picking tasks (inventory-and-orders §4.5, WHT-01…06). Every command runs in the request UnitOfWork. Commands that
/// may move the order lock the order header first through <see cref="IOrderFulfillment"/> (lock hierarchy step 1),
/// then the task (step 2); assignment commands lock only the task. Picking writes no ledger movement: stock stays
/// reserved on its shelf until the shipment is confirmed.
/// </summary>
internal sealed class PickingService(
    WarehouseDbContext db,
    FulfillmentQueries queries,
    FulfillmentLocks locks,
    FulfillmentAccess access,
    PickingTaskBuilder builder,
    PickingReallocator reallocator,
    IInventoryService inventory,
    IOrderFulfillment orders,
    IFulfillmentCatalog catalog,
    ITaskAssigneeDirectory assignees,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock)
{
    /// <summary>Order status in which the first picking activity moves the order to picking (§3.1 #4).</summary>
    private const string OrderApproved = "approved";

    // ---- Create / assign ----

    /// <summary>Creates the pick task for what is still open and reserved (approval creates the first one).</summary>
    public async Task<AppResult<PickingTaskResponse>> CreateAsync(CreatePickingTaskRequest request, CancellationToken cancellationToken)
    {
        if (request.OrderId is not { } orderId)
        {
            return FulfillmentErrors.Validation("orderId", "Required.");
        }

        var priority = request.Priority ?? PickingTaskPriorities.Normal;
        if (!PickingTaskPriorities.All.Contains(priority))
        {
            return InvalidPriority();
        }

        return await GuardAsync(async () =>
        {
            await db.JoinTransactionAsync(cancellationToken);
            var locked = await orders.LockAsync(orderId, cancellationToken);
            if (locked.Error is { } orderError)
            {
                return FulfillmentErrors.FromOrders(orderError);
            }

            var order = locked.Value!;
            if (!order.IsFulfillable)
            {
                return FulfillmentErrors.OrderNotFulfillable(order.Status);
            }

            await locks.LockOrderDocumentsAsync(orderId, cancellationToken);
            if (await db.PickingTasks.AnyAsync(t => t.OrderId == orderId && t.Type == PickingTaskTypes.Pick && PickingTaskStatuses.Active.Contains(t.Status), cancellationToken)
                || await db.Shipments.AnyAsync(s => s.OrderId == orderId && ShipmentStatuses.Unconfirmed.Contains(s.Status), cancellationToken))
            {
                return InProgress();
            }

            var task = await builder.CreateAsync(order.Id, order.Number, order.WarehouseId, order.Lines, cancellationToken, priority);
            if (task is null)
            {
                return new AppError(409, "picking.nothing_to_pick", "Nothing of this order is open and reserved");
            }

            await db.SaveChangesAsync(cancellationToken);
            return await DetailAsync(task.Id, cancellationToken);
        });
    }

    public async Task<AppResult<PickingTaskResponse>> AssignAsync(
        Guid id, long? expectedVersion, AssignPickingTaskRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId is not { } userId)
        {
            return FulfillmentErrors.Validation("userId", "Required.");
        }

        var role = await assignees.FindActiveRoleAsync(userId, cancellationToken);
        if (!RolePermissions.IsGranted(role, Permissions.TasksPick))
        {
            return FulfillmentErrors.Validation("userId", "Not an active member of this tenant whose role may pick.");
        }

        return await OnTaskAsync(id, expectedVersion, lockOrder: false, requireActor: false, async (task, _) =>
        {
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.Assign, task.Type, task.Status) is null)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.Assign, task.Status);
            }

            await SetAssigneeAsync(task, userId, cancellationToken);
            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken);
    }

    public Task<AppResult<PickingTaskResponse>> UnassignAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken) =>
        OnTaskAsync(id, expectedVersion, lockOrder: false, requireActor: false, async (task, _) =>
        {
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.Assign, task.Type, task.Status) is null)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.Assign, task.Status);
            }

            await SetAssigneeAsync(task, null, cancellationToken);
            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken);

    /// <summary>WHT-03 (#98): supervisors set the priority of an active task; the mobile work queue sorts by it.</summary>
    public async Task<AppResult<PickingTaskResponse>> SetPriorityAsync(
        Guid id, long? expectedVersion, SetPickingTaskPriorityRequest request, CancellationToken cancellationToken)
    {
        if (request.Priority is not { } priority || !PickingTaskPriorities.All.Contains(priority))
        {
            return InvalidPriority();
        }

        return await OnTaskAsync(id, expectedVersion, lockOrder: false, requireActor: false, async (task, _) =>
        {
            if (!PickingTaskStatuses.IsActive(task.Status))
            {
                return FulfillmentErrors.TaskTransition("set_priority", task.Status);
            }

            if (task.Priority == priority)
            {
                return await DetailAsync(task.Id, cancellationToken);
            }

            await audit.RecordAsync(
                new AuditEvent(AuditActions.PickingTaskPriorityChanged, FulfillmentAuditActions.TaskEntityType, task.Id, new AuditChanges()
                    .Field("priority", task.Priority, priority)
                    .ToDictionary()),
                cancellationToken);
            task.Priority = priority;
            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// #98: moves the task's open quantity away from deactivated shelves (or from <see cref="ReallocatePickingTaskRequest.FromLocationId"/>)
    /// to active shelves with free stock; reservations follow in the same transaction. What cannot move stays.
    /// </summary>
    public async Task<AppResult<ReallocatePickingTaskResponse>> ReallocateAsync(
        Guid id, long? expectedVersion, ReallocatePickingTaskRequest request, string? correlationId, CancellationToken cancellationToken) =>
        await OnTaskAsync<ReallocatePickingTaskResponse>(id, expectedVersion, lockOrder: true, requireActor: false, async (task, order) =>
        {
            if (task.Type != PickingTaskTypes.Pick || !PickingTaskStatuses.IsActive(task.Status))
            {
                return FulfillmentErrors.TaskTransition("reallocate", task.Status);
            }

            if (!order!.IsFulfillable)
            {
                return FulfillmentErrors.OrderNotFulfillable(order.Status);
            }

            if (request.FromLocationId is { } from
                && !await db.Locations.AsNoTracking().AnyAsync(l => l.Id == from && l.WarehouseId == task.WarehouseId, cancellationToken))
            {
                return FulfillmentErrors.Validation("fromLocationId", "Location not found in this task's warehouse.");
            }

            var summary = await reallocator.TaskAsync(task, request.FromLocationId, correlationId, cancellationToken);
            return await DetailAsync(task.Id, cancellationToken) is { Value: { } detail }
                ? new ReallocatePickingTaskResponse(detail, summary.Moved, summary.Unmoved)
                : AppError.NotFound();
        }, cancellationToken);

    /// <summary>Takes an unassigned active task from the pool; of two concurrent claims exactly one wins (row lock).</summary>
    public Task<AppResult<PickingTaskResponse>> ClaimAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken) =>
        OnTaskAsync(id, expectedVersion, lockOrder: false, requireActor: false, async (task, _) =>
        {
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.Claim, task.Type, task.Status) is null)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.Claim, task.Status);
            }

            if (task.AssignedTo == access.UserId)
            {
                return await DetailAsync(task.Id, cancellationToken);
            }

            if (task.AssignedTo is not null)
            {
                return new AppError(409, "picking.already_assigned", "The task was already taken by someone else");
            }

            await SetAssigneeAsync(task, access.UserId, cancellationToken);
            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken, claimVisibility: true);

    // ---- Work ----

    public Task<AppResult<PickingTaskResponse>> StartAsync(Guid id, long? expectedVersion, string? correlationId, CancellationToken cancellationToken) =>
        OnTaskAsync(id, expectedVersion, lockOrder: true, requireActor: true, async (task, order) =>
        {
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.Start, task.Type, task.Status) is not { } next)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.Start, task.Status);
            }

            if (await BeginWorkAsync(task, order!, next, correlationId, cancellationToken) is { } error)
            {
                return error;
            }

            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// One barcode scan (WHT-05, §4.5): code → variant (Catalog), location barcode/code → shelf, quantity (default 1).
    /// Rejections: <c>picking.unknown_code</c>, <c>picking.wrong_item</c> (not on this task),
    /// <c>picking.location_required</c>, <c>picking.unknown_location</c>, <c>picking.wrong_location</c> and
    /// <c>picking.over_pick</c>. Nothing changes on a rejection. When the item is (partly) reserved on another shelf and
    /// the scanned shelf is active with enough free stock, the reservation and the task line are re-allocated to the
    /// scanned shelf first (§4.5, #98); otherwise <c>picking.wrong_location</c> as before.
    /// </summary>
    public async Task<AppResult<PickScanResponse>> ScanAsync(
        Guid id, long? expectedVersion, PickScanRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var code = FulfillmentText.Clean(request.Code);
        if (code is null || code.Length > FulfillmentText.MaxCodeLength)
        {
            errors["code"] = [$"Required, at most {FulfillmentText.MaxCodeLength} characters."];
        }

        var location = FulfillmentText.Clean(request.Location);
        if (location?.Length > FulfillmentText.MaxCodeLength)
        {
            errors["location"] = [$"At most {FulfillmentText.MaxCodeLength} characters."];
        }

        var quantity = request.Quantity ?? 1m;
        if (!FulfillmentText.IsQuantity(quantity))
        {
            errors["quantity"] = ["Must be greater than 0 with at most 3 decimals."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await OnTaskAsync<PickScanResponse>(id, expectedVersion, lockOrder: true, requireActor: true, async (task, order) =>
        {
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.Pick, task.Type, task.Status) is not { } next)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.Pick, task.Status);
            }

            if (!order!.IsFulfillable)
            {
                return FulfillmentErrors.OrderNotFulfillable(order.Status);
            }

            if (await catalog.ResolveCodeAsync(code!, cancellationToken) is not { } variantId)
            {
                return new AppError(422, "picking.unknown_code", "The scanned code matches no product");
            }

            var candidates = task.Lines.Where(l => l.VariantId == variantId).OrderBy(l => l.Sequence).ToList();
            if (request.LineId is { } lineId)
            {
                var line = task.Lines.SingleOrDefault(l => l.Id == lineId);
                if (line is null)
                {
                    return FulfillmentErrors.Validation("lineId", "Line not found in this task.");
                }

                candidates = line.VariantId == variantId ? [line] : [];
            }

            if (candidates.Count == 0)
            {
                return WrongItem();
            }

            var variant = (await catalog.FindVariantsAsync([variantId], cancellationToken)).GetValueOrDefault(variantId);
            if (variant is { AllowsFraction: false } && quantity != decimal.Truncate(quantity))
            {
                return FulfillmentErrors.Validation("quantity", "The item's unit does not allow fractions.");
            }

            if (candidates.Any(l => l.LocationId is not null))
            {
                if (location is null)
                {
                    return new AppError(422, "picking.location_required", "Scan the shelf (location) barcode first");
                }

                var upper = location.ToUpperInvariant();
                var scanned = await db.Locations.AsNoTracking()
                    .Where(l => l.WarehouseId == task.WarehouseId && (l.Barcode == location || l.Code == upper))
                    .Select(l => new { l.Id, l.IsActive })
                    .FirstOrDefaultAsync(cancellationToken);
                if (scanned is null)
                {
                    return new AppError(422, "picking.unknown_location", "The scanned location does not exist in this warehouse");
                }

                var here = candidates.Where(l => l.LocationId == scanned.Id).ToList();
                var openHere = here.Sum(l => l.Remaining);
                var elsewhere = candidates.Where(l => l.LocationId is { } other && other != scanned.Id && l.Remaining > 0).ToList();
                if (quantity > openHere && elsewhere.Count > 0)
                {
                    // §4.5 / #98: the picker took the item from another shelf → re-allocate the reservation there first.
                    if (await ReallocateForScanAsync(task, variantId, scanned.Id, scanned.IsActive, openHere, elsewhere, quantity, correlationId, cancellationToken) is { } moveError)
                    {
                        return moveError;
                    }

                    here = [.. task.Lines.Where(l => l.VariantId == variantId && l.LocationId == scanned.Id).OrderBy(l => l.Sequence)];
                }

                candidates = here;
                if (candidates.Count == 0)
                {
                    return WrongLocation();
                }
            }

            var open = candidates.Sum(l => l.Remaining);
            if (quantity > open)
            {
                return OverPick(open);
            }

            var changed = new List<PickingTaskLine>();
            var left = quantity;
            foreach (var line in candidates)
            {
                var take = Math.Min(left, line.Remaining);
                if (take <= 0)
                {
                    continue;
                }

                line.QtyPicked += take;
                changed.Add(line);
                left -= take;
                if (left == 0)
                {
                    break;
                }
            }

            if (await BeginWorkAsync(task, order, next, correlationId, cancellationToken) is { } error)
            {
                return error;
            }

            task.Touch(access.UserId, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return new PickScanResponse(
                task.Id,
                task.Status,
                task.RowVersion,
                [.. changed.Select(l => new PickLineProgress(l.Id, l.VariantId, l.Sku, l.LocationId, l.QtyToPick, l.QtyPicked, l.QtyShort, l.Remaining))],
                task.Lines.Count(l => l.Remaining > 0));
        }, cancellationToken);
    }

    /// <summary>
    /// WHT-06: the line (or part of it) cannot be picked. The quantity stays reserved — releasing it is a manager
    /// decision (short-close, §3.1 6s) — the order is flagged and an outbox notification is written.
    /// </summary>
    public async Task<AppResult<PickingTaskResponse>> ReportShortageAsync(
        Guid id, Guid lineId, long? expectedVersion, ReportShortageRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        if (ValidateShortage(request) is { } invalid)
        {
            return invalid;
        }

        return await OnTaskAsync(id, expectedVersion, lockOrder: true, requireActor: true, async (task, order) =>
        {
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.ReportShortage, task.Type, task.Status) is not { } next)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.ReportShortage, task.Status);
            }

            if (!order!.IsFulfillable)
            {
                return FulfillmentErrors.OrderNotFulfillable(order.Status);
            }

            if (task.Lines.SingleOrDefault(l => l.Id == lineId) is not { } line)
            {
                return AppError.NotFound();
            }

            if (line.Remaining == 0)
            {
                return new AppError(409, "picking.line_closed", "Nothing is left open on this line");
            }

            var quantity = request.Quantity ?? line.Remaining;
            if (quantity > line.Remaining)
            {
                return new AppError(422, "picking.shortage_exceeds_open", $"Only {line.Remaining} is still open on this line");
            }

            line.QtyShort += quantity;
            line.ShortageReason = request.Reason;
            if (await BeginWorkAsync(task, order, next, correlationId, cancellationToken) is { } error)
            {
                return error;
            }

            if (await ShortageReporter.ReportAsync(
                orders, audit, outbox, task.OrderId, task.Id, null, line.OrderLineId, line.VariantId, line.Sku, quantity, request.Reason!, "picking", correlationId, cancellationToken) is { } flagError)
            {
                return flagError;
            }

            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// §3.1 #5: every line picked or reported short → completed / completed_with_shortage; the order moves to packed
    /// and the picked quantities become a draft shipment. A task with nothing picked completes without either.
    /// Putaway tasks just complete (goods back on their shelves).
    /// </summary>
    public Task<AppResult<PickingTaskResponse>> CompleteAsync(Guid id, long? expectedVersion, string? correlationId, CancellationToken cancellationToken) =>
        OnTaskAsync(id, expectedVersion, lockOrder: true, requireActor: true, async (task, order) =>
        {
            if (task.Type == PickingTaskTypes.Pick && task.Lines.Where(l => l.Remaining > 0).ToList() is { Count: > 0 } open)
            {
                return new AppError(
                    409,
                    "picking.incomplete",
                    "Every line must be picked or reported short before completing",
                    open.ToDictionary(l => $"lines[{l.Sequence - 1}]", l => new[] { $"{l.Sku}: {l.Remaining} still open." }));
            }

            var withShortage = task.Lines.Any(l => l.QtyShort > 0);
            if (FulfillmentStateMachine.NextTask(FulfillmentActions.Complete, task.Type, task.Status, withShortage) is not { } next)
            {
                return FulfillmentErrors.TaskTransition(FulfillmentActions.Complete, task.Status);
            }

            var now = clock.GetUtcNow();
            task.StartedAt ??= now;
            task.Status = next;
            task.CompletedAt = now;
            task.CompletedBy = access.UserId;
            var picked = task.Lines.Sum(l => l.QtyPicked);
            Guid? shipmentId = null;
            if (task.Type == PickingTaskTypes.Putaway)
            {
                foreach (var line in task.Lines)
                {
                    line.QtyPicked = line.QtyToPick - line.QtyShort;
                }
            }
            else if (picked > 0)
            {
                if (!order!.IsFulfillable)
                {
                    return FulfillmentErrors.OrderNotFulfillable(order.Status);
                }

                if (order.Status == OrderApproved && Map(await orders.StartPickingAsync(task.OrderId, correlationId, cancellationToken)) is { } startError)
                {
                    return startError;
                }

                if (Map(await orders.MarkPackedAsync(task.OrderId, correlationId, cancellationToken)) is { } packError)
                {
                    return packError;
                }

                var number = await db.Shipments.CountAsync(s => s.OrderId == task.OrderId, cancellationToken) + 1;
                var shipment = Shipment.Create(
                    task.TenantId, $"{task.OrderNumber}-S{number}", task.OrderId, task.OrderNumber, task.Id, task.WarehouseId, access.UserId, now);
                foreach (var line in task.Lines.Where(l => l.QtyPicked > 0).OrderBy(l => l.Sequence))
                {
                    shipment.Lines.Add(ShipmentLine.Create(shipment, line));
                }

                db.Shipments.Add(shipment);
                shipmentId = shipment.Id;
            }

            await audit.RecordAsync(
                new AuditEvent(AuditActions.OrderPickProgress, FulfillmentAuditActions.TaskEntityType, task.Id, new AuditChanges()
                    .Value("orderId", task.OrderId)
                    .Value("type", task.Type)
                    .Value("status", task.Status)
                    .Value("picked", picked)
                    .Value("short", task.Lines.Sum(l => l.QtyShort))
                    .Value("shipmentId", shipmentId)
                    .ToDictionary()),
                cancellationToken);
            await outbox.PublishAsync(
                FulfillmentEvents.PickingCompleted,
                new { taskId = task.Id, orderId = task.OrderId, type = task.Type, status = task.Status, shipmentId },
                cancellationToken);
            return await SaveAndDetailAsync(task, cancellationToken);
        }, cancellationToken);

    // ---- Shared ----

    /// <summary>Common shortage validation for picking and packing.</summary>
    internal static AppError? ValidateShortage(ReportShortageRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Reason is null || !ShortageReasons.All.Contains(request.Reason))
        {
            errors["reason"] = [$"Must be one of: {string.Join(", ", ShortageReasons.All)}."];
        }

        if (request.Quantity is { } quantity && !FulfillmentText.IsQuantity(quantity))
        {
            errors["quantity"] = ["Must be greater than 0 with at most 3 decimals."];
        }

        return errors.Count > 0 ? AppError.Validation(errors) : null;
    }

    /// <summary>open → in_progress (first activity); the order follows approved → picking (§3.1 #4).</summary>
    private async Task<AppError?> BeginWorkAsync(PickingTask task, FulfillmentOrder order, string next, string? correlationId, CancellationToken cancellationToken)
    {
        if (task.Status == PickingTaskStatuses.Open)
        {
            task.StartedAt = clock.GetUtcNow();
        }

        task.Status = next;
        return task.Type == PickingTaskTypes.Pick && order.Status == OrderApproved
            ? Map(await orders.StartPickingAsync(task.OrderId, correlationId, cancellationToken))
            : null;
    }

    private async Task SetAssigneeAsync(PickingTask task, Guid? userId, CancellationToken cancellationToken)
    {
        if (task.AssignedTo == userId)
        {
            return;
        }

        var previous = task.AssignedTo;
        task.AssignedTo = userId;
        task.AssignedAt = userId is null ? null : clock.GetUtcNow();
        await audit.RecordAsync(
            new AuditEvent(AuditActions.PickingTaskAssigned, FulfillmentAuditActions.TaskEntityType, task.Id, new AuditChanges()
                .Field("assignedTo", previous, userId)
                .ToDictionary()),
            cancellationToken);
        if (userId is not null)
        {
            // assignedBy lets the Notifications subscriber skip self-claims (no push to the one who took the task).
            await outbox.PublishAsync(
                FulfillmentEvents.PickingTaskAssigned, new { taskId = task.Id, orderId = task.OrderId, assignedTo = userId, assignedBy = access.UserId }, cancellationToken);
        }
    }

    private async Task<AppResult<PickingTaskResponse>> SaveAndDetailAsync(PickingTask task, CancellationToken cancellationToken)
    {
        task.Touch(access.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(task.Id, cancellationToken);
    }

    private async Task<AppResult<PickingTaskResponse>> DetailAsync(Guid id, CancellationToken cancellationToken) =>
        await queries.TaskAsync(id, cancellationToken) is { } detail ? detail : AppError.NotFound();

    /// <summary>
    /// Loads the task (404 when the caller may not see it), locks the order header when the command may move the
    /// order, then locks the task, checks the actor and the If-Match version and runs the change.
    /// </summary>
    /// <param name="claimVisibility">Claim: any active task may be addressed, so a lost race answers 409, not 404.</param>
    private Task<AppResult<T>> OnTaskAsync<T>(
        Guid id,
        long? expectedVersion,
        bool lockOrder,
        bool requireActor,
        Func<PickingTask, FulfillmentOrder?, Task<AppResult<T>>> change,
        CancellationToken cancellationToken,
        bool claimVisibility = false) =>
        GuardAsync(async () =>
        {
            await db.JoinTransactionAsync(cancellationToken);
            var head = await db.PickingTasks.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => new { t.OrderId, t.AssignedTo, t.Status })
                .SingleOrDefaultAsync(cancellationToken);
            if (head is null || !(access.CanSee(head.AssignedTo, head.Status) || (claimVisibility && PickingTaskStatuses.IsActive(head.Status))))
            {
                return AppError.NotFound();
            }

            FulfillmentOrder? order = null;
            if (lockOrder)
            {
                var locked = await orders.LockAsync(head.OrderId, cancellationToken);
                if (locked.Error is { } orderError)
                {
                    return FulfillmentErrors.FromOrders(orderError);
                }

                order = locked.Value;
            }

            if (!await locks.LockTaskAsync(id, cancellationToken))
            {
                return AppError.NotFound();
            }

            var task = await db.PickingTasks.Include(t => t.Lines).SingleAsync(t => t.Id == id, cancellationToken);
            if (requireActor && !access.CanAct(task.AssignedTo))
            {
                return FulfillmentErrors.NotAssignee();
            }

            if (expectedVersion is { } version && version != task.RowVersion)
            {
                return AppError.VersionMismatch();
            }

            return await change(task, order);
        });

    private static async Task<AppResult<T>> GuardAsync<T>(Func<Task<AppResult<T>>> body)
    {
        try
        {
            return await body();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            return FulfillmentErrors.Busy();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected })
        {
            return FulfillmentErrors.Busy();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique
            && unique.ConstraintName is WarehouseDbContext.ActivePickTaskUniqueIndex or WarehouseDbContext.OpenShipmentUniqueIndex)
        {
            return InProgress();
        }
    }

    /// <summary>
    /// Moves the missing part of a scan (<paramref name="quantity"/> − what is open on the scanned shelf) from the item's
    /// other task lines to the scanned shelf: the reservation moves in Inventory (target must be an active shelf with that
    /// much free stock) and the task lines follow (<see cref="ReservationHolders"/>). Null on success.
    /// </summary>
    private async Task<AppError?> ReallocateForScanAsync(
        PickingTask task,
        Guid variantId,
        Guid scannedLocationId,
        bool scannedIsActive,
        decimal openHere,
        IReadOnlyList<PickingTaskLine> elsewhere,
        decimal quantity,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var open = openHere + elsewhere.Sum(l => l.Remaining);
        if (quantity > open)
        {
            return OverPick(open);
        }

        if (!scannedIsActive)
        {
            return openHere == 0 ? WrongLocation() : OverPick(openHere);
        }

        var need = quantity - openHere;
        var shifts = new List<ReservationShift>();
        foreach (var line in elsewhere)
        {
            var take = Math.Min(need, line.Remaining);
            shifts.Add(new ReservationShift(line.OrderLineId, variantId, line.LocationId!.Value, scannedLocationId, take));
            need -= take;
            if (need == 0)
            {
                break;
            }
        }

        shifts = [.. shifts
            .GroupBy(s => (s.OrderLineId, s.FromLocationId))
            .Select(g => g.First() with { Quantity = g.Sum(s => s.Quantity) })];
        var result = await inventory.ReallocateAsync(
            new ReallocateReservations(task.WarehouseId, shifts, ReallocationReasons.PickedElsewhere, correlationId), cancellationToken);
        return result.Error switch
        {
            null => null,
            { Code: "inventory.busy" } => FulfillmentErrors.Busy(),
            _ when openHere == 0 => new AppError(
                422, "picking.wrong_location", "This item is not reserved on the scanned shelf and the shelf has not enough free stock to re-allocate"),
            _ => OverPick(openHere),
        };
    }

    private static AppError WrongLocation() => new(422, "picking.wrong_location", "This item is not picked from the scanned location");

    private static AppError OverPick(decimal open) => new(422, "picking.over_pick", $"Over-pick: {open} left to pick for this item here");

    private static AppError InvalidPriority() =>
        FulfillmentErrors.Validation("priority", $"Must be one of: {string.Join(", ", PickingTaskPriorities.All)}.");

    private static AppError InProgress() =>
        new(409, "picking.fulfillment_in_progress", "The order already has an active pick task or an unconfirmed shipment");

    private static AppError WrongItem() => new(422, "picking.wrong_item", "The scanned item is not on this picking task");

    private static AppError? Map<T>(FulfillmentResult<T> result) => result.Error is { } error ? FulfillmentErrors.FromOrders(error) : null;
}

/// <summary>WHT-06 / #38: a shortage flags the order (through Orders), is audited and goes to the outbox.</summary>
internal static class ShortageReporter
{
    public static async Task<AppError?> ReportAsync(
        IOrderFulfillment orders,
        IAuditLog audit,
        IOutbox outbox,
        Guid orderId,
        Guid? taskId,
        Guid? shipmentId,
        Guid orderLineId,
        Guid variantId,
        string sku,
        decimal quantity,
        string reason,
        string stage,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var flagged = await orders.ReportShortageAsync(orderId, $"{stage}: {sku} × {quantity} ({reason})", correlationId, cancellationToken);
        if (flagged.Error is { } error)
        {
            return FulfillmentErrors.FromOrders(error);
        }

        var entityType = taskId is null ? FulfillmentAuditActions.ShipmentEntityType : FulfillmentAuditActions.TaskEntityType;
        await audit.RecordAsync(
            new AuditEvent(AuditActions.PickingShortageReported, entityType, taskId ?? shipmentId, new AuditChanges()
                .Value("orderId", orderId)
                .Value("orderLineId", orderLineId)
                .Value("variantId", variantId)
                .Value("quantity", quantity)
                .Value("reason", reason)
                .Value("stage", stage)
                .ToDictionary()),
            cancellationToken);
        await outbox.PublishAsync(
            FulfillmentEvents.ShortageReported,
            new { orderId, taskId, shipmentId, orderLineId, variantId, quantity, reason, stage },
            cancellationToken);
        return null;
    }
}

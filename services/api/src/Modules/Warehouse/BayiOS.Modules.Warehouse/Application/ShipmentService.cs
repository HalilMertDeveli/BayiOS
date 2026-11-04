using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>
/// Shipments (inventory-and-orders §4.6, WHT-07/08). A draft comes from a completed pick task; the optional pack
/// check scans every item again into packages; confirming issues the stock — through Orders, which is the only
/// caller of <c>IInventoryService.IssueAsync</c> — exactly once: the order header lock serialises confirmations and a
/// confirmed shipment never confirms again.
/// </summary>
internal sealed class ShipmentService(
    WarehouseDbContext db,
    FulfillmentQueries queries,
    FulfillmentLocks locks,
    FulfillmentAccess access,
    IOrderFulfillment orders,
    IFulfillmentCatalog catalog,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider clock)
{
    /// <summary>
    /// Pack check: Σ packed per line must equal what ships. Rejections: <c>packing.unknown_code</c>,
    /// <c>packing.wrong_item</c>, <c>packing.over_pack</c>, <c>packing.quantity_mismatch</c> (report missing items as a
    /// shortage first). Records the packages; the shipment becomes packed.
    /// </summary>
    public async Task<AppResult<ShipmentResponse>> PackAsync(
        Guid id, long? expectedVersion, PackShipmentRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var packages = request.Packages ?? [];
        if (packages.Count is 0 or > FulfillmentText.MaxPackages)
        {
            errors["packages"] = [$"Between 1 and {FulfillmentText.MaxPackages} packages are required."];
        }

        for (var p = 0; p < packages.Count; p++)
        {
            var items = packages[p]?.Items ?? [];
            if (items.Count is 0 or > FulfillmentText.MaxLines)
            {
                errors[$"packages[{p}].items"] = [$"Between 1 and {FulfillmentText.MaxLines} items are required."];
                continue;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var code = FulfillmentText.Clean(item?.Code);
                if (item is null || (code is null) == (item.ShipmentLineId is null) || code?.Length > FulfillmentText.MaxCodeLength)
                {
                    errors[$"packages[{p}].items[{i}]"] = ["Give either code or shipmentLineId."];
                }
                else if (item.Quantity is not { } quantity || !FulfillmentText.IsQuantity(quantity))
                {
                    errors[$"packages[{p}].items[{i}].quantity"] = ["Must be greater than 0 with at most 3 decimals."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await OnShipmentAsync(id, expectedVersion, lockOrder: false, async (shipment, _) =>
        {
            if (FulfillmentStateMachine.NextShipment(FulfillmentActions.Pack, shipment.Status) is not { } next)
            {
                return FulfillmentErrors.ShipmentTransition(FulfillmentActions.Pack, shipment.Status);
            }

            var lines = shipment.Lines.Where(l => l.Quantity > 0).OrderBy(l => l.Sku, StringComparer.Ordinal).ThenBy(l => l.Id).ToList();
            var resolved = new Dictionary<string, Guid?>(StringComparer.Ordinal);
            foreach (var code in packages.SelectMany(p => p.Items!).Select(i => FulfillmentText.Clean(i.Code)).OfType<string>().Distinct())
            {
                resolved[code] = await catalog.ResolveCodeAsync(code, cancellationToken);
            }

            // Allocate every scanned item to a line: by id, or by variant filling lines in order (overflow → last line).
            var packed = lines.ToDictionary(l => l.Id, _ => 0m);
            var allocations = new List<(int Package, Guid LineId, decimal Quantity)>();
            for (var p = 0; p < packages.Count; p++)
            {
                foreach (var item in packages[p].Items!)
                {
                    var quantity = item.Quantity!.Value;
                    List<ShipmentLine> targets;
                    if (item.ShipmentLineId is { } lineId)
                    {
                        targets = [.. lines.Where(l => l.Id == lineId)];
                        if (targets.Count == 0)
                        {
                            return FulfillmentErrors.Validation($"packages[{p}]", "Shipment line not found (or nothing ships on it).");
                        }
                    }
                    else
                    {
                        var code = FulfillmentText.Clean(item.Code)!;
                        if (resolved[code] is not { } variantId)
                        {
                            return new AppError(422, "packing.unknown_code", "The scanned code matches no product");
                        }

                        targets = [.. lines.Where(l => l.VariantId == variantId)];
                        if (targets.Count == 0)
                        {
                            return new AppError(422, "packing.wrong_item", "The scanned item is not in this shipment");
                        }
                    }

                    var left = quantity;
                    for (var t = 0; t < targets.Count && left > 0; t++)
                    {
                        var take = t == targets.Count - 1 ? left : Math.Min(left, targets[t].Quantity - packed[targets[t].Id]);
                        if (take <= 0)
                        {
                            continue;
                        }

                        packed[targets[t].Id] += take;
                        allocations.Add((p, targets[t].Id, take));
                        left -= take;
                    }
                }
            }

            var over = lines.Where(l => packed[l.Id] > l.Quantity).ToList();
            if (over.Count > 0)
            {
                return new AppError(422, "packing.over_pack", "More items were packed than were picked", Mismatches(over, packed));
            }

            var under = lines.Where(l => packed[l.Id] < l.Quantity).ToList();
            if (under.Count > 0)
            {
                return new AppError(
                    422,
                    "packing.quantity_mismatch",
                    "Packed quantities differ from the picked quantities; report missing items as a shortage first",
                    Mismatches(under, packed));
            }

            foreach (var (packageIndex, group) in allocations.GroupBy(a => a.Package).Select(g => (g.Key, g)))
            {
                var package = ShipmentPackage.Create(shipment, packageIndex + 1);
                foreach (var (lineId, quantity) in group.GroupBy(a => a.LineId).Select(g => (g.Key, g.Sum(a => a.Quantity))))
                {
                    package.Items.Add(ShipmentPackageItem.Create(package, lineId, quantity));
                }

                shipment.Packages.Add(package);
                db.ShipmentPackages.Add(package);
            }

            foreach (var line in lines)
            {
                line.PackedQuantity = packed[line.Id];
            }

            var now = clock.GetUtcNow();
            shipment.Status = next;
            shipment.PackageCount = packages.Count;
            shipment.PackedAt = now;
            shipment.PackedBy = access.UserId;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.ShipmentPacked, FulfillmentAuditActions.ShipmentEntityType, shipment.Id, new AuditChanges()
                    .Value("orderId", shipment.OrderId)
                    .Value("packageCount", packages.Count)
                    .Value("quantity", lines.Sum(l => l.Quantity))
                    .ToDictionary()),
                cancellationToken);
            return await SaveAndDetailAsync(shipment, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// A shortage found while packing (WHT-06): the line ships less. The quantity stays reserved on the order line —
    /// the manager short-closes (§3.1 6s) or picks again; the order is flagged and the outbox notified.
    /// </summary>
    public async Task<AppResult<ShipmentResponse>> ReportShortageAsync(
        Guid id, Guid lineId, long? expectedVersion, ReportShortageRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        if (PickingService.ValidateShortage(request) is { } invalid)
        {
            return invalid;
        }

        return await OnShipmentAsync(id, expectedVersion, lockOrder: true, async (shipment, order) =>
        {
            if (FulfillmentStateMachine.NextShipment(FulfillmentActions.ReportShortage, shipment.Status) is null)
            {
                return FulfillmentErrors.ShipmentTransition(FulfillmentActions.ReportShortage, shipment.Status);
            }

            if (!order!.IsFulfillable)
            {
                return FulfillmentErrors.OrderNotFulfillable(order.Status);
            }

            if (shipment.Lines.SingleOrDefault(l => l.Id == lineId) is not { } line)
            {
                return AppError.NotFound();
            }

            if (line.Quantity == 0)
            {
                return new AppError(409, "picking.line_closed", "Nothing ships on this line any more");
            }

            var quantity = request.Quantity ?? line.Quantity;
            if (quantity > line.Quantity)
            {
                return new AppError(422, "picking.shortage_exceeds_open", $"Only {line.Quantity} ships on this line");
            }

            line.Quantity -= quantity;
            line.ShortQuantity += quantity;
            line.ShortageReason = request.Reason;
            if (await ShortageReporter.ReportAsync(
                orders, audit, outbox, shipment.OrderId, null, shipment.Id, line.OrderLineId, line.VariantId, line.Sku, quantity, request.Reason!, "packing", correlationId, cancellationToken) is { } error)
            {
                return error;
            }

            return await SaveAndDetailAsync(shipment, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// §4.6 / WHT-08: stock leaves the building. Orders issues the shipment lines from their reservations
    /// (<c>shipment_out</c>, source = this shipment) and moves the order (shipped, or fulfillment partial). A second
    /// confirmation — concurrent or later, with a different Idempotency-Key — answers 409 <c>shipment.already_confirmed</c>
    /// and changes nothing; a replay with the same key gets the stored response.
    /// </summary>
    public async Task<AppResult<ConfirmShipmentResponse>> ConfirmAsync(
        Guid id, long? expectedVersion, ConfirmShipmentRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var carrier = FulfillmentText.Clean(request.Carrier);
        var tracking = FulfillmentText.Clean(request.TrackingNumber);
        var errors = new Dictionary<string, string[]>();
        if (carrier?.Length > FulfillmentText.MaxShortTextLength || tracking?.Length > FulfillmentText.MaxShortTextLength)
        {
            errors["carrier"] = [$"Carrier and tracking number are at most {FulfillmentText.MaxShortTextLength} characters."];
        }

        if (request.PackageCount is <= 0)
        {
            errors["packageCount"] = ["Must be greater than 0."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await OnShipmentAsync<ConfirmShipmentResponse>(id, expectedVersion, lockOrder: true, async (shipment, order) =>
        {
            if (shipment.Status == ShipmentStatuses.Confirmed)
            {
                return new AppError(409, "shipment.already_confirmed", "The shipment is already confirmed; stock was issued once");
            }

            if (FulfillmentStateMachine.NextShipment(FulfillmentActions.Confirm, shipment.Status) is not { } next)
            {
                return FulfillmentErrors.ShipmentTransition(FulfillmentActions.Confirm, shipment.Status);
            }

            if (!order!.IsFulfillable)
            {
                return FulfillmentErrors.OrderNotFulfillable(order.Status);
            }

            var lines = shipment.Lines.Where(l => l.Quantity > 0).ToList();
            if (lines.Count == 0)
            {
                return new AppError(409, "shipment.empty", "Nothing ships on this shipment");
            }

            var packageCount = request.PackageCount ?? shipment.PackageCount;
            var shipped = await orders.ShipAsync(
                new ShipOrderCommand(
                    shipment.OrderId,
                    shipment.Id,
                    [.. lines.Select(l => new ShipOrderLine(l.Id, l.OrderLineId, l.LocationId, l.Quantity))],
                    carrier,
                    tracking,
                    packageCount,
                    correlationId),
                cancellationToken);
            if (shipped.Error is { } shipError)
            {
                return FulfillmentErrors.FromOrders(shipError);
            }

            var now = clock.GetUtcNow();
            shipment.Status = next;
            shipment.Carrier = carrier ?? shipment.Carrier;
            shipment.TrackingNumber = tracking ?? shipment.TrackingNumber;
            shipment.PackageCount = packageCount;
            shipment.ShippedAt = now;
            shipment.ConfirmedBy = access.UserId;
            var recorded = shipped.Value!;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.ShipmentConfirmed, FulfillmentAuditActions.ShipmentEntityType, shipment.Id, new AuditChanges()
                    .Value("orderId", shipment.OrderId)
                    .Value("quantity", lines.Sum(l => l.Quantity))
                    .Value("movements", recorded.MovementIds.Count)
                    .Value("orderStatus", recorded.Status)
                    .Value("fulfillmentStatus", recorded.FulfillmentStatus)
                    .ToDictionary()),
                cancellationToken);
            await outbox.PublishAsync(
                FulfillmentEvents.ShipmentConfirmed,
                new { shipmentId = shipment.Id, orderId = shipment.OrderId, orderStatus = recorded.Status, fulfillmentStatus = recorded.FulfillmentStatus },
                cancellationToken);
            var detail = await SaveAndDetailAsync(shipment, cancellationToken);
            return detail.Error is { } detailError
                ? detailError
                : new ConfirmShipmentResponse(detail.Value!, recorded.Status, recorded.FulfillmentStatus, recorded.MovementIds);
        }, cancellationToken);
    }

    private static Dictionary<string, string[]> Mismatches(IEnumerable<ShipmentLine> lines, Dictionary<Guid, decimal> packed) =>
        lines.ToDictionary(l => l.Id.ToString(), l => new[] { $"{l.Sku}: ships {l.Quantity}, packed {packed[l.Id]}." });

    private async Task<AppResult<ShipmentResponse>> SaveAndDetailAsync(Shipment shipment, CancellationToken cancellationToken)
    {
        shipment.Touch(access.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await queries.ShipmentAsync(shipment.Id, cancellationToken) is { } detail ? detail : AppError.NotFound();
    }

    /// <summary>
    /// Loads the shipment (404 when the caller may not see it: supervisors, or the assignee of its pick task), locks
    /// the order header when the command may move the order or stock, then locks the shipment and checks If-Match.
    /// </summary>
    private async Task<AppResult<T>> OnShipmentAsync<T>(
        Guid id, long? expectedVersion, bool lockOrder, Func<Shipment, FulfillmentOrder?, Task<AppResult<T>>> change, CancellationToken cancellationToken)
    {
        try
        {
            await db.JoinTransactionAsync(cancellationToken);
            var head = await db.Shipments.AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => new { s.OrderId, Assignee = db.PickingTasks.Where(t => t.Id == s.PickingTaskId).Select(t => t.AssignedTo).FirstOrDefault() })
                .SingleOrDefaultAsync(cancellationToken);
            if (head is null || !access.CanActOnShipment(head.Assignee))
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

            if (!await locks.LockShipmentAsync(id, cancellationToken))
            {
                return AppError.NotFound();
            }

            var shipment = await db.Shipments.Include(s => s.Lines).Include(s => s.Packages).SingleAsync(s => s.Id == id, cancellationToken);
            if (expectedVersion is { } version && version != shipment.RowVersion)
            {
                return AppError.VersionMismatch();
            }

            return await change(shipment, order);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            return FulfillmentErrors.Busy();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected })
        {
            return FulfillmentErrors.Busy();
        }
    }
}

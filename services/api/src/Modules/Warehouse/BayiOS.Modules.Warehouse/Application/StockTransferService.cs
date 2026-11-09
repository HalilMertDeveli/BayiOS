using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseModel = BayiOS.Modules.Warehouse.Domain.Warehouse;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>Stable problem codes of transfers and counts (api-strategy §6) and the shared engine-error mapping.</summary>
internal static class StockDocumentErrors
{
    public static AppError TransferTransition(string action, string status) =>
        new(409, "transfer.invalid_transition", $"Action '{action}' is not allowed for a transfer in status '{status}'");

    public static AppError CountTransition(string action, string status) =>
        new(409, "count.invalid_transition", $"Action '{action}' is not allowed for a stock count in status '{status}'");

    public static AppError TransferBusy() => new(409, "transfer.busy", "The transfer is locked by another operation; retry shortly");

    public static AppError CountBusy() => new(409, "count.busy", "The stock count is locked by another operation; retry shortly");

    public static AppError Validation(string field, string message) =>
        AppError.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static AppError FromInventory(InventoryError error) =>
        new(error.Status, error.Code, error.Title, error.Errors, error.Shortages);

    public static bool IsLockConflict(Exception ex) => ex switch
    {
        PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected } => true,
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected } } => true,
        _ => false,
    };
}

/// <summary>
/// Two-stage warehouse transfers (OD-01, AF-07, US-INV-007; inventory-and-orders §4.7). Every stock change goes through
/// <see cref="IInventoryService.TransferAsync"/>: dispatch moves the sent quantity from the source shelves to the
/// transfer's in-transit point ("Yolda": counted in no warehouse), each receipt moves what arrived from there into the
/// destination, and closing moves what never arrived back to the source or writes it off as lost. The document row is
/// locked first (lock hierarchy step 2), then the balances (step 3), so racing actions on one transfer serialise and
/// a dispatch racing another dispatch for the same stock can only take what is still available.
/// </summary>
internal sealed class StockTransferService(
    WarehouseDbContext db,
    StockTransferQueries queries,
    FulfillmentLocks locks,
    FulfillmentAccess access,
    IInventoryService inventory,
    IFulfillmentCatalog catalog,
    IAuditLog audit,
    TimeProvider clock)
{
    public const string EntityType = "stock_transfer";

    public async Task<AppResult<StockTransferResponse>> CreateAsync(CreateStockTransferRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var note = FulfillmentText.Clean(request.Note);
        if (note?.Length > FulfillmentText.MaxReasonLength)
        {
            errors["note"] = [$"At most {FulfillmentText.MaxReasonLength} characters."];
        }

        if (request.SourceWarehouseId is null)
        {
            errors["sourceWarehouseId"] = ["Required."];
        }

        if (request.DestinationWarehouseId is null)
        {
            errors["destinationWarehouseId"] = ["Required."];
        }
        else if (request.DestinationWarehouseId == request.SourceWarehouseId)
        {
            // AF-07: source and destination cannot be the same warehouse (shelf moves: POST /inventory/relocations).
            errors["destinationWarehouseId"] = ["Source and destination warehouses must differ."];
        }

        var lines = request.Lines ?? [];
        if (lines.Count is 0 or > FulfillmentText.MaxLines)
        {
            errors["lines"] = [$"Between 1 and {FulfillmentText.MaxLines} lines are required."];
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i]?.VariantId is null)
            {
                errors[$"lines[{i}].variantId"] = ["Required."];
            }

            if (lines[i]?.Quantity is not { } quantity || !FulfillmentText.IsQuantity(quantity))
            {
                errors[$"lines[{i}].quantity"] = ["Must be greater than 0 with at most 3 decimals."];
            }
        }

        if (errors.Count == 0 && lines.Select(l => (l.VariantId, l.SourceLocationId)).Distinct().Count() != lines.Count)
        {
            errors["lines"] = ["Each (variant, source location) may appear only once."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        await db.JoinTransactionAsync(cancellationToken);
        var ids = new[] { request.SourceWarehouseId!.Value, request.DestinationWarehouseId!.Value };
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var source = CheckWarehouse(errors, "sourceWarehouseId", warehouses.GetValueOrDefault(ids[0]));
        var destination = CheckWarehouse(errors, "destinationWarehouseId", warehouses.GetValueOrDefault(ids[1]));
        if (source is null || destination is null)
        {
            return AppError.Validation(errors);
        }

        var locationIds = lines.SelectMany(l => new[] { l.SourceLocationId, l.DestinationLocationId }).OfType<Guid>().Distinct().ToList();
        var locations = await db.Locations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, cancellationToken);
        var variants = await catalog.FindVariantsAsync([.. lines.Select(l => l.VariantId!.Value).Distinct()], cancellationToken);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            CheckLocation(errors, $"lines[{i}].sourceLocationId", source, line.SourceLocationId, locations, required: true);
            CheckLocation(errors, $"lines[{i}].destinationLocationId", destination, line.DestinationLocationId, locations, required: false);
            if (!variants.TryGetValue(line.VariantId!.Value, out var variant))
            {
                errors[$"lines[{i}].variantId"] = ["Variant not found."];
            }
            else if (!variant.AllowsFraction && line.Quantity!.Value != decimal.Truncate(line.Quantity.Value))
            {
                errors[$"lines[{i}].quantity"] = ["The variant's unit does not allow fractions."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var now = clock.GetUtcNow();
        var transfer = StockTransfer.Create(access.TenantId, source.Id, destination.Id, note, access.UserId, now);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            transfer.Lines.Add(StockTransferLine.Create(
                transfer, i + 1, line.VariantId!.Value, variants[line.VariantId.Value].Sku, line.SourceLocationId, line.DestinationLocationId, line.Quantity!.Value));
        }

        db.StockTransfers.Add(transfer);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.StockTransferCreated, EntityType, transfer.Id, new AuditChanges()
                .Value("sourceWarehouseId", source.Id)
                .Value("destinationWarehouseId", destination.Id)
                .Value("lineCount", transfer.Lines.Count)
                .Value("quantity", transfer.Lines.Sum(l => l.Quantity))
                .ToDictionary()),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(transfer.Id, cancellationToken) is { } detail ? detail : AppError.NotFound();
    }

    /// <summary>
    /// "Gönder": the whole sent quantity leaves the source (only available stock — reserved stock never moves, nothing
    /// goes negative) and sits in transit. 409 <c>inventory.insufficient_available</c> with <c>shortages</c> otherwise.
    /// </summary>
    public Task<AppResult<StockTransferActionResponse>> DispatchAsync(
        Guid id, long? expectedVersion, string? correlationId, CancellationToken cancellationToken) =>
        OnTransferAsync(id, expectedVersion, async transfer =>
        {
            if (StockTransferStateMachine.Next(StockTransferActions.Dispatch, transfer.Status) is not { } next)
            {
                return StockDocumentErrors.TransferTransition(StockTransferActions.Dispatch, transfer.Status);
            }

            if (await InactiveEndAsync(transfer, cancellationToken) is { } inactive)
            {
                return inactive;
            }

            var moved = await inventory.TransferAsync(
                new TransferStock(
                    transfer.Id,
                    [.. transfer.Lines.OrderBy(l => l.LineNo).Select(l => new TransferMove(
                        l.Id, l.VariantId, StockPoint.At(transfer.SourceWarehouseId, l.SourceLocationId), StockPoint.InTransit(transfer.Id), l.Quantity))],
                    $"Transfer {transfer.Number}",
                    correlationId),
                cancellationToken);
            if (moved.Error is { } error)
            {
                return StockDocumentErrors.FromInventory(error);
            }

            var now = clock.GetUtcNow();
            transfer.Status = next;
            transfer.DispatchedAt = now;
            transfer.DispatchedBy = access.UserId;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockTransferDispatched, EntityType, transfer.Id, new AuditChanges()
                    .Value("quantity", transfer.Lines.Sum(l => l.Quantity))
                    .Value("movements", moved.Value!.Movements.Count)
                    .ToDictionary()),
                cancellationToken);
            return await SaveAsync(transfer, moved.Value.Movements, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// "Teslim al": moves what arrived from in transit into the destination shelf. Repeatable until everything sent
    /// arrived (→ received); the rest stays in transit (→ partially_received) until it arrives or the transfer is closed.
    /// More than is still in transit is refused (422 <c>transfer.over_receipt</c>): surplus goods are a goods receipt.
    /// </summary>
    public async Task<AppResult<StockTransferActionResponse>> ReceiveAsync(
        Guid id, long? expectedVersion, ReceiveStockTransferRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var entries = request.Lines ?? [];
        if (entries.Count is 0 or > FulfillmentText.MaxLines)
        {
            errors["lines"] = [$"Between 1 and {FulfillmentText.MaxLines} lines are required."];
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var code = FulfillmentText.Clean(entry?.Code);
            var targets = (entry?.LineId is null ? 0 : 1) + (entry?.VariantId is null ? 0 : 1) + (code is null ? 0 : 1);
            if (entry is null || targets != 1 || code?.Length > FulfillmentText.MaxCodeLength)
            {
                errors[$"lines[{i}]"] = ["Give exactly one of lineId, variantId or code."];
            }
            else if (entry.Quantity is not { } quantity || !FulfillmentText.IsQuantity(quantity))
            {
                errors[$"lines[{i}].quantity"] = ["Must be greater than 0 with at most 3 decimals."];
            }
            else if (entry.LocationId is not null && entry.LocationCode is not null)
            {
                errors[$"lines[{i}].locationId"] = ["Give locationId or locationCode, not both."];
            }
            else if (entry.LocationCode?.Length > FulfillmentText.MaxCodeLength)
            {
                errors[$"lines[{i}].locationCode"] = [$"At most {FulfillmentText.MaxCodeLength} characters."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await OnTransferAsync(id, expectedVersion, async transfer =>
        {
            if (StockTransferStateMachine.Next(StockTransferActions.Receive, transfer.Status, 1m) is null)
            {
                return StockDocumentErrors.TransferTransition(StockTransferActions.Receive, transfer.Status);
            }

            if (await InactiveEndAsync(transfer, cancellationToken) is { } inactive)
            {
                return inactive;
            }

            var destination = await db.Warehouses.AsNoTracking().SingleAsync(w => w.Id == transfer.DestinationWarehouseId, cancellationToken);
            var remaining = transfer.Lines.ToDictionary(l => l.Id, l => l.Outstanding);
            var received = new Dictionary<Guid, decimal>();
            var moves = new List<TransferMove>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                List<StockTransferLine> targets;
                if (entry.LineId is { } lineId)
                {
                    targets = [.. transfer.Lines.Where(l => l.Id == lineId)];
                    if (targets.Count == 0)
                    {
                        return StockDocumentErrors.Validation($"lines[{i}].lineId", "Line not found on this transfer.");
                    }
                }
                else
                {
                    var variantId = entry.VariantId;
                    if (FulfillmentText.Clean(entry.Code) is { } code && (variantId = await catalog.ResolveCodeAsync(code, cancellationToken)) is null)
                    {
                        return new AppError(422, "transfer.unknown_code", "The scanned code matches no product");
                    }

                    targets = [.. transfer.Lines.Where(l => l.VariantId == variantId).OrderBy(l => l.LineNo)];
                    if (targets.Count == 0)
                    {
                        return new AppError(422, "transfer.wrong_item", "The item is not on this transfer");
                    }
                }

                Guid? location = entry.LocationId;
                if (FulfillmentText.Clean(entry.LocationCode) is { } locationCode
                    && (location = await FindLocationByCodeAsync(destination.Id, locationCode, cancellationToken)) is null)
                {
                    return new AppError(422, "transfer.unknown_location", "The scanned shelf is not a location of the destination warehouse");
                }

                var left = entry.Quantity!.Value;
                foreach (var line in targets)
                {
                    var take = Math.Min(left, remaining[line.Id]);
                    if (take <= 0)
                    {
                        continue;
                    }

                    var shelf = destination.UsesLocations ? location ?? line.DestinationLocationId : location;
                    if (destination.UsesLocations && shelf is null)
                    {
                        return StockDocumentErrors.Validation($"lines[{i}].locationId", "A destination location is required in this warehouse.");
                    }

                    moves.Add(new TransferMove(
                        Guid.CreateVersion7(), line.VariantId, StockPoint.InTransit(transfer.Id), StockPoint.At(destination.Id, shelf), take));
                    remaining[line.Id] -= take;
                    received[line.Id] = received.GetValueOrDefault(line.Id) + take;
                    left -= take;
                    if (left == 0)
                    {
                        break;
                    }
                }

                if (left > 0)
                {
                    return new AppError(
                        422,
                        "transfer.over_receipt",
                        "More was received than is still in transit on this transfer; record surplus goods as a goods receipt",
                        new Dictionary<string, string[]> { [$"lines[{i}]"] = [$"Only {entry.Quantity!.Value - left} of {entry.Quantity.Value} can still be received."] });
                }
            }

            var moved = await inventory.TransferAsync(new TransferStock(transfer.Id, moves, $"Transfer {transfer.Number}", correlationId), cancellationToken);
            if (moved.Error is { } error)
            {
                return StockDocumentErrors.FromInventory(error);
            }

            foreach (var line in transfer.Lines.Where(l => received.ContainsKey(l.Id)))
            {
                line.ReceivedQuantity += received[line.Id];
            }

            var outstanding = transfer.Lines.Sum(l => l.Outstanding);
            var next = StockTransferStateMachine.Next(StockTransferActions.Receive, transfer.Status, outstanding)!;
            var now = clock.GetUtcNow();
            transfer.Status = next;
            transfer.LastReceivedAt = now;
            if (next == StockTransferStatuses.Received)
            {
                transfer.CompletedAt = now;
            }

            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockTransferReceived, EntityType, transfer.Id, new AuditChanges()
                    .Value("quantity", received.Values.Sum())
                    .Value("inTransit", outstanding)
                    .Value("status", next)
                    .ToDictionary()),
                cancellationToken);
            return await SaveAsync(transfer, moved.Value!.Movements, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Closes the discrepancy (AF-07 step 5): everything still in transit is written off as lost or returned to the
    /// source shelves. The transfer ends as closed with the received / returned / lost split kept per line.
    /// </summary>
    public async Task<AppResult<StockTransferActionResponse>> CloseAsync(
        Guid id, long? expectedVersion, CloseStockTransferRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Resolution is not { } resolution || !TransferResolutions.All.Contains(resolution))
        {
            errors["resolution"] = [$"Must be one of: {string.Join(", ", TransferResolutions.All)}."];
        }

        var reason = FulfillmentText.Clean(request.Reason);
        if (reason is null || reason.Length > FulfillmentText.MaxReasonLength)
        {
            errors["reason"] = [$"Required, at most {FulfillmentText.MaxReasonLength} characters."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await OnTransferAsync(id, expectedVersion, async transfer =>
        {
            if (StockTransferStateMachine.Next(StockTransferActions.Close, transfer.Status) is not { } next)
            {
                return StockDocumentErrors.TransferTransition(StockTransferActions.Close, transfer.Status);
            }

            var open = transfer.Lines.Where(l => l.Outstanding > 0).OrderBy(l => l.LineNo).ToList();
            var lost = request.Resolution == TransferResolutions.Lost;
            var moved = await inventory.TransferAsync(
                new TransferStock(
                    transfer.Id,
                    [.. open.Select(l => new TransferMove(
                        Guid.CreateVersion7(),
                        l.VariantId,
                        StockPoint.InTransit(transfer.Id),
                        lost ? null : StockPoint.At(transfer.SourceWarehouseId, l.SourceLocationId),
                        l.Outstanding))],
                    $"Transfer {transfer.Number}: {reason}",
                    correlationId),
                cancellationToken);
            if (moved.Error is { } error)
            {
                return StockDocumentErrors.FromInventory(error);
            }

            var quantity = open.Sum(l => l.Outstanding);
            foreach (var line in open)
            {
                if (lost)
                {
                    line.LostQuantity += line.Outstanding;
                }
                else
                {
                    line.ReturnedQuantity += line.Outstanding;
                }
            }

            var now = clock.GetUtcNow();
            transfer.Status = next;
            transfer.CloseResolution = request.Resolution;
            transfer.CloseReason = reason;
            transfer.ClosedBy = access.UserId;
            transfer.CompletedAt = now;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockTransferClosed, EntityType, transfer.Id, new AuditChanges()
                    .Value("resolution", request.Resolution)
                    .Value("quantity", quantity)
                    .Value("reason", reason)
                    .ToDictionary()),
                cancellationToken);
            return await SaveAsync(transfer, moved.Value!.Movements, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Cancels a draft (AF-07: only before dispatch). No stock was moved, none moves.</summary>
    public async Task<AppResult<StockTransferActionResponse>> CancelAsync(
        Guid id, long? expectedVersion, CancelStockTransferRequest request, CancellationToken cancellationToken)
    {
        var reason = FulfillmentText.Clean(request.Reason);
        if (reason is null || reason.Length > FulfillmentText.MaxReasonLength)
        {
            return StockDocumentErrors.Validation("reason", $"Required, at most {FulfillmentText.MaxReasonLength} characters.");
        }

        return await OnTransferAsync(id, expectedVersion, async transfer =>
        {
            if (StockTransferStateMachine.Next(StockTransferActions.Cancel, transfer.Status) is not { } next)
            {
                return StockDocumentErrors.TransferTransition(StockTransferActions.Cancel, transfer.Status);
            }

            transfer.Status = next;
            transfer.CancelledAt = clock.GetUtcNow();
            transfer.CancelledBy = access.UserId;
            transfer.CancelReason = reason;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockTransferCancelled, EntityType, transfer.Id, new AuditChanges().Value("reason", reason).ToDictionary()),
                cancellationToken);
            return await SaveAsync(transfer, [], cancellationToken);
        }, cancellationToken);
    }

    private async Task<AppError?> InactiveEndAsync(StockTransfer transfer, CancellationToken cancellationToken)
    {
        var inactive = await db.Warehouses.AsNoTracking()
            .AnyAsync(
                w => (w.Id == transfer.SourceWarehouseId || w.Id == transfer.DestinationWarehouseId) && w.Status != WarehouseStatuses.Active,
                cancellationToken);
        return inactive ? new AppError(409, "transfer.warehouse_inactive", "The source or destination warehouse is inactive") : null;
    }

    private async Task<Guid?> FindLocationByCodeAsync(Guid warehouseId, string code, CancellationToken cancellationToken)
    {
        var normalized = code.ToUpperInvariant();
        return await db.Locations.AsNoTracking()
            .Where(l => l.WarehouseId == warehouseId && (l.Barcode == code || l.Code == normalized))
            .OrderBy(l => l.Barcode == code ? 0 : 1)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<AppResult<StockTransferActionResponse>> SaveAsync(
        StockTransfer transfer, IReadOnlyList<MovementResult> movements, CancellationToken cancellationToken)
    {
        transfer.Touch(access.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(transfer.Id, cancellationToken) is { } detail
            ? new StockTransferActionResponse(detail, [.. movements.Select(m => m.Id)])
            : AppError.NotFound();
    }

    /// <summary>Locks the transfer row (404 when it is not this tenant's), checks If-Match, then runs the change.</summary>
    private async Task<AppResult<T>> OnTransferAsync<T>(
        Guid id, long? expectedVersion, Func<StockTransfer, Task<AppResult<T>>> change, CancellationToken cancellationToken)
    {
        try
        {
            if (!await locks.LockTransferAsync(id, cancellationToken))
            {
                return AppError.NotFound();
            }

            var transfer = await db.StockTransfers.Include(t => t.Lines).SingleAsync(t => t.Id == id, cancellationToken);
            if (expectedVersion is { } version && version != transfer.RowVersion)
            {
                return AppError.VersionMismatch();
            }

            return await change(transfer);
        }
        catch (Exception ex) when (StockDocumentErrors.IsLockConflict(ex))
        {
            return StockDocumentErrors.TransferBusy();
        }
    }

    private static WarehouseModel? CheckWarehouse(Dictionary<string, string[]> errors, string field, WarehouseModel? warehouse)
    {
        if (warehouse is null)
        {
            errors[field] = ["Warehouse not found."];
            return null;
        }

        if (warehouse.Status != WarehouseStatuses.Active)
        {
            errors[field] = ["Warehouse is inactive."];
            return null;
        }

        return warehouse;
    }

    private static void CheckLocation(
        Dictionary<string, string[]> errors,
        string field,
        WarehouseModel warehouse,
        Guid? locationId,
        Dictionary<Guid, WarehouseLocation> locations,
        bool required)
    {
        if (!warehouse.UsesLocations)
        {
            if (locationId is not null)
            {
                errors[field] = ["This warehouse does not use locations."];
            }

            return;
        }

        if (locationId is not { } id)
        {
            if (required)
            {
                errors[field] = ["A location is required in this warehouse."];
            }
        }
        else if (!locations.TryGetValue(id, out var location) || location.WarehouseId != warehouse.Id)
        {
            errors[field] = ["Location not found in this warehouse."];
        }
        else if (!location.IsActive)
        {
            errors[field] = ["Location is inactive."];
        }
    }
}

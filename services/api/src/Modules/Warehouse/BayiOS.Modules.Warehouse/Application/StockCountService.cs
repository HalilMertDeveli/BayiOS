using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Warehouse.Application;

/// <summary>
/// Stock counts (PRD INV-06, AF-06, US-INV-005; inventory-and-orders §4.8). Creating a count snapshots the system
/// on_hand of the scope ("anlık görüntü"); counters enter quantities by line, or by scanning product and shelf codes
/// (handheld), while stock keeps moving; a manager reviews the difference report, may send lines back for a recount,
/// and applies: the stock engine writes one <c>count_correction</c> per difference against the system balance at the
/// moment each line was counted, so a shipment during the count is neither lost nor counted twice. Timestamps are
/// the database's <c>now()</c> — the clock of the ledger's <c>occurred_at</c>.
/// </summary>
internal sealed class StockCountService(
    WarehouseDbContext db,
    StockCountQueries queries,
    FulfillmentLocks locks,
    FulfillmentAccess access,
    UnitOfWork unitOfWork,
    IInventoryService inventory,
    IStockQueries stock,
    IFulfillmentCatalog catalog,
    IAuditLog audit,
    TimeProvider clock)
{
    public const string EntityType = "stock_count";
    public const int MaxScopeItems = 500;

    public async Task<AppResult<StockCountResponse>> CreateAsync(CreateStockCountRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var scope = request.Scope ?? StockCountScopes.Full;
        var note = FulfillmentText.Clean(request.Note);
        var locationIds = (request.LocationIds ?? []).Distinct().ToList();
        var variantIds = (request.VariantIds ?? []).Distinct().ToList();
        if (request.WarehouseId is null)
        {
            errors["warehouseId"] = ["Required."];
        }

        if (!StockCountScopes.All.Contains(scope))
        {
            errors["scope"] = [$"Must be one of: {string.Join(", ", StockCountScopes.All)}."];
        }
        else
        {
            CheckScopeList(errors, "locationIds", locationIds.Count, scope == StockCountScopes.Locations);
            CheckScopeList(errors, "variantIds", variantIds.Count, scope == StockCountScopes.Variants);
        }

        if (note?.Length > FulfillmentText.MaxReasonLength)
        {
            errors["note"] = [$"At most {FulfillmentText.MaxReasonLength} characters."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        await db.JoinTransactionAsync(cancellationToken);
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.Status != WarehouseStatuses.Active)
        {
            return StockDocumentErrors.Validation("warehouseId", warehouse is null ? "Warehouse not found." : "Warehouse is inactive.");
        }

        if (locationIds.Count > 0)
        {
            if (!warehouse.UsesLocations)
            {
                return StockDocumentErrors.Validation("locationIds", "This warehouse does not use locations.");
            }

            var known = await db.Locations.AsNoTracking().CountAsync(l => l.WarehouseId == warehouse.Id && locationIds.Contains(l.Id), cancellationToken);
            if (known != locationIds.Count)
            {
                return StockDocumentErrors.Validation("locationIds", "Every location must belong to this warehouse.");
            }
        }

        if (variantIds.Count > 0 && (await catalog.FindVariantsAsync(variantIds, cancellationToken)).Count != variantIds.Count)
        {
            return StockDocumentErrors.Validation("variantIds", "Variant not found.");
        }

        if (await db.StockCounts.AnyAsync(c => c.WarehouseId == warehouse.Id && StockCountStatuses.Active.Contains(c.Status), cancellationToken))
        {
            return ActiveCountExists();
        }

        var snapshotAt = await DatabaseNowAsync(cancellationToken);
        var levels = await stock.LevelsAsync(
            warehouse.Id,
            scope == StockCountScopes.Locations ? locationIds : null,
            scope == StockCountScopes.Variants ? variantIds : null,
            InventoryLimits.MaxCountLines + 1,
            cancellationToken);
        var rows = levels.Where(l => l.OnHand != 0 || l.Reserved != 0).Select(l => (l.VariantId, l.LocationId, l.OnHand)).ToList();
        if (scope == StockCountScopes.Variants && !warehouse.UsesLocations)
        {
            // A scoped variant without any stock row is still counted (expected 0) in a location-less warehouse.
            rows.AddRange(variantIds.Where(v => rows.All(r => r.VariantId != v)).Select(v => (v, (Guid?)null, 0m)));
        }

        if (rows.Count > InventoryLimits.MaxCountLines)
        {
            return new AppError(422, "count.scope_too_large", $"The scope has more than {InventoryLimits.MaxCountLines} stock rows; count by shelves or products");
        }

        var skus = await catalog.FindVariantsAsync([.. rows.Select(r => r.VariantId).Distinct()], cancellationToken);
        var count = StockCount.Create(
            access.TenantId, warehouse.Id, scope, locationIds, variantIds, request.Blind ?? false, note, access.UserId, snapshotAt);
        foreach (var (variantId, locationId, onHand) in rows)
        {
            count.Lines.Add(StockCountLine.Create(count, variantId, skus.GetValueOrDefault(variantId)?.Sku ?? "?", locationId, onHand, snapshotAt));
        }

        db.StockCounts.Add(count);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.StockCountCreated, EntityType, count.Id, new AuditChanges()
                .Value("warehouseId", warehouse.Id)
                .Value("scope", scope)
                .Value("blind", count.IsBlind)
                .Value("lineCount", count.Lines.Count)
                .ToDictionary()),
            cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: WarehouseDbContext.ActiveCountUniqueIndex })
        {
            return ActiveCountExists();
        }

        return await queries.DetailAsync(count.Id, cancellationToken) is { } detail ? detail : AppError.NotFound();
    }

    /// <summary>
    /// Counted quantities (counters: <c>inventory.count.perform</c>). Entries resolve product and shelf codes; an item
    /// found where the snapshot had none becomes a new line with that key's current on_hand as its expectation.
    /// </summary>
    public async Task<AppResult<StockCountResponse>> EnterAsync(
        Guid id, long? expectedVersion, StockCountEntriesRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var entries = request.Entries ?? [];
        if (entries.Count is 0 or > FulfillmentText.MaxLines)
        {
            errors["entries"] = [$"Between 1 and {FulfillmentText.MaxLines} entries are required."];
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var code = FulfillmentText.Clean(entry?.Code);
            var mode = entry?.Mode ?? "set";
            if (entry is null || (entry.LineId is null && entry.VariantId is null && code is null)
                || (entry.LineId is not null && (entry.VariantId is not null || code is not null || entry.LocationId is not null || entry.LocationCode is not null))
                || (entry.VariantId is not null && code is not null))
            {
                errors[$"entries[{i}]"] = ["Give lineId, or one of variantId / code with an optional locationId / locationCode."];
            }
            else if (mode is not ("set" or "add"))
            {
                errors[$"entries[{i}].mode"] = ["Must be set or add."];
            }
            else if (entry.Quantity is not { } quantity || quantity > FulfillmentText.MaxQuantity || decimal.Round(quantity, 3) != quantity
                || (mode == "set" ? quantity < 0 : quantity <= 0))
            {
                errors[$"entries[{i}].quantity"] = [mode == "set" ? "Must be ≥ 0 with at most 3 decimals." : "Must be greater than 0 with at most 3 decimals."];
            }
            else if ((entry.LocationId is not null && entry.LocationCode is not null)
                || code?.Length > FulfillmentText.MaxCodeLength || entry.LocationCode?.Length > FulfillmentText.MaxCodeLength)
            {
                errors[$"entries[{i}].locationId"] = ["Give locationId or locationCode (codes at most 128 characters)."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        return await OnCountAsync(id, expectedVersion, async count =>
        {
            if (StockCountStateMachine.Next(StockCountActions.Count, count.Status) is null)
            {
                return StockDocumentErrors.CountTransition(StockCountActions.Count, count.Status);
            }

            var warehouse = await db.Warehouses.AsNoTracking().SingleAsync(w => w.Id == count.WarehouseId, cancellationToken);
            var now = await DatabaseNowAsync(cancellationToken);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                StockCountLine? line;
                if (entry.LineId is { } lineId)
                {
                    line = count.Lines.SingleOrDefault(l => l.Id == lineId);
                    if (line is null)
                    {
                        return StockDocumentErrors.Validation($"entries[{i}].lineId", "Line not found on this count.");
                    }
                }
                else
                {
                    var variantId = entry.VariantId;
                    if (FulfillmentText.Clean(entry.Code) is { } code && (variantId = await catalog.ResolveCodeAsync(code, cancellationToken)) is null)
                    {
                        return new AppError(422, "count.unknown_code", "The scanned code matches no product");
                    }

                    var location = entry.LocationId;
                    if (FulfillmentText.Clean(entry.LocationCode) is { } locationCode
                        && (location = await FindLocationByCodeAsync(warehouse.Id, locationCode, cancellationToken)) is null)
                    {
                        return new AppError(422, "count.unknown_location", "The scanned shelf is not a location of this warehouse");
                    }

                    line = count.Lines.SingleOrDefault(l => l.VariantId == variantId && l.LocationId == location);
                    if (line is null)
                    {
                        var added = await AddLineAsync(count, warehouse, variantId!.Value, location, now, $"entries[{i}]", cancellationToken);
                        if (added.Error is { } addError)
                        {
                            return addError;
                        }

                        line = added.Value!;
                    }
                }

                if (await FractionErrorAsync(line.VariantId, entry.Quantity!.Value, $"entries[{i}].quantity", cancellationToken) is { } fraction)
                {
                    return fraction;
                }

                line.CountedQuantity = entry.Mode == "add" ? (line.CountedQuantity ?? 0m) + entry.Quantity!.Value : entry.Quantity!.Value;
                if (line.CountedQuantity > FulfillmentText.MaxQuantity)
                {
                    return StockDocumentErrors.Validation($"entries[{i}].quantity", "The counted quantity is too large.");
                }

                line.CountedAt = now;
                line.CountedBy = access.UserId;
                line.LineStatus = StockCountLineStatuses.Counted;
            }

            return await SaveAsync(count, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Counting is finished; the difference report is ready for review (counters may submit).</summary>
    public Task<AppResult<StockCountResponse>> SubmitAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken) =>
        OnCountAsync<StockCountResponse>(id, expectedVersion, async count =>
        {
            if (StockCountStateMachine.Next(StockCountActions.Submit, count.Status) is not { } next)
            {
                return StockDocumentErrors.CountTransition(StockCountActions.Submit, count.Status);
            }

            if (count.Lines.All(l => l.CountedQuantity is null))
            {
                return NothingCounted();
            }

            count.Status = next;
            count.SubmittedAt = clock.GetUtcNow();
            count.SubmittedBy = access.UserId;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockCountSubmitted, EntityType, count.Id, new AuditChanges()
                    .Value("countedLines", count.Lines.Count(l => l.CountedQuantity is not null))
                    .ToDictionary()),
                cancellationToken);
            return await SaveAsync(count, cancellationToken);
        }, cancellationToken);

    /// <summary>"Yeniden sayım": clears the chosen lines' counts and reopens the count.</summary>
    public async Task<AppResult<StockCountResponse>> RecountAsync(
        Guid id, long? expectedVersion, RecountStockCountRequest request, CancellationToken cancellationToken)
    {
        var lineIds = (request.LineIds ?? []).Distinct().ToList();
        if (lineIds.Count is 0 or > InventoryLimits.MaxCountLines)
        {
            return StockDocumentErrors.Validation("lineIds", $"Between 1 and {InventoryLimits.MaxCountLines} line ids are required.");
        }

        return await OnCountAsync(id, expectedVersion, async count =>
        {
            if (StockCountStateMachine.Next(StockCountActions.Recount, count.Status) is not { } next)
            {
                return StockDocumentErrors.CountTransition(StockCountActions.Recount, count.Status);
            }

            var lines = count.Lines.Where(l => lineIds.Contains(l.Id)).ToList();
            if (lines.Count != lineIds.Count)
            {
                return StockDocumentErrors.Validation("lineIds", "Every line must belong to this count.");
            }

            foreach (var line in lines)
            {
                line.CountedQuantity = null;
                line.CountedAt = null;
                line.CountedBy = null;
                line.LineStatus = StockCountLineStatuses.Pending;
            }

            count.Status = next;
            count.SubmittedAt = null;
            count.SubmittedBy = null;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockCountRecountRequested, EntityType, count.Id, new AuditChanges().Value("lineCount", lines.Count).ToDictionary()),
                cancellationToken);
            return await SaveAsync(count, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// "Onayla": count_correction movements for every difference of the counted lines (uncounted lines change nothing
    /// and are closed as uncounted). Lines whose stock moved after their snapshot need <c>acceptMovedLines</c>.
    /// </summary>
    public Task<AppResult<ApplyStockCountResponse>> ApplyAsync(
        Guid id, long? expectedVersion, ApplyStockCountRequest request, string? correlationId, CancellationToken cancellationToken) =>
        OnCountAsync<ApplyStockCountResponse>(id, expectedVersion, async count =>
        {
            if (StockCountStateMachine.Next(StockCountActions.Apply, count.Status) is not { } next)
            {
                return StockDocumentErrors.CountTransition(StockCountActions.Apply, count.Status);
            }

            var counted = count.Lines.Where(l => l.CountedQuantity is not null).ToList();
            if (counted.Count == 0)
            {
                return NothingCounted();
            }

            if (request.AcceptMovedLines != true)
            {
                var moved = await stock.MovedSinceAsync(
                    count.WarehouseId, [.. counted.Select(l => (l.VariantId, l.LocationId, l.SnapshotAt))], cancellationToken);
                if (moved.Count > 0)
                {
                    return new AppError(
                        409,
                        "count.lines_moved",
                        "Stock of some counted lines moved after the snapshot; review them and repeat with acceptMovedLines=true",
                        counted.Where(l => moved.Contains((l.VariantId, l.LocationId)))
                            .ToDictionary(l => l.Id.ToString(), l => new[] { $"{l.Sku}: stock moved after the snapshot." }));
                }
            }

            var applied = await inventory.ApplyCountAsync(
                new ApplyCount(
                    count.Id,
                    count.WarehouseId,
                    [.. counted.Select(l => new CountLine(l.Id, l.VariantId, l.LocationId, l.CountedQuantity!.Value, l.CountedAt!.Value))],
                    correlationId),
                cancellationToken);
            if (applied.Error is { } error)
            {
                return StockDocumentErrors.FromInventory(error);
            }

            var deltas = applied.Value!.Lines.ToDictionary(l => l.CountLineId, l => l.Delta);
            foreach (var line in count.Lines)
            {
                if (deltas.TryGetValue(line.Id, out var delta))
                {
                    line.AppliedDelta = delta;
                    line.LineStatus = StockCountLineStatuses.Applied;
                }
                else
                {
                    line.LineStatus = StockCountLineStatuses.Uncounted;
                }
            }

            count.Status = next;
            count.AppliedAt = clock.GetUtcNow();
            count.AppliedBy = access.UserId;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockCountApplied, EntityType, count.Id, new AuditChanges()
                    .Value("countedLines", counted.Count)
                    .Value("uncountedLines", count.Lines.Count - counted.Count)
                    .Value("movements", applied.Value.Movements.Count)
                    .ToDictionary()),
                cancellationToken);
            var detail = await SaveAsync(count, cancellationToken);
            return detail.Error is { } detailError
                ? detailError
                : new ApplyStockCountResponse(detail.Value!, [.. applied.Value.Movements.Select(m => m.Id)]);
        }, cancellationToken);

    public async Task<AppResult<StockCountResponse>> CancelAsync(
        Guid id, long? expectedVersion, CancelStockCountRequest request, CancellationToken cancellationToken)
    {
        var reason = FulfillmentText.Clean(request.Reason);
        if (reason is null || reason.Length > FulfillmentText.MaxReasonLength)
        {
            return StockDocumentErrors.Validation("reason", $"Required, at most {FulfillmentText.MaxReasonLength} characters.");
        }

        return await OnCountAsync(id, expectedVersion, async count =>
        {
            if (StockCountStateMachine.Next(StockCountActions.Cancel, count.Status) is not { } next)
            {
                return StockDocumentErrors.CountTransition(StockCountActions.Cancel, count.Status);
            }

            count.Status = next;
            count.CancelledAt = clock.GetUtcNow();
            count.CancelledBy = access.UserId;
            count.CancelReason = reason;
            await audit.RecordAsync(
                new AuditEvent(AuditActions.StockCountCancelled, EntityType, count.Id, new AuditChanges().Value("reason", reason).ToDictionary()),
                cancellationToken);
            return await SaveAsync(count, cancellationToken);
        }, cancellationToken);
    }

    private async Task<AppResult<StockCountLine>> AddLineAsync(
        StockCount count, Domain.Warehouse warehouse, Guid variantId, Guid? locationId, DateTimeOffset now, string field, CancellationToken cancellationToken)
    {
        if (warehouse.UsesLocations != locationId is not null)
        {
            return StockDocumentErrors.Validation(field + ".locationId", warehouse.UsesLocations
                ? "A location is required in this warehouse."
                : "This warehouse does not use locations.");
        }

        if (locationId is { } id && !await db.Locations.AsNoTracking().AnyAsync(l => l.Id == id && l.WarehouseId == warehouse.Id, cancellationToken))
        {
            return StockDocumentErrors.Validation(field + ".locationId", "Location not found in this warehouse.");
        }

        if (!count.Covers(variantId, locationId))
        {
            return new AppError(422, "count.out_of_scope", "The item or shelf is outside this count's scope");
        }

        if (count.Lines.Count >= InventoryLimits.MaxCountLines)
        {
            return new AppError(422, "count.scope_too_large", $"A count has at most {InventoryLimits.MaxCountLines} lines");
        }

        var variants = await catalog.FindVariantsAsync([variantId], cancellationToken);
        if (!variants.TryGetValue(variantId, out var variant))
        {
            return StockDocumentErrors.Validation(field + ".variantId", "Variant not found.");
        }

        var level = (await stock.LevelsAsync(warehouse.Id, locationId is { } l ? [l] : null, [variantId], 2, cancellationToken))
            .FirstOrDefault(x => x.LocationId == locationId);
        var line = StockCountLine.Create(count, variantId, variant.Sku, locationId, level?.OnHand ?? 0m, now);
        count.Lines.Add(line);
        db.StockCountLines.Add(line);
        return line;
    }

    private async Task<AppError?> FractionErrorAsync(Guid variantId, decimal quantity, string field, CancellationToken cancellationToken)
    {
        if (quantity == decimal.Truncate(quantity))
        {
            return null;
        }

        var variants = await catalog.FindVariantsAsync([variantId], cancellationToken);
        return variants.TryGetValue(variantId, out var variant) && !variant.AllowsFraction
            ? StockDocumentErrors.Validation(field, "The variant's unit does not allow fractions.")
            : null;
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

    /// <summary>The transaction's <c>now()</c>: the same clock as <c>inventory_movements.occurred_at</c>.</summary>
    private async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT now()", unitOfWork.Connection, unitOfWork.Transaction);
        var value = (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private async Task<AppResult<StockCountResponse>> SaveAsync(StockCount count, CancellationToken cancellationToken)
    {
        count.Touch(access.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(count.Id, cancellationToken) is { } detail ? detail : AppError.NotFound();
    }

    private async Task<AppResult<T>> OnCountAsync<T>(
        Guid id, long? expectedVersion, Func<StockCount, Task<AppResult<T>>> change, CancellationToken cancellationToken)
    {
        try
        {
            if (!await locks.LockCountAsync(id, cancellationToken))
            {
                return AppError.NotFound();
            }

            var count = await db.StockCounts.Include(c => c.Lines).SingleAsync(c => c.Id == id, cancellationToken);
            if (expectedVersion is { } version && version != count.RowVersion)
            {
                return AppError.VersionMismatch();
            }

            return await change(count);
        }
        catch (Exception ex) when (StockDocumentErrors.IsLockConflict(ex))
        {
            return StockDocumentErrors.CountBusy();
        }
    }

    private static void CheckScopeList(Dictionary<string, string[]> errors, string field, int count, bool required)
    {
        if (required && count is 0 or > MaxScopeItems)
        {
            errors[field] = [$"Between 1 and {MaxScopeItems} ids are required for this scope."];
        }
        else if (!required && count > 0)
        {
            errors[field] = ["Not allowed for this scope."];
        }
    }

    private static AppError ActiveCountExists() =>
        new(409, "count.already_active", "This warehouse already has an open stock count; finish or cancel it first");

    private static AppError NothingCounted() => new(409, "count.nothing_counted", "No line has been counted yet");
}

/// <summary>Limits shared with the stock engine.</summary>
internal static class InventoryLimits
{
    /// <summary>Lines of one count (the engine applies at most this many in one transaction).</summary>
    public const int MaxCountLines = 5000;
}

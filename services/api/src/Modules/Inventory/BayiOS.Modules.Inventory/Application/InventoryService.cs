using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using Npgsql;

namespace BayiOS.Modules.Inventory.Application;

/// <summary>
/// The stock engine (ADR 0004, inventory-and-orders §2–§4). Each command:
/// <list type="number">
/// <item>runs under a savepoint of the request transaction with <c>lock_timeout</c> = 5 s,</item>
/// <item>validates references through the owning modules' ports (same transaction),</item>
/// <item>locks the affected balance rows in the fixed order, checks everything, then writes balances, ledger
/// movements and reservations,</item>
/// <item>on any error rolls back to the savepoint, so a failed command leaves no partial change behind.</item>
/// </list>
/// </summary>
internal sealed class InventoryService(
    StockStore store,
    IWarehouseDirectory warehouses,
    IVariantDirectory variants,
    INegativeStockPolicyProvider policies) : IInventoryService
{
    public const int MaxLines = 500;
    public const int MaxReasonLength = 500;

    /// <summary>numeric(18,3): 15 integer digits.</summary>
    public const decimal MaxQuantity = 999_999_999_999_999m;

    private const string Savepoint = "inventory_operation";

    public Task<InventoryResult<ReceiptResult>> ReceiveAsync(ReceiveStock command, CancellationToken cancellationToken) =>
        RunAsync(() => ReceiveCoreAsync(command, cancellationToken), cancellationToken);

    public Task<InventoryResult<MovementResult>> AdjustAsync(AdjustStock command, CancellationToken cancellationToken) =>
        RunAsync(() => AdjustCoreAsync(command, cancellationToken), cancellationToken);

    public Task<InventoryResult<ReservationResult>> ReserveAsync(ReserveStock command, CancellationToken cancellationToken) =>
        RunAsync(() => ReserveCoreAsync(command, cancellationToken), cancellationToken);

    public Task<InventoryResult<ReservationResult>> ReleaseAsync(ReleaseReservations command, CancellationToken cancellationToken) =>
        RunAsync(() => ReleaseCoreAsync(command, cancellationToken), cancellationToken);

    public Task<InventoryResult<IssueResult>> IssueAsync(IssueStock command, CancellationToken cancellationToken) =>
        RunAsync(() => IssueCoreAsync(command, cancellationToken), cancellationToken);

    // ---- §4.1 Goods receipt ----

    private async Task<InventoryResult<ReceiptResult>> ReceiveCoreAsync(ReceiveStock command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateLineCount(errors, command.Lines?.Count ?? 0);
        var reference = Clean(command.Reference);
        if (reference is { Length: > 200 })
        {
            errors["reference"] = ["Must be at most 200 characters."];
        }

        var lines = command.Lines ?? [];
        for (var i = 0; i < lines.Count; i++)
        {
            ValidateQuantity(errors, $"lines[{i}].quantity", lines[i].Quantity);
            if (lines[i].UnitCost is { } cost && (cost < 0 || cost > MaxQuantity || decimal.Round(cost, 4) != cost))
            {
                errors[$"lines[{i}].unitCost"] = ["Must be ≥ 0 with at most 4 decimals."];
            }
        }

        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var warehouse = command.WarehouseId is { } warehouseId
            ? await warehouses.FindWarehouseAsync(warehouseId, cancellationToken)
            : await warehouses.GetOrCreateDefaultAsync(cancellationToken);
        if (ValidateWarehouse(warehouse) is { } warehouseError)
        {
            return warehouseError;
        }

        await ValidateLocationsAsync(errors, warehouse!, [.. lines.Select((l, i) => ($"lines[{i}].locationId", l.LocationId))], cancellationToken);
        await ValidateVariantsAsync(
            errors, [.. lines.Select((l, i) => ($"lines[{i}]", l.VariantId, l.Quantity))], requireActive: true, cancellationToken);
        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var allowNegative = await AllowsNegativeAsync(cancellationToken);
        var keyed = lines.Select(l => (Line: l, Key: new BalanceKey(l.VariantId, warehouse!.Id, l.LocationId))).ToList();
        var rows = Index(await store.LockBalancesAsync([.. keyed.Select(k => k.Key)], allowNegative, createMissing: true, cancellationToken));

        var receiptId = command.ReceiptId ?? Guid.CreateVersion7();
        var movements = new List<MovementResult>(keyed.Count);
        foreach (var (line, key) in keyed)
        {
            var row = rows[key];
            var after = await store.UpdateBalanceAsync(
                row, line.Quantity, 0m, NextAllowNegative(row, allowNegative, row.OnHand + line.Quantity, row.Reserved, worsening: false), cancellationToken);
            movements.Add(await store.InsertMovementAsync(
                new NewMovement(key, MovementTypes.Receipt, line.Quantity, line.UnitCost, MovementSourceTypes.Receipt,
                    receiptId, line.LineId ?? Guid.CreateVersion7(), reference, command.CorrelationId),
                after,
                cancellationToken));
        }

        return new ReceiptResult(receiptId, warehouse!.Id, movements);
    }

    // ---- §4.2 Adjustment ----

    private async Task<InventoryResult<MovementResult>> AdjustCoreAsync(AdjustStock command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!MovementTypes.Adjustments.Contains(command.Type))
        {
            errors["type"] = [$"Must be one of: {string.Join(", ", MovementTypes.Adjustments)}."];
        }

        ValidateQuantity(errors, "quantity", command.Quantity);
        var reason = Clean(command.Reason);
        if (reason is null || reason.Length > MaxReasonLength)
        {
            errors["reason"] = [$"A reason of 1–{MaxReasonLength} characters is required."];
        }

        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var warehouse = await warehouses.FindWarehouseAsync(command.WarehouseId, cancellationToken);
        if (ValidateWarehouse(warehouse) is { } warehouseError)
        {
            return warehouseError;
        }

        await ValidateLocationsAsync(errors, warehouse!, [("locationId", command.LocationId)], cancellationToken);
        var found = await ValidateVariantsAsync(errors, [(string.Empty, command.VariantId, command.Quantity)], requireActive: false, cancellationToken);
        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var policy = await policies.GetPolicyAsync(cancellationToken);
        var allowNegative = policy != NegativeStockPolicies.Block;
        var key = new BalanceKey(command.VariantId, warehouse!.Id, command.LocationId);
        var row = (await store.LockBalancesAsync([key], allowNegative, createMissing: true, cancellationToken)).Single();

        var outbound = MovementTypes.Outbound.Contains(command.Type);
        var delta = outbound ? -command.Quantity : command.Quantity;
        if (outbound && row.Available < command.Quantity)
        {
            // Reserved stock cannot be adjusted away (§4.2): the check is against available, not on_hand.
            var shortage = new StockShortage(key.VariantId, found[key.VariantId].Sku, key.WarehouseId, key.LocationId, command.Quantity, row.Available);
            var blocked = new InventoryError(409, "inventory.insufficient_available", "Available stock is insufficient for this adjustment", Shortages: [shortage]);
            if (NegativeStockDecision(policy, command.ConfirmNegativeStock, [shortage], blocked) is { } decision)
            {
                return decision;
            }
        }

        var after = await store.UpdateBalanceAsync(
            row, delta, 0m, NextAllowNegative(row, allowNegative, row.OnHand + delta, row.Reserved, worsening: outbound), cancellationToken);
        return await store.InsertMovementAsync(
            new NewMovement(key, command.Type, delta, null, MovementSourceTypes.Adjustment, Guid.CreateVersion7(), null, reason, command.CorrelationId),
            after,
            cancellationToken);
    }

    // ---- §4.3 Reservation on order approval (all or nothing) ----

    private async Task<InventoryResult<ReservationResult>> ReserveCoreAsync(ReserveStock command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var lines = command.Lines ?? [];
        ValidateLineCount(errors, lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            ValidateQuantity(errors, $"lines[{i}].quantity", lines[i].Quantity);
        }

        if (lines.Select(l => l.OrderLineId).Distinct().Count() != lines.Count)
        {
            errors["lines"] = ["Each order line may appear only once."];
        }

        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var warehouse = await warehouses.FindWarehouseAsync(command.WarehouseId, cancellationToken);
        if (ValidateWarehouse(warehouse) is { } warehouseError)
        {
            return warehouseError;
        }

        var found = await ValidateVariantsAsync(
            errors, [.. lines.Select((l, i) => ($"lines[{i}]", l.VariantId, l.Quantity))], requireActive: false, cancellationToken);
        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var lineIds = lines.Select(l => l.OrderLineId).ToList();
        if ((await store.FindActiveReservationsAsync(null, lineIds, cancellationToken)).Count > 0)
        {
            return AlreadyReserved();
        }

        var policy = await policies.GetPolicyAsync(cancellationToken);
        var allowNegative = policy != NegativeStockPolicies.Block;
        var variantIds = lines.Select(l => l.VariantId).Distinct().ToList();

        // Location-less warehouses keep one (variant, warehouse, NULL) row per variant: make sure it exists.
        if (!warehouse!.UsesLocations)
        {
            await store.LockBalancesAsync(
                [.. variantIds.Select(v => new BalanceKey(v, warehouse.Id, null))], allowNegative, createMissing: true, cancellationToken);
        }

        var locked = await store.LockVariantBalancesAsync(warehouse.Id, variantIds, cancellationToken);
        var locationIds = locked.Where(r => r.Key.LocationId is not null).Select(r => r.Key.LocationId!.Value).Distinct().ToList();
        var locations = locationIds.Count == 0 ? new Dictionary<Guid, LocationInfo>() : await warehouses.FindLocationsAsync(locationIds, cancellationToken);

        // Rows stock may be allocated from, in allocation order (pick_sequence, then location).
        var candidates = locked
            .Where(r => r.Key.LocationId is not { } loc || (locations.TryGetValue(loc, out var info) && info.IsActive))
            .GroupBy(r => r.Key.VariantId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Key.LocationId is { } loc ? locations[loc].PickSequence : int.MinValue)
                    .ThenBy(r => r.Key.LocationId?.ToString() ?? string.Empty, StringComparer.Ordinal)
                    .ToList());

        // Check every variant before writing anything (I6).
        var shortages = new List<StockShortage>();
        foreach (var variantId in variantIds)
        {
            var need = lines.Where(l => l.VariantId == variantId).Sum(l => l.Quantity);
            var rows = candidates.GetValueOrDefault(variantId) ?? [];
            var available = rows.Sum(r => r.Available);
            if (available < need)
            {
                shortages.Add(new StockShortage(variantId, found[variantId].Sku, warehouse.Id, null, need, available));
            }
        }

        var insufficient = new InventoryError(409, "inventory.insufficient_stock", "Available stock is insufficient for one or more lines", Shortages: shortages);
        if (NegativeStockDecision(policy, command.ConfirmNegativeStock, shortages, insufficient) is { } decision)
        {
            return decision;
        }

        // Allocate deterministically; with negative stock allowed the remainder goes to the last candidate row.
        var allocations = new List<(ReserveLine Line, BalanceRow Row, decimal Quantity)>();
        var taken = new Dictionary<Guid, decimal>();
        foreach (var line in lines.Select((l, i) => (Line: l, Index: i)).OrderBy(x => x.Line.VariantId).ThenBy(x => x.Index).Select(x => x.Line))
        {
            var rows = candidates.GetValueOrDefault(line.VariantId) ?? [];
            if (rows.Count == 0)
            {
                return insufficient with { Shortages = [new StockShortage(line.VariantId, found[line.VariantId].Sku, warehouse.Id, null, line.Quantity, 0m)] };
            }

            var remaining = line.Quantity;
            foreach (var row in rows)
            {
                var free = row.Available - taken.GetValueOrDefault(row.Id);
                var take = Math.Min(remaining, Math.Max(free, 0m));
                if (take > 0)
                {
                    allocations.Add((line, row, take));
                    taken[row.Id] = taken.GetValueOrDefault(row.Id) + take;
                    remaining -= take;
                }

                if (remaining == 0)
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                var last = rows[^1];
                allocations.Add((line, last, remaining));
                taken[last.Id] = taken.GetValueOrDefault(last.Id) + remaining;
            }
        }

        foreach (var row in locked.Where(r => taken.ContainsKey(r.Id)))
        {
            var reservedAfter = row.Reserved + taken[row.Id];
            await store.UpdateBalanceAsync(
                row, 0m, taken[row.Id], NextAllowNegative(row, allowNegative, row.OnHand, reservedAfter, worsening: true), cancellationToken);
        }

        var result = new List<ReservationAllocation>(allocations.Count);
        foreach (var group in allocations.GroupBy(a => (a.Line.OrderLineId, a.Row.Id)))
        {
            var (line, row, _) = group.First();
            var quantity = group.Sum(a => a.Quantity);
            var id = Guid.CreateVersion7();
            await store.InsertReservationAsync(id, command.OrderId, line.OrderLineId, row.Key, quantity, command.CorrelationId, cancellationToken);
            result.Add(new ReservationAllocation(id, line.OrderLineId, line.VariantId, row.Key.WarehouseId, row.Key.LocationId, quantity, "active"));
        }

        return new ReservationResult(command.OrderId, result);
    }

    // ---- §4.4 Release (cancel / short-close) ----

    private async Task<InventoryResult<ReservationResult>> ReleaseCoreAsync(ReleaseReservations command, CancellationToken cancellationToken)
    {
        var candidates = await store.FindActiveReservationsAsync(command.OrderId, command.OrderLineIds, cancellationToken);
        if (candidates.Count == 0)
        {
            return new ReservationResult(command.OrderId, []);
        }

        var allowNegative = await AllowsNegativeAsync(cancellationToken);
        var rows = Index(await store.LockBalancesAsync([.. candidates.Select(r => r.Key)], allowNegative, createMissing: false, cancellationToken));
        var reservations = await store.LockActiveReservationsAsync(command.OrderId, command.OrderLineIds, cancellationToken);

        var released = new List<ReservationAllocation>();
        foreach (var reservation in reservations)
        {
            var open = reservation.Open;
            if (!rows.TryGetValue(reservation.Key, out var row))
            {
                // A reservation appeared between the scan and the lock (concurrent change): the caller retries.
                return InventoryError.Busy();
            }

            await store.UpdateBalanceAsync(
                row, 0m, -open, NextAllowNegative(row, allowNegative, row.OnHand, row.Reserved - open, worsening: false), cancellationToken);
            var status = await store.ProgressReservationAsync(reservation.Id, 0m, open, cancellationToken);
            released.Add(new ReservationAllocation(
                reservation.Id, reservation.OrderLineId, reservation.VariantId, reservation.WarehouseId, reservation.LocationId, open, status));
        }

        return new ReservationResult(command.OrderId, released);
    }

    // ---- §4.6 Issue on shipment confirmation ----

    private async Task<InventoryResult<IssueResult>> IssueCoreAsync(IssueStock command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var lines = command.Lines ?? [];
        ValidateLineCount(errors, lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            ValidateQuantity(errors, $"lines[{i}].quantity", lines[i].Quantity);
        }

        if (lines.Select(l => (l.OrderLineId, l.LocationId)).Distinct().Count() != lines.Count)
        {
            errors["lines"] = ["Each (order line, location) may appear only once."];
        }

        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var warehouse = await warehouses.FindWarehouseAsync(command.WarehouseId, cancellationToken);
        if (warehouse is null)
        {
            return InventoryError.Validation(new Dictionary<string, string[]> { ["warehouseId"] = ["Warehouse not found."] });
        }

        var orderLineIds = lines.Select(l => l.OrderLineId).Distinct().ToList();
        var candidates = await store.FindActiveReservationsAsync(null, orderLineIds, cancellationToken);
        var keys = lines.Select(l => new BalanceKey(l.VariantId, warehouse.Id, l.LocationId)).ToList();
        var allowNegative = await AllowsNegativeAsync(cancellationToken);
        var rows = Index(await store.LockBalancesAsync(keys, allowNegative, createMissing: false, cancellationToken));
        var reservations = candidates.Count == 0 ? [] : await store.LockActiveReservationsAsync(null, orderLineIds, cancellationToken);

        var plan = new List<(IssueLine Line, BalanceRow Row, ReservationRow Reservation)>();
        foreach (var line in lines)
        {
            var key = new BalanceKey(line.VariantId, warehouse.Id, line.LocationId);
            var reservation = reservations.FirstOrDefault(r => r.OrderLineId == line.OrderLineId && r.Key == key);
            if (reservation is null || reservation.Open < line.Quantity || !rows.TryGetValue(key, out var row))
            {
                return new InventoryError(409, "inventory.not_reserved", "The shipped quantity is not reserved for this order line and location");
            }

            plan.Add((line, row, reservation));
        }

        var movements = new List<MovementResult>(plan.Count);
        foreach (var (line, row, reservation) in plan)
        {
            // on_hand and reserved drop together: available is unchanged (I5).
            var after = await store.UpdateBalanceAsync(
                row, -line.Quantity, -line.Quantity,
                NextAllowNegative(row, allowNegative, row.OnHand - line.Quantity, row.Reserved - line.Quantity, worsening: false),
                cancellationToken);
            movements.Add(await store.InsertMovementAsync(
                new NewMovement(row.Key, MovementTypes.ShipmentOut, -line.Quantity, null, MovementSourceTypes.Shipment,
                    command.ShipmentId, line.ShipmentLineId, null, command.CorrelationId),
                after,
                cancellationToken));
            await store.ProgressReservationAsync(reservation.Id, line.Quantity, 0m, cancellationToken);
        }

        return new IssueResult(command.ShipmentId, movements);
    }

    // ---- Shared ----

    private async Task<InventoryResult<T>> RunAsync<T>(Func<Task<InventoryResult<T>>> operation, CancellationToken cancellationToken)
    {
        await store.BeginOperationAsync(Savepoint, cancellationToken);
        InventoryResult<T> result;
        try
        {
            result = await operation();
        }
        catch (PostgresException ex) when (FromDatabaseError(ex) is { } error)
        {
            await store.AbortOperationAsync(Savepoint, CancellationToken.None);
            return error;
        }
        catch
        {
            await TryAbortAsync();
            throw;
        }

        if (result.Error is null)
        {
            await store.CompleteOperationAsync(Savepoint, cancellationToken);
        }
        else
        {
            await store.AbortOperationAsync(Savepoint, CancellationToken.None);
        }

        return result;

        async Task TryAbortAsync()
        {
            try
            {
                await store.AbortOperationAsync(Savepoint, CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // The connection is unusable; the request transaction is rolled back on disposal anyway.
            }
        }
    }

    /// <summary>Database last line of defence (§2.4) mapped to stable codes.</summary>
    private static InventoryError? FromDatabaseError(PostgresException ex) => ex.SqlState switch
    {
        PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected => InventoryError.Busy(),
        PostgresErrorCodes.CheckViolation when ex.TableName == "inventory_balances" =>
            new InventoryError(409, "inventory.insufficient_stock", "The stock change would violate the negative-stock rule"),
        PostgresErrorCodes.UniqueViolation when ex.ConstraintName == InventoryDbContext.MovementSourceLineIndex =>
            new InventoryError(409, "inventory.duplicate_source", "This source document line was already applied to stock"),
        PostgresErrorCodes.UniqueViolation when ex.ConstraintName == InventoryDbContext.ActiveReservationIndex => AlreadyReserved(),
        _ => null,
    };

    private static InventoryError AlreadyReserved() =>
        new(409, "inventory.already_reserved", "An order line already has an active reservation");

    /// <summary>
    /// Negative-stock policy (§2.4, PRD TEN-12): <c>block</c> → the given error; <c>warn</c> → 409 asking for explicit
    /// confirmation unless the caller confirmed; <c>allow</c> → proceed.
    /// </summary>
    private static InventoryError? NegativeStockDecision(
        string policy, bool confirmed, List<StockShortage> shortages, InventoryError blocked)
    {
        if (shortages.Count == 0)
        {
            return null;
        }

        return policy switch
        {
            NegativeStockPolicies.Allow => null,
            NegativeStockPolicies.Warn when confirmed => null,
            NegativeStockPolicies.Warn => new InventoryError(
                409,
                "inventory.insufficient_stock",
                "Stock would go negative; repeat with confirmNegativeStock=true to proceed",
                Shortages: shortages,
                ConfirmationRequired: true),
            _ => blocked,
        };
    }

    /// <summary>
    /// The row's <c>allow_negative</c> after a change: the tenant policy, except that a row that is already negative
    /// (left over from a laxer policy) may keep the flag for changes that do not make it worse — e.g. a receipt.
    /// Worsening changes under <c>block</c> always write <c>false</c>, so the DB CHECK stays a real backstop.
    /// </summary>
    private static bool NextAllowNegative(BalanceRow row, bool policyAllows, decimal onHandAfter, decimal reservedAfter, bool worsening) =>
        policyAllows || (!worsening && row.AllowNegative && !(onHandAfter >= 0 && reservedAfter <= onHandAfter));

    private async Task<bool> AllowsNegativeAsync(CancellationToken cancellationToken) =>
        await policies.GetPolicyAsync(cancellationToken) != NegativeStockPolicies.Block;

    private static InventoryError? ValidateWarehouse(WarehouseInfo? warehouse) => warehouse switch
    {
        null => InventoryError.Validation(new Dictionary<string, string[]> { ["warehouseId"] = ["Warehouse not found."] }),
        { IsActive: false } => InventoryError.Validation(new Dictionary<string, string[]> { ["warehouseId"] = ["Warehouse is inactive."] }),
        _ => null,
    };

    /// <summary>§2.6: located warehouses require an active location of that warehouse; others accept none.</summary>
    private async Task ValidateLocationsAsync(
        Dictionary<string, string[]> errors, WarehouseInfo warehouse, IReadOnlyList<(string Field, Guid? LocationId)> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Where(l => l.LocationId is not null).Select(l => l.LocationId!.Value).Distinct().ToList();
        var found = ids.Count == 0 ? new Dictionary<Guid, LocationInfo>() : await warehouses.FindLocationsAsync(ids, cancellationToken);
        foreach (var (field, locationId) in lines)
        {
            if (!warehouse.UsesLocations)
            {
                if (locationId is not null)
                {
                    errors[field] = ["This warehouse does not use locations."];
                }

                continue;
            }

            if (locationId is not { } id)
            {
                errors[field] = ["A location is required in this warehouse."];
            }
            else if (!found.TryGetValue(id, out var location) || location.WarehouseId != warehouse.Id)
            {
                errors[field] = ["Location not found in this warehouse."];
            }
            else if (!location.IsActive)
            {
                errors[field] = ["Location is inactive."];
            }
        }
    }

    private async Task<IReadOnlyDictionary<Guid, VariantInfo>> ValidateVariantsAsync(
        Dictionary<string, string[]> errors,
        IReadOnlyList<(string Prefix, Guid VariantId, decimal Quantity)> lines,
        bool requireActive,
        CancellationToken cancellationToken)
    {
        var found = await variants.FindVariantsAsync([.. lines.Select(l => l.VariantId).Distinct()], cancellationToken);
        foreach (var (prefix, variantId, quantity) in lines)
        {
            var field = prefix.Length == 0 ? "variantId" : prefix + ".variantId";
            if (!found.TryGetValue(variantId, out var variant))
            {
                errors[field] = ["Variant not found."];
            }
            else if (requireActive && !variant.IsActive)
            {
                errors[field] = ["Variant is not active."];
            }
            else if (!variant.AllowsFraction && quantity != decimal.Truncate(quantity))
            {
                errors[prefix.Length == 0 ? "quantity" : prefix + ".quantity"] = ["The variant's unit does not allow fractions."];
            }
        }

        return found;
    }

    private static void ValidateLineCount(Dictionary<string, string[]> errors, int count)
    {
        if (count is < 1 or > MaxLines)
        {
            errors["lines"] = [$"Between 1 and {MaxLines} lines are required."];
        }
    }

    private static void ValidateQuantity(Dictionary<string, string[]> errors, string field, decimal quantity)
    {
        if (quantity <= 0 || quantity > MaxQuantity || decimal.Round(quantity, 3) != quantity)
        {
            errors[field] = ["Must be greater than 0 with at most 3 decimals."];
        }
    }

    private static Dictionary<BalanceKey, BalanceRow> Index(IReadOnlyList<BalanceRow> rows) => rows.ToDictionary(r => r.Key);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

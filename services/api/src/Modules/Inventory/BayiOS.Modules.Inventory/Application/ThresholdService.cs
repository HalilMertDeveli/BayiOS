using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Inventory.Application;

/// <summary>One requested change of a bulk threshold write. <see cref="MinQuantity"/> null = remove.</summary>
internal sealed record ThresholdChange(Guid VariantId, Guid? WarehouseId, decimal? MinQuantity);

internal readonly record struct ThresholdKey(Guid VariantId, Guid? WarehouseId);

/// <param name="Created">True when the row did not exist (201), false when it was updated or unchanged (200).</param>
internal sealed record ThresholdSetResult(StockThresholdResponse Threshold, bool Created);

/// <summary>
/// Minimum stock thresholds (#137, data-model §5.13): writes and the critical-stock read side
/// (<see cref="IStockThresholdQueries"/>). Configuration only — no movement, no balance change. Writes run as SQL on the
/// request UnitOfWork transaction under a savepoint with <c>lock_timeout</c>; rows of one request are locked
/// (<c>SELECT … FOR UPDATE</c>) in the fixed order (variant_id, warehouse_id NULLS FIRST), so concurrent bulk writes
/// cannot deadlock. Every change is audited in the same transaction.
/// </summary>
internal sealed class ThresholdService(
    UnitOfWork unitOfWork,
    IRequestContext requestContext,
    IWarehouseDirectory warehouses,
    IVariantDirectory variants,
    IAuditLog audit) : IStockThresholdQueries
{
    public const int MaxBulkItems = 500;

    /// <summary>Same ceiling as the critical-stock report's explicit threshold.</summary>
    public const decimal MaxMinQuantity = 1_000_000_000m;

    private const string Savepoint = "inventory_threshold";
    private const string Columns = "id, variant_id, warehouse_id, min_quantity, version, created_at, updated_at";
    private const string Order = "variant_id, warehouse_id NULLS FIRST";

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private NpgsqlTransaction Transaction =>
        unitOfWork.Transaction ?? throw new InvalidOperationException("Threshold operations need an active unit of work.");

    // ---------------------------------------------------------------------------------------------------- commands

    /// <summary>Upsert by (variant, warehouse). <c>Created</c> tells 201 from 200.</summary>
    public async Task<InventoryResult<ThresholdSetResult>> SetAsync(
        Guid variantId, Guid? warehouseId, decimal minQuantity, CancellationToken cancellationToken)
    {
        var result = await BulkAsync([new ThresholdChange(variantId, warehouseId, minQuantity)], single: true, cancellationToken);
        if (result.Error is { } error)
        {
            return error;
        }

        var value = result.Value!;
        return new ThresholdSetResult(value.Items[0], value.Created == 1);
    }

    /// <summary>Changes the minimum of one threshold row, guarded by its version (If-Match).</summary>
    public Task<InventoryResult<StockThresholdResponse>> UpdateAsync(Guid id, decimal? minQuantity, long expectedVersion, CancellationToken cancellationToken) =>
        RunAsync<StockThresholdResponse>(
            async () =>
            {
                if (minQuantity is not { } min)
                {
                    return InventoryError.Validation(new Dictionary<string, string[]> { ["minQuantity"] = ["Required."] });
                }

                if (await LockByIdAsync(id, cancellationToken) is not { } row)
                {
                    return NotFound();
                }

                if (row.Version != expectedVersion)
                {
                    return VersionMismatch();
                }

                var errors = new Dictionary<string, string[]>();
                var found = await variants.FindVariantsAsync([row.VariantId], cancellationToken);
                ValidateMinimum(errors, "minQuantity", min, found.GetValueOrDefault(row.VariantId));
                if (errors.Count > 0)
                {
                    return InventoryError.Validation(errors);
                }

                if (row.MinQuantity == min)
                {
                    return row;
                }

                var updated = (await UpdateRowsAsync([(row.Id, min)], cancellationToken))[0];
                await AuditSetAsync(updated, row.MinQuantity, cancellationToken);
                return updated;
            },
            cancellationToken);

    /// <summary>Removes one threshold row, guarded by its version (If-Match).</summary>
    public Task<InventoryResult<bool>> DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        RunAsync<bool>(
            async () =>
            {
                if (await LockByIdAsync(id, cancellationToken) is not { } row)
                {
                    return NotFound();
                }

                if (row.Version != expectedVersion)
                {
                    return VersionMismatch();
                }

                await DeleteRowsAsync([row.Id], cancellationToken);
                await AuditRemovedAsync(row, cancellationToken);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Sets and removes thresholds in one all-or-nothing step. Validates everything first (variants and warehouses of
    /// the caller's tenant only), then locks the existing rows, inserts the missing ones, updates changed values and
    /// deletes removed ones. Unchanged rows are not written (no version bump, no audit).
    /// </summary>
    public Task<InventoryResult<BulkStockThresholdResponse>> BulkAsync(
        IReadOnlyList<ThresholdChange> changes, bool single, CancellationToken cancellationToken) =>
        RunAsync(() => BulkCoreAsync(changes, single, cancellationToken), cancellationToken);

    private async Task<InventoryResult<BulkStockThresholdResponse>> BulkCoreAsync(
        IReadOnlyList<ThresholdChange> changes, bool single, CancellationToken cancellationToken)
    {
        string Field(int i, string name) => single ? name : $"items[{i}].{name}";

        var errors = new Dictionary<string, string[]>();
        if (changes.Count is < 1 or > MaxBulkItems)
        {
            errors["items"] = [$"Between 1 and {MaxBulkItems} items are required."];
            return InventoryError.Validation(errors);
        }

        var found = await variants.FindVariantsAsync([.. changes.Select(c => c.VariantId).Distinct()], cancellationToken);
        var warehouseIds = changes.Where(c => c.WarehouseId is not null).Select(c => c.WarehouseId!.Value).Distinct().ToList();
        var knownWarehouses = new HashSet<Guid>();
        foreach (var warehouseId in warehouseIds)
        {
            if (await warehouses.FindWarehouseAsync(warehouseId, cancellationToken) is not null)
            {
                knownWarehouses.Add(warehouseId);
            }
        }

        var seen = new HashSet<ThresholdKey>();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (!seen.Add(new ThresholdKey(change.VariantId, change.WarehouseId)))
            {
                errors[$"items[{i}]"] = ["The same variant and warehouse appear more than once."];
                continue;
            }

            if (!found.TryGetValue(change.VariantId, out var variant))
            {
                errors[Field(i, "variantId")] = ["Variant not found."];
                continue;
            }

            if (change.WarehouseId is { } warehouseId && !knownWarehouses.Contains(warehouseId))
            {
                errors[Field(i, "warehouseId")] = ["Warehouse not found."];
            }

            if (change.MinQuantity is { } min)
            {
                ValidateMinimum(errors, Field(i, "minQuantity"), min, variant);
            }
        }

        if (errors.Count > 0)
        {
            return InventoryError.Validation(errors);
        }

        var keys = changes.Select(c => new ThresholdKey(c.VariantId, c.WarehouseId)).ToList();
        var existing = (await LockByKeysAsync(keys, cancellationToken)).ToDictionary(r => new ThresholdKey(r.VariantId, r.WarehouseId));

        // Missing rows: insert; a row a concurrent request inserted meanwhile is locked and updated instead.
        var toInsert = changes.Where(c => c.MinQuantity is not null && !existing.ContainsKey(new ThresholdKey(c.VariantId, c.WarehouseId))).ToList();
        var inserted = await InsertRowsAsync(toInsert, cancellationToken);
        var insertedKeys = inserted.Select(r => new ThresholdKey(r.VariantId, r.WarehouseId)).ToHashSet();
        var raced = toInsert.Select(c => new ThresholdKey(c.VariantId, c.WarehouseId)).Where(k => !insertedKeys.Contains(k)).ToList();
        if (raced.Count > 0)
        {
            foreach (var row in await LockByKeysAsync(raced, cancellationToken))
            {
                existing[new ThresholdKey(row.VariantId, row.WarehouseId)] = row;
            }
        }

        var updates = new List<(Guid Id, decimal Min)>();
        var previous = new Dictionary<Guid, decimal>();
        var removals = new List<StockThresholdResponse>();
        var unchanged = new List<StockThresholdResponse>();
        foreach (var change in changes)
        {
            var key = new ThresholdKey(change.VariantId, change.WarehouseId);
            if (insertedKeys.Contains(key))
            {
                continue;
            }

            var current = existing.GetValueOrDefault(key);
            if (change.MinQuantity is not { } min)
            {
                if (current is not null)
                {
                    removals.Add(current);
                }

                continue;
            }

            if (current is null)
            {
                // Inserted by a concurrent request and deleted again before we could lock it.
                return InventoryError.Busy();
            }

            if (current.MinQuantity == min)
            {
                unchanged.Add(current);
            }
            else
            {
                updates.Add((current.Id, min));
                previous[current.Id] = current.MinQuantity;
            }
        }

        var updated = await UpdateRowsAsync(updates, cancellationToken);
        await DeleteRowsAsync([.. removals.Select(r => r.Id)], cancellationToken);

        foreach (var row in inserted)
        {
            await AuditSetAsync(row, null, cancellationToken);
        }

        foreach (var row in updated)
        {
            await AuditSetAsync(row, previous[row.Id], cancellationToken);
        }

        foreach (var row in removals)
        {
            await AuditRemovedAsync(row, cancellationToken);
        }

        // Response rows in request order (removed rows are absent).
        var after = inserted.Concat(updated).Concat(unchanged).ToDictionary(r => new ThresholdKey(r.VariantId, r.WarehouseId));
        var items = keys.Where(after.ContainsKey).Select(k => after[k]).ToList();
        return new BulkStockThresholdResponse(inserted.Count, updated.Count, removals.Count, unchanged.Count, items);
    }

    // ---------------------------------------------------------------------------------------------------- queries

    public async Task<IReadOnlyDictionary<Guid, decimal>> EffectiveMinimumsAsync(
        IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        await using var command = Command($"{EffectiveMinimums(filterVariants: true)} SELECT variant_id, min_quantity FROM m WHERE min_quantity IS NOT NULL");
        AddScope(command, warehouseId);
        command.Parameters.Add(Uuids("variants", [.. variantIds.Distinct()]));
        var result = new Dictionary<Guid, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetGuid(0)] = reader.GetDecimal(1);
        }

        return result;
    }

    public Task<IReadOnlyList<Guid>> CriticalVariantsAsync(Guid? warehouseId, CancellationToken cancellationToken) =>
        ReadIdsAsync(
            $"""
            {EffectiveMinimums(filterVariants: false)}, {Available}
            SELECT m.variant_id FROM m LEFT JOIN s ON s.variant_id = m.variant_id
            WHERE m.min_quantity IS NOT NULL AND coalesce(s.available, 0) <= m.min_quantity
            """,
            warehouseId,
            cancellationToken);

    public Task<IReadOnlyList<Guid>> VariantsAboveMinimumOrZeroAsync(Guid? warehouseId, CancellationToken cancellationToken) =>
        ReadIdsAsync(
            $"""
            {EffectiveMinimums(filterVariants: false)}, {Available}
            SELECT s.variant_id FROM s LEFT JOIN m ON m.variant_id = s.variant_id
            WHERE s.available > coalesce(m.min_quantity, 0)
            """,
            warehouseId,
            cancellationToken);

    public async Task<bool> AnyThresholdAsync(Guid? warehouseId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            SELECT EXISTS (
                SELECT 1 FROM inventory.stock_thresholds t
                WHERE t.tenant_id = @tenant AND (@warehouse::uuid IS NULL OR t.warehouse_id IS NULL OR t.warehouse_id = @warehouse))
            """);
        AddScope(command, warehouseId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// CTE <c>m(variant_id, min_quantity)</c>: the effective minimum per variant in the scope (see
    /// <see cref="IStockThresholdQueries"/>): one warehouse → override, else default; all warehouses → default, else
    /// Σ overrides.
    /// </summary>
    private static string EffectiveMinimums(bool filterVariants) =>
        $"""
        WITH m AS (
            SELECT t.variant_id,
                   CASE WHEN @warehouse::uuid IS NULL
                        THEN coalesce(max(t.min_quantity) FILTER (WHERE t.warehouse_id IS NULL),
                                      sum(t.min_quantity) FILTER (WHERE t.warehouse_id IS NOT NULL))
                        ELSE coalesce(max(t.min_quantity) FILTER (WHERE t.warehouse_id = @warehouse),
                                      max(t.min_quantity) FILTER (WHERE t.warehouse_id IS NULL))
                   END AS min_quantity
            FROM inventory.stock_thresholds t
            WHERE t.tenant_id = @tenant
              AND (@warehouse::uuid IS NULL OR t.warehouse_id IS NULL OR t.warehouse_id = @warehouse)
              {(filterVariants ? "AND t.variant_id = ANY(@variants)" : string.Empty)}
            GROUP BY t.variant_id)
        """;

    /// <summary>CTE <c>s(variant_id, available)</c>: Σ on_hand − Σ reserved per variant over the scope's bins.</summary>
    private const string Available =
        """
        s AS (
            SELECT b.variant_id, sum(b.on_hand) - sum(b.reserved) AS available
            FROM inventory.inventory_balances b
            WHERE b.tenant_id = @tenant AND NOT b.is_transit AND (@warehouse::uuid IS NULL OR b.warehouse_id = @warehouse)
            GROUP BY b.variant_id)
        """;

    private async Task<IReadOnlyList<Guid>> ReadIdsAsync(string sql, Guid? warehouseId, CancellationToken cancellationToken)
    {
        await using var command = Command(sql);
        AddScope(command, warehouseId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private void AddScope(NpgsqlCommand command, Guid? warehouseId)
    {
        command.Parameters.Add(Uuid("tenant", TenantId));
        command.Parameters.Add(Uuid("warehouse", warehouseId));
    }

    // ---------------------------------------------------------------------------------------------------- SQL

    private async Task<StockThresholdResponse?> LockByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"SELECT {Columns} FROM inventory.stock_thresholds WHERE tenant_id = @tenant AND id = @id FOR UPDATE");
        command.Parameters.Add(Uuid("tenant", TenantId));
        command.Parameters.Add(Uuid("id", id));
        return (await ReadRowsAsync(command, cancellationToken)).SingleOrDefault();
    }

    private async Task<IReadOnlyList<StockThresholdResponse>> LockByKeysAsync(IReadOnlyCollection<ThresholdKey> keys, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            SELECT t.{Columns.Replace(", ", ", t.", StringComparison.Ordinal)}
            FROM inventory.stock_thresholds t
            JOIN unnest(@variants, @warehouses) AS k(variant_id, warehouse_id)
              ON t.variant_id = k.variant_id AND t.warehouse_id IS NOT DISTINCT FROM k.warehouse_id
            WHERE t.tenant_id = @tenant
            ORDER BY t.{Order.Replace(", ", ", t.", StringComparison.Ordinal)}
            FOR UPDATE OF t
            """);
        command.Parameters.Add(Uuid("tenant", TenantId));
        command.Parameters.Add(Uuids("variants", [.. keys.Select(k => k.VariantId)]));
        command.Parameters.Add(NullableUuids("warehouses", [.. keys.Select(k => k.WarehouseId)]));
        return await ReadRowsAsync(command, cancellationToken);
    }

    private async Task<IReadOnlyList<StockThresholdResponse>> InsertRowsAsync(List<ThresholdChange> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        await using var command = Command(
            $"""
            INSERT INTO inventory.stock_thresholds (id, tenant_id, variant_id, warehouse_id, min_quantity, version, created_at, created_by)
            SELECT k.id, @tenant, k.variant_id, k.warehouse_id, k.min_quantity, 1, now(), @user
            FROM unnest(@ids, @variants, @warehouses, @mins) AS k(id, variant_id, warehouse_id, min_quantity)
            ORDER BY k.{Order.Replace(", ", ", k.", StringComparison.Ordinal)}
            ON CONFLICT (tenant_id, variant_id, warehouse_id) DO NOTHING
            RETURNING {Columns}
            """);
        command.Parameters.Add(Uuid("tenant", TenantId));
        command.Parameters.Add(Uuid("user", requestContext.UserId));
        command.Parameters.Add(Uuids("ids", [.. rows.Select(_ => Guid.CreateVersion7())]));
        command.Parameters.Add(Uuids("variants", [.. rows.Select(r => r.VariantId)]));
        command.Parameters.Add(NullableUuids("warehouses", [.. rows.Select(r => r.WarehouseId)]));
        command.Parameters.Add(new NpgsqlParameter("mins", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = rows.Select(r => r.MinQuantity!.Value).ToArray() });
        return await ReadRowsAsync(command, cancellationToken);
    }

    private async Task<IReadOnlyList<StockThresholdResponse>> UpdateRowsAsync(List<(Guid Id, decimal Min)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        await using var command = Command(
            $"""
            UPDATE inventory.stock_thresholds t
            SET min_quantity = k.min_quantity, version = t.version + 1, updated_at = now(), updated_by = @user
            FROM unnest(@ids, @mins) AS k(id, min_quantity)
            WHERE t.tenant_id = @tenant AND t.id = k.id
            RETURNING t.{Columns.Replace(", ", ", t.", StringComparison.Ordinal)}
            """);
        command.Parameters.Add(Uuid("tenant", TenantId));
        command.Parameters.Add(Uuid("user", requestContext.UserId));
        command.Parameters.Add(Uuids("ids", [.. rows.Select(r => r.Id)]));
        command.Parameters.Add(new NpgsqlParameter("mins", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = rows.Select(r => r.Min).ToArray() });
        return await ReadRowsAsync(command, cancellationToken);
    }

    private async Task DeleteRowsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var command = Command("DELETE FROM inventory.stock_thresholds WHERE tenant_id = @tenant AND id = ANY(@ids)");
        command.Parameters.Add(Uuid("tenant", TenantId));
        command.Parameters.Add(Uuids("ids", [.. ids]));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<StockThresholdResponse>> ReadRowsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<StockThresholdResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StockThresholdResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetDecimal(3),
                reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------------- helpers

    private async Task<InventoryResult<T>> RunAsync<T>(Func<Task<InventoryResult<T>>> operation, CancellationToken cancellationToken)
    {
        await Transaction.SaveAsync(Savepoint, cancellationToken);
        await using (var timeout = Command($"SET LOCAL lock_timeout = '{StockStore.LockTimeout}'"))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        InventoryResult<T> result;
        try
        {
            result = await operation();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            await Transaction.RollbackAsync(Savepoint, CancellationToken.None);
            return InventoryError.Busy();
        }

        if (result.Error is null)
        {
            await Transaction.ReleaseAsync(Savepoint, cancellationToken);
        }
        else
        {
            await Transaction.RollbackAsync(Savepoint, CancellationToken.None);
        }

        return result;
    }

    private static void ValidateMinimum(Dictionary<string, string[]> errors, string field, decimal min, VariantInfo? variant)
    {
        if (min < 0 || min > MaxMinQuantity || decimal.Round(min, 3) != min)
        {
            errors[field] = [$"Must be between 0 and {MaxMinQuantity:0} with at most 3 decimals."];
        }
        else if (variant is { AllowsFraction: false } && min != decimal.Truncate(min))
        {
            errors[field] = ["The variant's unit does not allow fractions."];
        }
    }

    private Task AuditSetAsync(StockThresholdResponse row, decimal? before, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent(AuditActions.InventoryThresholdSet, "stock_threshold", row.Id, new AuditChanges()
                .Value("variantId", row.VariantId)
                .Value("warehouseId", row.WarehouseId)
                .Value("minQuantity", new Dictionary<string, object?> { ["from"] = before, ["to"] = row.MinQuantity })
                .ToDictionary()),
            cancellationToken);

    private Task AuditRemovedAsync(StockThresholdResponse row, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent(AuditActions.InventoryThresholdRemoved, "stock_threshold", row.Id, new AuditChanges()
                .Value("variantId", row.VariantId)
                .Value("warehouseId", row.WarehouseId)
                .Value("minQuantity", new Dictionary<string, object?> { ["from"] = row.MinQuantity, ["to"] = null })
                .ToDictionary()),
            cancellationToken);

    private static InventoryError NotFound() => new(404, "resource.not_found", "Resource not found");

    private static InventoryError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "If-Match does not match the current version");

    private NpgsqlCommand Command(string sql) => new(sql, unitOfWork.Connection, Transaction);

    private static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value is { } v ? v : DBNull.Value };

    private static NpgsqlParameter Uuids(string name, Guid[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values };

    private static NpgsqlParameter NullableUuids(string name, Guid?[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values };
}

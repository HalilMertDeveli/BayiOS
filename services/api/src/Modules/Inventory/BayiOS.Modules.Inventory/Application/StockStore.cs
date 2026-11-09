using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Inventory.Domain;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Inventory.Application;

/// <summary>Identity of a balance row: (variant, warehouse, location or null).</summary>
internal readonly record struct BalanceKey(Guid VariantId, Guid WarehouseId, Guid? LocationId);

/// <summary>A locked balance row, kept current by <see cref="StockStore.UpdateBalanceAsync"/>.</summary>
internal sealed class BalanceRow
{
    public required Guid Id { get; init; }

    public required BalanceKey Key { get; init; }

    public decimal OnHand { get; set; }

    public decimal Reserved { get; set; }

    public bool AllowNegative { get; set; }

    /// <summary>In-transit stock of a transfer (OD-01); see <c>InventoryBalance.IsTransit</c>.</summary>
    public bool IsTransit { get; init; }

    public decimal Available => OnHand - Reserved;
}

internal sealed record ReservationRow(
    Guid Id,
    Guid OrderId,
    Guid OrderLineId,
    Guid VariantId,
    Guid WarehouseId,
    Guid? LocationId,
    decimal Quantity,
    decimal ConsumedQuantity,
    decimal ReleasedQuantity,
    string Status)
{
    public decimal Open => Quantity - ConsumedQuantity - ReleasedQuantity;

    public BalanceKey Key => new(VariantId, WarehouseId, LocationId);
}

internal sealed record NewMovement(
    BalanceKey Key,
    string Type,
    decimal Quantity,
    decimal? UnitCost,
    string SourceType,
    Guid? SourceId,
    Guid? SourceLineId,
    string? Reason,
    string? CorrelationId,
    Guid? TransferGroupId = null);

/// <summary>
/// SQL of the stock engine (inventory-and-orders §2), always on the request's UnitOfWork connection and transaction.
/// Balance rows are locked with <c>SELECT … FOR UPDATE</c> in the fixed order
/// <c>(variant_id, warehouse_id, location_id NULLS FIRST)</c> — evaluated by PostgreSQL, so every operation agrees on
/// the order and multi-row operations cannot deadlock each other.
/// </summary>
internal sealed class StockStore(UnitOfWork unitOfWork, IRequestContext requestContext)
{
    /// <summary>Lock wait limit (§2.3); exceeding it surfaces as <c>409 inventory.busy</c>.</summary>
    public const string LockTimeout = "5s";

    private const string Order = "variant_id, warehouse_id, location_id NULLS FIRST";

    private const string BalanceColumns = "id, variant_id, warehouse_id, location_id, on_hand, reserved, allow_negative, is_transit";

    private const string ReservationColumns =
        "id, order_id, order_line_id, variant_id, warehouse_id, location_id, quantity, consumed_quantity, released_quantity, status";

    public Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private NpgsqlTransaction Transaction =>
        unitOfWork.Transaction ?? throw new InvalidOperationException("Stock operations need an active unit of work.");

    public async Task BeginOperationAsync(string savepoint, CancellationToken cancellationToken)
    {
        await Transaction.SaveAsync(savepoint, cancellationToken);
        await ExecuteAsync($"SET LOCAL lock_timeout = '{LockTimeout}'", cancellationToken);
    }

    public Task CompleteOperationAsync(string savepoint, CancellationToken cancellationToken) =>
        Transaction.ReleaseAsync(savepoint, cancellationToken);

    public Task AbortOperationAsync(string savepoint, CancellationToken cancellationToken) =>
        Transaction.RollbackAsync(savepoint, cancellationToken);

    /// <summary>
    /// LOCK_BALANCES (§2.5): creates missing rows (<c>INSERT … ON CONFLICT DO NOTHING</c>, sorted) and locks every
    /// key's row (<c>FOR UPDATE</c>, sorted). Returns the rows in lock order.
    /// </summary>
    /// <param name="transitIds">Warehouse-id slots that are in-transit points (transfer ids): their missing rows are
    /// created with <c>is_transit</c> and <c>allow_negative = false</c>.</param>
    public async Task<IReadOnlyList<BalanceRow>> LockBalancesAsync(
        IReadOnlyCollection<BalanceKey> keys,
        bool createMissing,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? transitIds = null)
    {
        var distinct = keys.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var variants = distinct.Select(k => k.VariantId).ToArray();
        var warehouses = distinct.Select(k => k.WarehouseId).ToArray();
        var locations = distinct.Select(k => k.LocationId).ToArray();

        if (createMissing)
        {
            await using var insert = Command(
                $"""
                INSERT INTO inventory.inventory_balances
                    (id, tenant_id, variant_id, warehouse_id, location_id, on_hand, reserved, allow_negative, is_transit, version, created_at)
                SELECT k.id, @tenant, k.variant_id, k.warehouse_id, k.location_id, 0, 0,
                       false, k.warehouse_id = ANY(@transit), 0, now() -- OD-09: never negative
                FROM unnest(@ids, @variants, @warehouses, @locations) AS k(id, variant_id, warehouse_id, location_id)
                ORDER BY k.{Order.Replace(", ", ", k.", StringComparison.Ordinal)}
                ON CONFLICT (tenant_id, variant_id, warehouse_id, location_id) DO NOTHING
                """);
            insert.Parameters.Add(Uuid("tenant", TenantId));
            insert.Parameters.Add(Uuids("ids", distinct.Select(_ => (Guid?)Guid.CreateVersion7()).ToArray()));
            insert.Parameters.Add(Uuids("variants", variants.Select(v => (Guid?)v).ToArray()));
            insert.Parameters.Add(Uuids("warehouses", warehouses.Select(w => (Guid?)w).ToArray()));
            insert.Parameters.Add(Uuids("locations", locations));
            insert.Parameters.Add(Uuids("transit", [.. (transitIds ?? []).Select(t => (Guid?)t)]));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = Command(
            $"""
            SELECT b.{BalanceColumns.Replace(", ", ", b.", StringComparison.Ordinal)}
            FROM inventory.inventory_balances b
            JOIN unnest(@variants, @warehouses, @locations) AS k(variant_id, warehouse_id, location_id)
              ON b.variant_id = k.variant_id AND b.warehouse_id = k.warehouse_id
             AND b.location_id IS NOT DISTINCT FROM k.location_id
            WHERE b.tenant_id = @tenant
            ORDER BY b.{Order.Replace(", ", ", b.", StringComparison.Ordinal)}
            FOR UPDATE OF b
            """);
        select.Parameters.Add(Uuid("tenant", TenantId));
        select.Parameters.Add(Uuids("variants", variants.Select(v => (Guid?)v).ToArray()));
        select.Parameters.Add(Uuids("warehouses", warehouses.Select(w => (Guid?)w).ToArray()));
        select.Parameters.Add(Uuids("locations", locations));
        return await ReadBalancesAsync(select, cancellationToken);
    }

    /// <summary>Locks every row of the given variants in one warehouse (all locations), in the fixed order.</summary>
    public async Task<IReadOnlyList<BalanceRow>> LockVariantBalancesAsync(
        Guid warehouseId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        await using var select = Command(
            $"""
            SELECT {BalanceColumns}
            FROM inventory.inventory_balances
            WHERE tenant_id = @tenant AND warehouse_id = @warehouse AND variant_id = ANY(@variants)
            ORDER BY {Order}
            FOR UPDATE
            """);
        select.Parameters.Add(Uuid("tenant", TenantId));
        select.Parameters.Add(Uuid("warehouse", warehouseId));
        select.Parameters.Add(Uuids("variants", variantIds.Select(v => (Guid?)v).ToArray()));
        return await ReadBalancesAsync(select, cancellationToken);
    }

    /// <summary>Applies deltas to a locked row (version + 1) and returns the new on_hand.</summary>
    public async Task<decimal> UpdateBalanceAsync(
        BalanceRow row, decimal onHandDelta, decimal reservedDelta, bool allowNegative, CancellationToken cancellationToken)
    {
        await using var update = Command(
            """
            UPDATE inventory.inventory_balances
               SET on_hand = on_hand + @on_hand_delta,
                   reserved = reserved + @reserved_delta,
                   allow_negative = @allow_negative,
                   version = version + 1,
                   updated_at = now()
             WHERE id = @id AND tenant_id = @tenant
            RETURNING on_hand, reserved
            """);
        update.Parameters.Add(new NpgsqlParameter<decimal>("on_hand_delta", onHandDelta));
        update.Parameters.Add(new NpgsqlParameter<decimal>("reserved_delta", reservedDelta));
        update.Parameters.Add(new NpgsqlParameter<bool>("allow_negative", allowNegative));
        update.Parameters.Add(Uuid("id", row.Id));
        update.Parameters.Add(Uuid("tenant", TenantId));
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Locked balance row disappeared.");
        }

        row.OnHand = reader.GetDecimal(0);
        row.Reserved = reader.GetDecimal(1);
        row.AllowNegative = allowNegative;
        return row.OnHand;
    }

    public async Task<MovementResult> InsertMovementAsync(NewMovement movement, decimal balanceAfter, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        await using var insert = Command(
            """
            INSERT INTO inventory.inventory_movements
                (id, tenant_id, variant_id, warehouse_id, location_id, type, quantity, unit_cost, source_type, source_id,
                 source_line_id, transfer_group_id, reason, balance_after, correlation_id, occurred_at, created_by)
            VALUES (@id, @tenant, @variant, @warehouse, @location, @type, @quantity, @unit_cost, @source_type, @source_id,
                    @source_line_id, @transfer_group, @reason, @balance_after, @correlation_id, now(), @user)
            RETURNING occurred_at
            """);
        insert.Parameters.Add(Uuid("id", id));
        insert.Parameters.Add(Uuid("tenant", TenantId));
        insert.Parameters.Add(Uuid("variant", movement.Key.VariantId));
        insert.Parameters.Add(Uuid("warehouse", movement.Key.WarehouseId));
        insert.Parameters.Add(Uuid("location", movement.Key.LocationId));
        insert.Parameters.Add(Text("type", movement.Type));
        insert.Parameters.Add(new NpgsqlParameter<decimal>("quantity", movement.Quantity));
        insert.Parameters.Add(new NpgsqlParameter("unit_cost", NpgsqlDbType.Numeric) { Value = (object?)movement.UnitCost ?? DBNull.Value });
        insert.Parameters.Add(Text("source_type", movement.SourceType));
        insert.Parameters.Add(Uuid("source_id", movement.SourceId));
        insert.Parameters.Add(Uuid("source_line_id", movement.SourceLineId));
        insert.Parameters.Add(Uuid("transfer_group", movement.TransferGroupId));
        insert.Parameters.Add(Text("reason", movement.Reason));
        insert.Parameters.Add(new NpgsqlParameter<decimal>("balance_after", balanceAfter));
        insert.Parameters.Add(Text("correlation_id", movement.CorrelationId));
        insert.Parameters.Add(Uuid("user", UserId));
        var occurredAt = (DateTime)(await insert.ExecuteScalarAsync(cancellationToken))!;

        return new MovementResult(
            id,
            movement.Key.VariantId,
            movement.Key.WarehouseId,
            movement.Key.LocationId,
            movement.Type,
            movement.Quantity,
            movement.UnitCost,
            movement.SourceType,
            movement.SourceId,
            movement.SourceLineId,
            movement.Reason,
            balanceAfter,
            movement.CorrelationId,
            new DateTimeOffset(DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc)),
            UserId);
    }

    /// <summary>
    /// Σ ledger quantity per key after its instant (exclusive). Called with the keys' balance rows locked, so no
    /// movement of these keys can appear concurrently and the sum is exact against the locked on_hand.
    /// </summary>
    public async Task<IReadOnlyDictionary<BalanceKey, decimal>> SumMovementsAfterAsync(
        IReadOnlyList<(BalanceKey Key, DateTimeOffset After)> keys, CancellationToken cancellationToken)
    {
        var sums = new Dictionary<BalanceKey, decimal>();
        if (keys.Count == 0)
        {
            return sums;
        }

        await using var select = Command(
            """
            SELECT k.variant_id, k.warehouse_id, k.location_id, coalesce(sum(m.quantity), 0)
            FROM unnest(@variants, @warehouses, @locations, @after) AS k(variant_id, warehouse_id, location_id, after_at)
            LEFT JOIN inventory.inventory_movements m
              ON m.tenant_id = @tenant AND m.variant_id = k.variant_id AND m.warehouse_id = k.warehouse_id
             AND m.location_id IS NOT DISTINCT FROM k.location_id AND m.occurred_at > k.after_at
            GROUP BY k.variant_id, k.warehouse_id, k.location_id
            """);
        select.Parameters.Add(Uuid("tenant", TenantId));
        select.Parameters.Add(Uuids("variants", [.. keys.Select(k => (Guid?)k.Key.VariantId)]));
        select.Parameters.Add(Uuids("warehouses", [.. keys.Select(k => (Guid?)k.Key.WarehouseId)]));
        select.Parameters.Add(Uuids("locations", [.. keys.Select(k => k.Key.LocationId)]));
        select.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = keys.Select(k => k.After.UtcDateTime).ToArray(),
        });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new BalanceKey(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2));
            sums[key] = reader.GetDecimal(3);
        }

        return sums;
    }

    /// <summary>Active reservations of order lines, unlocked (callers lock balances first, then reservations).</summary>
    public async Task<IReadOnlyList<ReservationRow>> FindActiveReservationsAsync(
        Guid? orderId, IReadOnlyCollection<Guid>? orderLineIds, CancellationToken cancellationToken)
    {
        await using var select = Command(ReservationQuery(lockRows: false, orderId, orderLineIds));
        AddReservationFilter(select, orderId, orderLineIds);
        return await ReadReservationsAsync(select, cancellationToken);
    }

    /// <summary>Re-reads and locks active reservations (lock hierarchy step 4: after the balances).</summary>
    public async Task<IReadOnlyList<ReservationRow>> LockActiveReservationsAsync(
        Guid? orderId, IReadOnlyCollection<Guid>? orderLineIds, CancellationToken cancellationToken)
    {
        await using var select = Command(ReservationQuery(lockRows: true, orderId, orderLineIds));
        AddReservationFilter(select, orderId, orderLineIds);
        return await ReadReservationsAsync(select, cancellationToken);
    }

    public async Task InsertReservationAsync(
        Guid id, Guid orderId, Guid orderLineId, BalanceKey key, decimal quantity, string? correlationId, CancellationToken cancellationToken)
    {
        await using var insert = Command(
            """
            INSERT INTO inventory.stock_reservations
                (id, tenant_id, order_id, order_line_id, variant_id, warehouse_id, location_id, quantity,
                 consumed_quantity, released_quantity, status, correlation_id, created_at, created_by)
            VALUES (@id, @tenant, @order, @order_line, @variant, @warehouse, @location, @quantity,
                    0, 0, 'active', @correlation_id, now(), @user)
            """);
        insert.Parameters.Add(Uuid("id", id));
        insert.Parameters.Add(Uuid("tenant", TenantId));
        insert.Parameters.Add(Uuid("order", orderId));
        insert.Parameters.Add(Uuid("order_line", orderLineId));
        insert.Parameters.Add(Uuid("variant", key.VariantId));
        insert.Parameters.Add(Uuid("warehouse", key.WarehouseId));
        insert.Parameters.Add(Uuid("location", key.LocationId));
        insert.Parameters.Add(new NpgsqlParameter<decimal>("quantity", quantity));
        insert.Parameters.Add(Text("correlation_id", correlationId));
        insert.Parameters.Add(Uuid("user", UserId));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Active reservations sitting on the given balance keys, unlocked (re-allocation candidates, #98).</summary>
    public async Task<IReadOnlyList<ReservationRow>> FindActiveReservationsAtAsync(IReadOnlyCollection<BalanceKey> keys, CancellationToken cancellationToken)
    {
        var distinct = keys.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        await using var select = Command(
            $"""
            SELECT r.{ReservationColumns.Replace(", ", ", r.", StringComparison.Ordinal)}
            FROM inventory.stock_reservations r
            JOIN unnest(@variants, @warehouses, @locations) AS k(variant_id, warehouse_id, location_id)
              ON r.variant_id = k.variant_id AND r.warehouse_id = k.warehouse_id
             AND r.location_id IS NOT DISTINCT FROM k.location_id
            WHERE r.tenant_id = @tenant AND r.status = 'active'
            ORDER BY r.order_line_id, r.location_id NULLS FIRST, r.id
            """);
        select.Parameters.Add(Uuid("tenant", TenantId));
        select.Parameters.Add(Uuids("variants", [.. distinct.Select(k => (Guid?)k.VariantId)]));
        select.Parameters.Add(Uuids("warehouses", [.. distinct.Select(k => (Guid?)k.WarehouseId)]));
        select.Parameters.Add(Uuids("locations", [.. distinct.Select(k => k.LocationId)]));
        return await ReadReservationsAsync(select, cancellationToken);
    }

    /// <summary>Adds to the quantity of a locked active reservation (re-allocation target that already exists).</summary>
    public async Task GrowReservationAsync(Guid id, decimal quantity, CancellationToken cancellationToken)
    {
        await using var update = Command(
            """
            UPDATE inventory.stock_reservations
               SET quantity = quantity + @quantity, updated_at = now(), updated_by = @user
             WHERE id = @id AND tenant_id = @tenant AND status = 'active'
            """);
        update.Parameters.Add(new NpgsqlParameter<decimal>("quantity", quantity));
        update.Parameters.Add(Uuid("user", UserId));
        update.Parameters.Add(Uuid("id", id));
        update.Parameters.Add(Uuid("tenant", TenantId));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Locked reservation is no longer active.");
        }
    }

    /// <summary>Adds consumed/released quantities; closes the reservation when nothing stays open.</summary>
    public async Task<string> ProgressReservationAsync(
        Guid id, decimal consumed, decimal released, CancellationToken cancellationToken)
    {
        await using var update = Command(
            """
            UPDATE inventory.stock_reservations
               SET consumed_quantity = consumed_quantity + @consumed,
                   released_quantity = released_quantity + @released,
                   status = CASE
                       WHEN consumed_quantity + @consumed + released_quantity + @released < quantity THEN 'active'
                       WHEN consumed_quantity + @consumed > 0 THEN 'consumed'
                       ELSE 'released' END,
                   updated_at = now(),
                   updated_by = @user
             WHERE id = @id AND tenant_id = @tenant AND status = 'active'
            RETURNING status
            """);
        update.Parameters.Add(new NpgsqlParameter<decimal>("consumed", consumed));
        update.Parameters.Add(new NpgsqlParameter<decimal>("released", released));
        update.Parameters.Add(Uuid("user", UserId));
        update.Parameters.Add(Uuid("id", id));
        update.Parameters.Add(Uuid("tenant", TenantId));
        return (string?)await update.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Locked reservation is no longer active.");
    }

    private static string ReservationQuery(bool lockRows, Guid? orderId, IReadOnlyCollection<Guid>? orderLineIds) =>
        $"""
        SELECT {ReservationColumns}
        FROM inventory.stock_reservations
        WHERE tenant_id = @tenant AND status = 'active'
          {(orderId is null ? string.Empty : "AND order_id = @order")}
          {(orderLineIds is null ? string.Empty : "AND order_line_id = ANY(@order_lines)")}
        ORDER BY order_line_id, location_id NULLS FIRST, id
        {(lockRows ? "FOR UPDATE" : string.Empty)}
        """;

    private void AddReservationFilter(NpgsqlCommand command, Guid? orderId, IReadOnlyCollection<Guid>? orderLineIds)
    {
        command.Parameters.Add(Uuid("tenant", TenantId));
        if (orderId is not null)
        {
            command.Parameters.Add(Uuid("order", orderId));
        }

        if (orderLineIds is not null)
        {
            command.Parameters.Add(Uuids("order_lines", orderLineIds.Select(l => (Guid?)l).ToArray()));
        }
    }

    private static async Task<IReadOnlyList<BalanceRow>> ReadBalancesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<BalanceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BalanceRow
            {
                Id = reader.GetGuid(0),
                Key = new BalanceKey(reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)),
                OnHand = reader.GetDecimal(4),
                Reserved = reader.GetDecimal(5),
                AllowNegative = reader.GetBoolean(6),
                IsTransit = reader.GetBoolean(7),
            });
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ReservationRow>> ReadReservationsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<ReservationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ReservationRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetString(9)));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlCommand Command(string sql) => new(sql, unitOfWork.Connection, Transaction);

    private static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value is { } v ? v : DBNull.Value };

    private static NpgsqlParameter Uuids(string name, Guid?[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values };

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}

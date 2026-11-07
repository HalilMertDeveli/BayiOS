using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Inventory.Infrastructure.Persistence;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>
/// Database-level guarantees of the inventory and warehouse schemas, exercised as the runtime role
/// <c>bayios_app</c> (RLS applies) and as superuser (only triggers/constraints apply): tenant isolation (I13),
/// append-only ledger (I4) and the negative-stock CHECK backstop (I3).
/// </summary>
public sealed class InventoryDatabaseTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] InventoryTables = ["inventory_balances", "inventory_movements", "stock_reservations", "stock_thresholds"];
    private static readonly string[] WarehouseTables =
        ["picking_task_lines", "picking_tasks", "shipment_lines", "shipment_package_items", "shipment_packages", "shipments", "warehouse_locations", "warehouses"];

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _variant = Guid.CreateVersion7();
    private readonly Guid _warehouseA = Guid.CreateVersion7();
    private readonly Guid _warehouseB = Guid.CreateVersion7();
    private readonly Guid _movementA = Guid.CreateVersion7();
    private readonly Guid _movementB = Guid.CreateVersion7();
    private readonly Guid _balanceA = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO warehouse.warehouses (id, tenant_id, code, name, uses_locations, is_default, status, row_version, created_at)
            VALUES (@wa, @a, 'MAIN', 'A', false, true, 'active', 1, now()), (@wb, @b, 'MAIN', 'B', false, true, 'active', 1, now());
            INSERT INTO inventory.inventory_movements (id, tenant_id, variant_id, warehouse_id, type, quantity, source_type, balance_after, occurred_at)
            VALUES (@ma, @a, @v, @wa, 'receipt', 5, 'receipt', 5, now()), (@mb, @b, @v, @wb, 'receipt', 7, 'receipt', 7, now());
            INSERT INTO inventory.inventory_balances (id, tenant_id, variant_id, warehouse_id, on_hand, reserved, allow_negative, version, created_at)
            VALUES (@ba, @a, @v, @wa, 5, 0, false, 1, now()), (gen_random_uuid(), @b, @v, @wb, 7, 0, false, 1, now());
            """,
            connection);
        command.Parameters.AddWithValue("a", _tenantA);
        command.Parameters.AddWithValue("b", _tenantB);
        command.Parameters.AddWithValue("v", _variant);
        command.Parameters.AddWithValue("wa", _warehouseA);
        command.Parameters.AddWithValue("wb", _warehouseB);
        command.Parameters.AddWithValue("ma", _movementA);
        command.Parameters.AddWithValue("mb", _movementB);
        command.Parameters.AddWithValue("ba", _balanceA);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rls_shows_only_the_current_tenants_stock()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        Assert.Equal([_movementA], await session.Ids($"SELECT id FROM inventory.inventory_movements WHERE variant_id = '{_variant}'"));
        Assert.Equal([_balanceA], await session.Ids($"SELECT id FROM inventory.inventory_balances WHERE variant_id = '{_variant}'"));
        Assert.Equal([_warehouseA], await session.Ids($"SELECT id FROM warehouse.warehouses WHERE id IN ('{_warehouseA}', '{_warehouseB}')"));
    }

    [Fact]
    public async Task Rls_rejects_writes_into_another_tenant()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
                $"INSERT INTO inventory.inventory_movements (id, tenant_id, variant_id, warehouse_id, type, quantity, source_type, balance_after, occurred_at) VALUES (gen_random_uuid(), '{_tenantB}', '{_variant}', '{_warehouseB}', 'receipt', 1, 'receipt', 8, now())"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            Assert.Equal(0, await session.Execute($"UPDATE inventory.inventory_balances SET on_hand = 999 WHERE tenant_id = '{_tenantB}'"));
            Assert.Equal(0, await session.Execute($"UPDATE warehouse.warehouses SET name = 'hacked' WHERE id = '{_warehouseB}'"));
        }

        await using var asB = await AppSession.BeginAsync(fixture, _tenantB);
        Assert.Equal(7m, await asB.Scalar<decimal>($"SELECT on_hand FROM inventory.inventory_balances WHERE variant_id = '{_variant}'"));
    }

    [Fact]
    public async Task Without_tenant_context_nothing_is_visible()
    {
        await using var session = await AppSession.BeginAsync(fixture, tenantId: null);

        foreach (var table in InventoryTables)
        {
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM inventory.{table}"));
        }

        foreach (var table in WarehouseTables)
        {
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM warehouse.{table}"));
        }
    }

    [Theory]
    [InlineData("UPDATE inventory.inventory_movements SET quantity = 500 WHERE id = '{0}'")]
    [InlineData("DELETE FROM inventory.inventory_movements WHERE id = '{0}'")]
    [InlineData("TRUNCATE inventory.inventory_movements")]
    public async Task Runtime_role_cannot_change_or_remove_ledger_rows(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, _movementA)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [InlineData("UPDATE inventory.inventory_movements SET quantity = 500 WHERE id = '{0}'")]
    [InlineData("DELETE FROM inventory.inventory_movements WHERE id = '{0}'")]
    [InlineData("TRUNCATE inventory.inventory_movements")]
    public async Task Ledger_trigger_blocks_changes_even_for_privileged_roles(string sql)
    {
        // The superuser bypasses grants and RLS: only the append-only trigger stands in the way.
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, _movementA), connection, transaction);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_role_cannot_delete_balances_reservations_or_warehouses()
    {
        foreach (var sql in new[]
        {
            $"DELETE FROM inventory.inventory_balances WHERE id = '{_balanceA}'",
            "DELETE FROM inventory.stock_reservations",
            $"DELETE FROM warehouse.warehouses WHERE id = '{_warehouseA}'",
        })
        {
            await using var session = await AppSession.BeginAsync(fixture, _tenantA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE inventory.inventory_balances SET on_hand = -1 WHERE id = '{0}'")]
    [InlineData("UPDATE inventory.inventory_balances SET reserved = 6 WHERE id = '{0}'")]
    [InlineData("UPDATE inventory.inventory_balances SET reserved = -1, allow_negative = true WHERE id = '{0}'")]
    public async Task Balance_checks_reject_negative_or_over_reserved_stock(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, _balanceA)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Ledger_rejects_movements_whose_sign_contradicts_the_type()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
            $"INSERT INTO inventory.inventory_movements (id, tenant_id, variant_id, warehouse_id, type, quantity, source_type, balance_after, occurred_at) VALUES (gen_random_uuid(), '{_tenantA}', '{_variant}', '{_warehouseA}', 'shipment_out', 3, 'shipment', 8, now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Every_inventory_and_warehouse_table_has_forced_rls_and_is_owned_by_the_migrator()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname || '.' || c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('inventory', 'warehouse') AND c.relkind = 'r' AND c.relname <> '__ef_migrations_history'
            ORDER BY 1
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(reader.GetString(0));
            Assert.True(reader.GetBoolean(1), $"{reader.GetString(0)}: RLS not enabled");
            Assert.True(reader.GetBoolean(2), $"{reader.GetString(0)}: RLS not forced");
            Assert.Equal("bayios_migrator", reader.GetString(3));
        }

        Assert.Equal(
            [.. InventoryTables.Select(t => "inventory." + t), .. WarehouseTables.Select(t => "warehouse." + t)],
            tables);
    }

    [Fact]
    public async Task Ef_query_filters_isolate_tenants_even_without_rls()
    {
        var context = new RequestContext { TenantId = _tenantA };
        var inventoryOptions = new DbContextOptionsBuilder<InventoryDbContext>();
        InventoryDbContext.Configure(inventoryOptions, fixture.SuperuserConnectionString);
        await using var inventory = new InventoryDbContext(inventoryOptions.Options, context);
        var warehouseOptions = new DbContextOptionsBuilder<WarehouseDbContext>();
        WarehouseDbContext.Configure(warehouseOptions, fixture.SuperuserConnectionString);
        await using var warehouse = new WarehouseDbContext(warehouseOptions.Options, context);

        Assert.Equal([_movementA], await inventory.Movements.Where(m => m.VariantId == _variant).Select(m => m.Id).ToListAsync(Ct));
        Assert.Equal([_tenantA], await inventory.Balances.Where(b => b.VariantId == _variant).Select(b => b.TenantId).ToListAsync(Ct));
        Assert.Equal([_warehouseA], await warehouse.Warehouses.Where(w => w.Id == _warehouseA || w.Id == _warehouseB).Select(w => w.Id).ToListAsync(Ct));

        var unfiltered = inventory.Model.GetEntityTypes().Concat(warehouse.Model.GetEntityTypes())
            .Where(e => e.FindProperty("TenantId") is null || e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name);
        Assert.Empty(unfiltered);
    }

    /// <summary>A transaction as the runtime role with the same settings the UnitOfWork applies.</summary>
    private sealed class AppSession : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private AppSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static async Task<AppSession> BeginAsync(ApiFixture fixture, Guid? tenantId)
        {
            var connection = new NpgsqlConnection(fixture.AppConnectionString);
            await connection.OpenAsync(Ct);
            var transaction = await connection.BeginTransactionAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = tenantId?.ToString() ?? string.Empty });
            await command.ExecuteNonQueryAsync(Ct);
            return new AppSession(connection, transaction);
        }

        public async Task<List<Guid>> Ids(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var ids = new List<Guid>();
            while (await reader.ReadAsync(Ct))
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }

        public async Task<T> Scalar<T>(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            return (T)(await command.ExecuteScalarAsync(Ct))!;
        }

        public async Task<int> Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            return await command.ExecuteNonQueryAsync(Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

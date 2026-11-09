using System.Globalization;
using BayiOS.IntegrationTests.Infrastructure;
using Npgsql;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>
/// Database-level guarantees of the fulfilment tables as the runtime role <c>bayios_app</c>: forced RLS tenant
/// isolation (EF filter out of the picture), no DELETE of tasks/shipments, write-once packages, the quantity CHECKs and
/// the "one active pick task / one open shipment per order" unique indexes.
/// </summary>
public sealed class FulfillmentDatabaseTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Tables =
        ["picking_task_lines", "picking_tasks", "shipment_lines", "shipment_package_items", "shipment_packages", "shipments"];

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _warehouseA = Guid.CreateVersion7();
    private readonly Guid _warehouseB = Guid.CreateVersion7();
    private readonly Guid _orderA = Guid.CreateVersion7();
    private readonly Guid _taskA = Guid.CreateVersion7();
    private readonly Guid _taskB = Guid.CreateVersion7();
    private readonly Guid _lineA = Guid.CreateVersion7();
    private readonly Guid _shipmentA = Guid.CreateVersion7();
    private readonly Guid _shipmentLineA = Guid.CreateVersion7();
    private readonly Guid _packageA = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO warehouse.warehouses (id, tenant_id, code, name, uses_locations, is_default, status, row_version, created_at)
            VALUES (@wa, @a, 'MAIN', 'A', false, true, 'active', 1, now()), (@wb, @b, 'MAIN', 'B', false, true, 'active', 1, now());
            INSERT INTO warehouse.picking_tasks (id, tenant_id, type, order_id, order_number, warehouse_id, status, row_version, created_at)
            VALUES (@ta, @a, 'pick', @oa, 'SIP-2026-000001', @wa, 'open', 1, now()),
                   (@tb, @b, 'pick', gen_random_uuid(), 'SIP-2026-000001', @wb, 'open', 1, now());
            INSERT INTO warehouse.picking_task_lines (id, tenant_id, picking_task_id, sequence, order_line_id, variant_id, sku, name, unit, qty_to_pick, qty_picked, qty_short)
            VALUES (@la, @a, @ta, 1, gen_random_uuid(), gen_random_uuid(), 'LED-1', 'Sentetik LED', 'adet', 5, 0, 0);
            INSERT INTO warehouse.shipments (id, tenant_id, number, order_id, order_number, picking_task_id, warehouse_id, status, row_version, created_at)
            VALUES (@sa, @a, 'SIP-2026-000001-S1', @oa, 'SIP-2026-000001', @ta, @wa, 'draft', 1, now());
            INSERT INTO warehouse.shipment_lines (id, tenant_id, shipment_id, order_line_id, variant_id, sku, name, unit, picked_quantity, quantity, short_quantity)
            VALUES (@sla, @a, @sa, gen_random_uuid(), gen_random_uuid(), 'LED-1', 'Sentetik LED', 'adet', 2, 2, 0);
            INSERT INTO warehouse.shipment_packages (id, tenant_id, shipment_id, package_no) VALUES (@pa, @a, @sa, 1);
            INSERT INTO warehouse.shipment_package_items (id, tenant_id, package_id, shipment_line_id, quantity) VALUES (gen_random_uuid(), @a, @pa, @sla, 2);
            """,
            connection);
        command.Parameters.AddWithValue("a", _tenantA);
        command.Parameters.AddWithValue("b", _tenantB);
        command.Parameters.AddWithValue("wa", _warehouseA);
        command.Parameters.AddWithValue("wb", _warehouseB);
        command.Parameters.AddWithValue("oa", _orderA);
        command.Parameters.AddWithValue("ta", _taskA);
        command.Parameters.AddWithValue("tb", _taskB);
        command.Parameters.AddWithValue("la", _lineA);
        command.Parameters.AddWithValue("sa", _shipmentA);
        command.Parameters.AddWithValue("sla", _shipmentLineA);
        command.Parameters.AddWithValue("pa", _packageA);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rls_shows_and_changes_only_the_current_tenants_rows()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantB))
        {
            Assert.Equal(1L, await session.Scalar<long>($"SELECT count(*) FROM warehouse.picking_tasks WHERE id IN ('{_taskA}', '{_taskB}')"));
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM warehouse.picking_task_lines WHERE picking_task_id = '{_taskA}'"));
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM warehouse.shipments WHERE id = '{_shipmentA}'"));
            Assert.Equal(0, await session.Execute($"UPDATE warehouse.picking_tasks SET assigned_to = gen_random_uuid() WHERE id = '{_taskA}'"));
            Assert.Equal(0, await session.Execute($"UPDATE warehouse.picking_task_lines SET qty_picked = 5 WHERE id = '{_lineA}'"));
            Assert.Equal(0, await session.Execute($"UPDATE warehouse.shipments SET status = 'confirmed', shipped_at = now(), confirmed_by = gen_random_uuid() WHERE id = '{_shipmentA}'"));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
                $"INSERT INTO warehouse.picking_tasks (id, tenant_id, type, order_id, order_number, warehouse_id, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'pick', gen_random_uuid(), 'X', '{_warehouseA}', 'open', 1, now())"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using var none = await AppSession.BeginAsync(fixture, tenantId: null);
        foreach (var table in Tables)
        {
            Assert.Equal(0L, await none.Scalar<long>($"SELECT count(*) FROM warehouse.{table}"));
        }
    }

    [Theory]
    [InlineData("DELETE FROM warehouse.picking_tasks WHERE id = '{0}'")]
    [InlineData("DELETE FROM warehouse.picking_task_lines WHERE picking_task_id = '{0}'")]
    [InlineData("DELETE FROM warehouse.shipments WHERE picking_task_id = '{0}'")]
    [InlineData("DELETE FROM warehouse.shipment_package_items")]
    [InlineData("UPDATE warehouse.shipment_package_items SET quantity = 9")]
    [InlineData("TRUNCATE warehouse.picking_tasks CASCADE")]
    public async Task Runtime_role_cannot_delete_tasks_or_shipments_or_rewrite_packages(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(CultureInfo.InvariantCulture, sql, _taskA)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [InlineData("UPDATE warehouse.picking_task_lines SET qty_picked = 4, qty_short = 2 WHERE id = '{0}'")]
    [InlineData("UPDATE warehouse.picking_task_lines SET qty_picked = -1 WHERE id = '{0}'")]
    [InlineData("UPDATE warehouse.picking_task_lines SET qty_to_pick = -1 WHERE id = '{0}'")]
    [InlineData("UPDATE warehouse.picking_task_lines SET qty_picked = 1, qty_to_pick = 0 WHERE id = '{0}'")]
    [InlineData("UPDATE warehouse.picking_tasks SET priority = 'asap' WHERE id = '{3}'")]
    [InlineData("UPDATE warehouse.shipment_lines SET quantity = 3 WHERE id = '{1}'")]
    [InlineData("UPDATE warehouse.shipment_lines SET packed_quantity = 3 WHERE id = '{1}'")]
    [InlineData("UPDATE warehouse.shipments SET status = 'confirmed' WHERE id = '{2}'")]
    public async Task Check_constraints_backstop_the_quantity_rules(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(CultureInfo.InvariantCulture, sql, _lineA, _shipmentLineA, _shipmentA, _taskA)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task One_active_pick_task_and_one_open_shipment_per_order()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
                $"INSERT INTO warehouse.picking_tasks (id, tenant_id, type, order_id, order_number, warehouse_id, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'pick', '{_orderA}', 'X', '{_warehouseA}', 'in_progress', 1, now())"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        }

        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
                $"INSERT INTO warehouse.shipments (id, tenant_id, number, order_id, order_number, warehouse_id, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'SIP-2026-000001-S2', '{_orderA}', 'X', '{_warehouseA}', 'packed', 1, now())"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        }

        // Putaway tasks and finished/cancelled documents do not count.
        await using var allowed = await AppSession.BeginAsync(fixture, _tenantA);
        Assert.Equal(1, await allowed.Execute(
            $"INSERT INTO warehouse.picking_tasks (id, tenant_id, type, order_id, order_number, warehouse_id, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'putaway', '{_orderA}', 'X', '{_warehouseA}', 'open', 1, now())"));
        Assert.Equal(1, await allowed.Execute(
            $"INSERT INTO warehouse.shipments (id, tenant_id, number, order_id, order_number, warehouse_id, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'SIP-2026-000001-S3', '{_orderA}', 'X', '{_warehouseA}', 'cancelled', 1, now())"));
    }

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

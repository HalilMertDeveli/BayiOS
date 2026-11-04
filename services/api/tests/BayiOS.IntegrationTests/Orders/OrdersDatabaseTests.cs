using System.Globalization;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
using Npgsql;

namespace BayiOS.IntegrationTests.Orders;

/// <summary>
/// Database-level guarantees of the orders schema as the runtime role <c>bayios_app</c>: forced RLS tenant isolation
/// (with the EF filter out of the picture), no DELETE of orders, append-only history and shipments, only draft lines
/// deletable, and CHECK backstops for the line invariants (I7, one discount type).
/// </summary>
public sealed class OrdersDatabaseTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Tables =
        ["order_lines", "order_number_sequences", "order_shipment_lines", "order_shipments", "order_status_history", "orders"];

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _orderA = Guid.CreateVersion7();
    private readonly Guid _orderB = Guid.CreateVersion7();
    private readonly Guid _draftA = Guid.CreateVersion7();
    private readonly Guid _lineA = Guid.CreateVersion7();
    private readonly Guid _draftLineA = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO orders.orders (id, tenant_id, number, customer_id, warehouse_id, status, fulfillment_status, source, currency,
                                       prices_include_tax, price_date, subtotal, discount_total, tax_total, grand_total, row_version, created_at)
            VALUES (@oa, @a, 'SIP-2026-000001', gen_random_uuid(), gen_random_uuid(), 'approved', 'none', 'manual', 'TRY', false, '2026-11-05', 10, 0, 2, 12, 1, now()),
                   (@da, @a, 'SIP-2026-000002', gen_random_uuid(), gen_random_uuid(), 'draft', 'none', 'manual', 'TRY', false, '2026-11-05', 0, 0, 0, 0, 1, now()),
                   (@ob, @b, 'SIP-2026-000001', gen_random_uuid(), gen_random_uuid(), 'draft', 'none', 'manual', 'TRY', false, '2026-11-05', 0, 0, 0, 0, 1, now());
            INSERT INTO orders.order_lines (id, tenant_id, order_id, line_no, variant_id, sku, name, unit, quantity, unit_price, price_overridden,
                                            tax_rate, line_subtotal, line_discount, line_total, qty_reserved, qty_shipped, qty_cancelled, created_at)
            VALUES (@la, @a, @oa, 1, gen_random_uuid(), 'LED-1', 'Sentetik LED', 'adet', 1, 10, false, 20, 10, 0, 10, 1, 0, 0, now()),
                   (@dla, @a, @da, 1, gen_random_uuid(), 'LED-1', 'Sentetik LED', 'adet', 1, NULL, false, 20, NULL, NULL, NULL, 0, 0, 0, now());
            INSERT INTO orders.order_status_history (id, tenant_id, order_id, action, from_status, to_status, fulfillment_status, changed_at)
            VALUES (gen_random_uuid(), @a, @oa, 'create', NULL, 'draft', 'none', now()), (gen_random_uuid(), @b, @ob, 'create', NULL, 'draft', 'none', now());
            """,
            connection);
        command.Parameters.AddWithValue("a", _tenantA);
        command.Parameters.AddWithValue("b", _tenantB);
        command.Parameters.AddWithValue("oa", _orderA);
        command.Parameters.AddWithValue("ob", _orderB);
        command.Parameters.AddWithValue("da", _draftA);
        command.Parameters.AddWithValue("la", _lineA);
        command.Parameters.AddWithValue("dla", _draftLineA);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_orders_table_has_forced_rls_and_is_owned_by_the_migrator()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'orders' AND c.relkind = 'r' AND c.relname <> '__ef_migrations_history'
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

        Assert.Equal(Tables, tables);
    }

    [Fact]
    public async Task Rls_shows_and_changes_only_the_current_tenants_rows()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            Assert.Equal(2L, await session.Scalar<long>($"SELECT count(*) FROM orders.orders WHERE id IN ('{_orderA}', '{_orderB}', '{_draftA}')"));
            Assert.Equal(1L, await session.Scalar<long>($"SELECT count(*) FROM orders.order_status_history WHERE order_id IN ('{_orderA}', '{_orderB}')"));
            Assert.Equal(0, await session.Execute($"UPDATE orders.orders SET status = 'cancelled', cancel_reason = 'x' WHERE id = '{_orderB}'"));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
                $"INSERT INTO orders.order_status_history (id, tenant_id, order_id, action, to_status, fulfillment_status, changed_at) VALUES (gen_random_uuid(), '{_tenantB}', '{_orderB}', 'submit', 'pending_approval', 'none', now())"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using var none = await AppSession.BeginAsync(fixture, tenantId: null);
        foreach (var table in Tables)
        {
            Assert.Equal(0L, await none.Scalar<long>($"SELECT count(*) FROM orders.{table}"));
        }
    }

    [Theory]
    [InlineData("DELETE FROM orders.orders WHERE id = '{0}'")]
    [InlineData("DELETE FROM orders.order_status_history WHERE order_id = '{0}'")]
    [InlineData("UPDATE orders.order_status_history SET reason = 'x' WHERE order_id = '{0}'")]
    [InlineData("UPDATE orders.order_shipments SET carrier = 'x' WHERE order_id = '{0}'")]
    [InlineData("TRUNCATE orders.orders CASCADE")]
    public async Task Runtime_role_cannot_delete_orders_or_rewrite_history(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(CultureInfo.InvariantCulture, sql, _orderA)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Only_draft_lines_can_be_deleted()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute($"DELETE FROM orders.order_lines WHERE id = '{_lineA}'"));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        }

        await using var draft = await AppSession.BeginAsync(fixture, _tenantA);
        Assert.Equal(1, await draft.Execute($"DELETE FROM orders.order_lines WHERE id = '{_draftLineA}'"));
    }

    [Theory]
    [InlineData("UPDATE orders.order_lines SET qty_shipped = 1 WHERE id = '{0}'")]
    [InlineData("UPDATE orders.order_lines SET qty_reserved = -1 WHERE id = '{0}'")]
    [InlineData("UPDATE orders.order_lines SET discount_percent = 5, discount_amount = 1 WHERE id = '{0}'")]
    [InlineData("UPDATE orders.order_lines SET line_total = 999 WHERE id = '{0}'")]
    [InlineData("UPDATE orders.order_lines SET unit_price = NULL WHERE id = '{0}'")]
    [InlineData("UPDATE orders.order_lines SET quantity = 0 WHERE id = '{0}'")]
    [InlineData("UPDATE orders.orders SET status = 'cancelled' WHERE id = '{1}'")]
    [InlineData("UPDATE orders.orders SET status = 'teleported' WHERE id = '{1}'")]
    public async Task Check_constraints_back_the_line_invariants(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(CultureInfo.InvariantCulture, sql, _lineA, _orderA)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Order_numbers_and_line_numbers_are_unique_per_tenant()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
                $"UPDATE orders.orders SET number = 'SIP-2026-000001' WHERE id = '{_draftA}'"));
            Assert.Equal((PostgresErrorCodes.UniqueViolation, OrdersDbContext.NumberUniqueIndex), (ex.SqlState, ex.ConstraintName));
        }

        await using var lines = await AppSession.BeginAsync(fixture, _tenantA);
        await lines.Execute(
            $"INSERT INTO orders.order_lines (id, tenant_id, order_id, line_no, variant_id, sku, name, unit, quantity, price_overridden, tax_rate, qty_reserved, qty_shipped, qty_cancelled, created_at) VALUES (gen_random_uuid(), '{_tenantA}', '{_draftA}', 1, gen_random_uuid(), 'X', 'X', 'adet', 1, false, 20, 0, 0, 0, now())");
        // Deferred: the duplicate line number is only rejected at commit.
        var commit = await Assert.ThrowsAsync<PostgresException>(lines.Commit);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, OrdersDbContext.LineNoUniqueConstraint), (commit.SqlState, commit.ConstraintName));
    }

    [Fact]
    public async Task Money_and_quantities_are_numeric_columns()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        Assert.Equal(
            "numeric(18,4)|numeric(18,3)|numeric(5,2)|numeric(18,4)|numeric(18,3)",
            await session.Scalar<string>(
                """
                SELECT string_agg(format_type(a.atttypid, a.atttypmod), '|' ORDER BY x.ord)
                FROM (VALUES ('orders.order_lines'::regclass, 'unit_price', 1), ('orders.order_lines'::regclass, 'quantity', 2),
                             ('orders.order_lines'::regclass, 'discount_percent', 3), ('orders.orders'::regclass, 'grand_total', 4),
                             ('orders.order_shipment_lines'::regclass, 'quantity', 5)) x(rel, col, ord)
                JOIN pg_attribute a ON a.attrelid = x.rel AND a.attname = x.col
                """));
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

        public Task Commit() => _transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

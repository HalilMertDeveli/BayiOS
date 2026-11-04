using System.Globalization;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.IntegrationTests.Customers;

/// <summary>
/// Database-level guarantees of the customers and pricing schemas as the runtime role <c>bayios_app</c>: forced RLS
/// tenant isolation, no DELETE where the domain archives, numeric money/quantity columns and CHECK backstops.
/// </summary>
public sealed class CustomersPricingDatabaseTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] CustomersTables = ["customer_addresses", "customers", "dealer_groups"];
    private static readonly string[] PricingTables = ["price_list_items", "price_lists"];

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _variant = Guid.CreateVersion7();
    private readonly Guid _listA = Guid.CreateVersion7();
    private readonly Guid _listB = Guid.CreateVersion7();
    private readonly Guid _itemA = Guid.CreateVersion7();
    private readonly Guid _customerA = Guid.CreateVersion7();
    private readonly Guid _customerB = Guid.CreateVersion7();
    private readonly Guid _groupA = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pricing.price_lists (id, tenant_id, name, kind, currency, is_default, prices_include_tax, status, row_version, created_at)
            VALUES (@la, @a, 'Perakende', 'sales', 'TRY', true, false, 'active', 1, now()), (@lb, @b, 'Perakende', 'sales', 'TRY', true, false, 'active', 1, now());
            INSERT INTO pricing.price_list_items (id, tenant_id, price_list_id, variant_id, min_quantity, unit_price, row_version, created_at)
            VALUES (@ia, @a, @la, @v, 1, 100.1234, 1, now()), (gen_random_uuid(), @b, @lb, @v, 1, 200, 1, now());
            INSERT INTO customers.dealer_groups (id, tenant_id, name, price_list_id, status, row_version, created_at)
            VALUES (@ga, @a, 'Bayi', @la, 'active', 1, now());
            INSERT INTO customers.customers (id, tenant_id, code, name, dealer_group_id, status, row_version, created_at)
            VALUES (@ca, @a, 'C1', 'A Müşteri', @ga, 'active', 1, now()), (@cb, @b, 'C1', 'B Müşteri', NULL, 'active', 1, now());
            INSERT INTO customers.customer_addresses (id, tenant_id, customer_id, type, is_default, line1, city, country, row_version, created_at)
            VALUES (gen_random_uuid(), @a, @ca, 'shipping', true, 'Adres A', 'İstanbul', 'TR', 1, now()),
                   (gen_random_uuid(), @b, @cb, 'shipping', true, 'Adres B', 'Ankara', 'TR', 1, now());
            """,
            connection);
        command.Parameters.AddWithValue("a", _tenantA);
        command.Parameters.AddWithValue("b", _tenantB);
        command.Parameters.AddWithValue("v", _variant);
        command.Parameters.AddWithValue("la", _listA);
        command.Parameters.AddWithValue("lb", _listB);
        command.Parameters.AddWithValue("ia", _itemA);
        command.Parameters.AddWithValue("ca", _customerA);
        command.Parameters.AddWithValue("cb", _customerB);
        command.Parameters.AddWithValue("ga", _groupA);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rls_shows_only_the_current_tenants_rows()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        Assert.Equal([_listA], await session.Ids($"SELECT id FROM pricing.price_lists WHERE id IN ('{_listA}', '{_listB}')"));
        Assert.Equal([_itemA], await session.Ids($"SELECT id FROM pricing.price_list_items WHERE variant_id = '{_variant}'"));
        Assert.Equal([_customerA], await session.Ids($"SELECT id FROM customers.customers WHERE id IN ('{_customerA}', '{_customerB}')"));
        Assert.Equal(1L, await session.Scalar<long>($"SELECT count(*) FROM customers.customer_addresses WHERE customer_id IN ('{_customerA}', '{_customerB}')"));
    }

    [Fact]
    public async Task Rls_rejects_writes_into_another_tenant()
    {
        foreach (var sql in new[]
        {
            $"INSERT INTO pricing.price_list_items (id, tenant_id, price_list_id, variant_id, min_quantity, unit_price, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantB}', '{_listB}', '{_variant}', 5, 1, 1, now())",
            $"INSERT INTO customers.customers (id, tenant_id, code, name, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantB}', 'X', 'X', 'active', 1, now())",
        })
        {
            await using var session = await AppSession.BeginAsync(fixture, _tenantA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            Assert.Equal(0, await session.Execute($"UPDATE pricing.price_list_items SET unit_price = 0 WHERE tenant_id = '{_tenantB}'"));
            Assert.Equal(0, await session.Execute($"UPDATE customers.customers SET name = 'hacked' WHERE id = '{_customerB}'"));
            Assert.Equal(0, await session.Execute($"DELETE FROM customers.customer_addresses WHERE tenant_id = '{_tenantB}'"));
        }

        await using var asB = await AppSession.BeginAsync(fixture, _tenantB);
        Assert.Equal(200m, await asB.Scalar<decimal>($"SELECT unit_price FROM pricing.price_list_items WHERE variant_id = '{_variant}'"));
        Assert.Equal("B Müşteri", await asB.Scalar<string>($"SELECT name FROM customers.customers WHERE id = '{_customerB}'"));
    }

    [Fact]
    public async Task Without_tenant_context_nothing_is_visible()
    {
        await using var session = await AppSession.BeginAsync(fixture, tenantId: null);

        foreach (var table in CustomersTables)
        {
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM customers.{table}"));
        }

        foreach (var table in PricingTables)
        {
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM pricing.{table}"));
        }
    }

    [Fact]
    public async Task Runtime_role_cannot_delete_customers_groups_or_price_lists()
    {
        foreach (var sql in new[]
        {
            $"DELETE FROM customers.customers WHERE id = '{_customerA}'",
            $"DELETE FROM customers.dealer_groups WHERE id = '{_groupA}'",
            $"DELETE FROM pricing.price_lists WHERE id = '{_listA}'",
            "TRUNCATE customers.customers",
        })
        {
            await using var session = await AppSession.BeginAsync(fixture, _tenantA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pricing.price_list_items SET unit_price = -0.0001 WHERE id = '{0}'")]
    [InlineData("UPDATE pricing.price_list_items SET min_quantity = 0 WHERE id = '{0}'")]
    [InlineData("UPDATE pricing.price_lists SET currency = 'tr' WHERE id = '{1}'")]
    [InlineData("UPDATE pricing.price_lists SET discount_percent = 10 WHERE id = '{1}'")]
    [InlineData("UPDATE pricing.price_lists SET kind = 'purchase' WHERE id = '{1}'")]
    [InlineData("UPDATE customers.customers SET tax_number = '12AB' WHERE id = '{2}'")]
    public async Task Check_constraints_back_the_application_rules(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(string.Format(CultureInfo.InvariantCulture, sql, _itemA, _listA, _customerA)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Money_and_quantities_are_numeric_columns()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        Assert.Equal(
            "numeric(18,4)|numeric(18,3)|numeric(5,2)|numeric(18,4)",
            await session.Scalar<string>(
                """
                SELECT string_agg(format_type(a.atttypid, a.atttypmod), '|' ORDER BY x.ord)
                FROM (VALUES ('pricing.price_list_items'::regclass, 'unit_price', 1), ('pricing.price_list_items'::regclass, 'min_quantity', 2),
                             ('pricing.price_lists'::regclass, 'discount_percent', 3), ('customers.customers'::regclass, 'credit_limit', 4)) x(rel, col, ord)
                JOIN pg_attribute a ON a.attrelid = x.rel AND a.attname = x.col
                """));
        Assert.Equal(100.1234m, await session.Scalar<decimal>($"SELECT unit_price FROM pricing.price_list_items WHERE id = '{_itemA}'"));
    }

    [Fact]
    public async Task Every_customers_and_pricing_table_has_forced_rls_and_is_owned_by_the_migrator()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname || '.' || c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('customers', 'pricing') AND c.relkind = 'r' AND c.relname <> '__ef_migrations_history'
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

        Assert.Equal([.. CustomersTables.Select(t => "customers." + t), .. PricingTables.Select(t => "pricing." + t)], tables);
    }

    [Fact]
    public async Task Index_names_mapped_to_problem_codes_exist_verbatim()
    {
        // PostgreSQL truncates identifiers to 63 bytes; a truncated name would silently break the 409 mapping.
        string[] names =
        [
            PricingDbContext.NameUniqueIndex, PricingDbContext.DefaultUniqueIndex, PricingDbContext.ItemUniqueIndex,
            CustomersDbContext.CodeUniqueIndex, CustomersDbContext.GroupNameUniqueIndex, CustomersDbContext.DefaultAddressUniqueIndex,
        ];
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE schemaname IN ('pricing', 'customers') AND indexname = ANY($1)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = names });

        Assert.Equal((long)names.Length, (long)(await command.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task Ef_query_filters_isolate_tenants_even_without_rls()
    {
        var context = new RequestContext { TenantId = _tenantA };
        var pricingOptions = new DbContextOptionsBuilder<PricingDbContext>();
        PricingDbContext.Configure(pricingOptions, fixture.SuperuserConnectionString);
        await using var pricing = new PricingDbContext(pricingOptions.Options, context);
        var customersOptions = new DbContextOptionsBuilder<CustomersDbContext>();
        CustomersDbContext.Configure(customersOptions, fixture.SuperuserConnectionString);
        await using var customers = new CustomersDbContext(customersOptions.Options, context);

        Assert.Equal([_listA], await pricing.PriceLists.Where(l => l.Id == _listA || l.Id == _listB).Select(l => l.Id).ToListAsync(Ct));
        Assert.Equal([_itemA], await pricing.Items.Where(i => i.VariantId == _variant).Select(i => i.Id).ToListAsync(Ct));
        Assert.Equal([_customerA], await customers.Customers.Where(c => c.Id == _customerA || c.Id == _customerB).Select(c => c.Id).ToListAsync(Ct));

        var unfiltered = pricing.Model.GetEntityTypes().Concat(customers.Model.GetEntityTypes())
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

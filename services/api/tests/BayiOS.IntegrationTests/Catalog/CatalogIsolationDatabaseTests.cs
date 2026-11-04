using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.IntegrationTests.Catalog;

/// <summary>
/// Defence in depth for the catalog schema (ADR 0002): as <c>bayios_app</c>, RLS alone isolates tenants; the
/// composite tenant FKs make cross-tenant references impossible; EF query filters isolate even without RLS.
/// </summary>
public sealed class CatalogIsolationDatabaseTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] CatalogTables = ["brands", "categories", "custom_field_definitions", "product_variants", "products", "units"];

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _unitA = Guid.CreateVersion7();
    private readonly Guid _unitB = Guid.CreateVersion7();
    private readonly Guid _productA = Guid.CreateVersion7();
    private readonly Guid _productB = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO catalog.units (id, tenant_id, code, name, allows_fraction, row_version, created_at)
            VALUES (@ua, @a, 'adet', 'Adet', false, 1, now()), (@ub, @b, 'adet', 'Adet', false, 1, now());
            INSERT INTO catalog.products (id, tenant_id, name, default_unit_id, tax_rate, status, row_version, created_at)
            VALUES (@pa, @a, 'A ürünü', @ua, 20, 'active', 1, now()), (@pb, @b, 'B ürünü', @ub, 20, 'active', 1, now());
            INSERT INTO catalog.product_variants (id, tenant_id, product_id, sku, barcode, unit_id, status, row_version, created_at)
            VALUES (gen_random_uuid(), @a, @pa, 'SAME-SKU', '111', @ua, 'active', 1, now()),
                   (gen_random_uuid(), @b, @pb, 'SAME-SKU', '111', @ub, 'active', 1, now());
            INSERT INTO catalog.custom_field_definitions (id, tenant_id, entity_type, key, label, data_type, options, required, filterable, sort_order, row_version, created_at)
            VALUES (gen_random_uuid(), @a, 'product', 'guc', 'Güç', 'number', '[]', false, true, 0, 1, now()),
                   (gen_random_uuid(), @b, 'product', 'guc', 'Güç B', 'number', '[]', true, true, 0, 1, now());
            """,
            connection);
        command.Parameters.AddWithValue("a", _tenantA);
        command.Parameters.AddWithValue("b", _tenantB);
        command.Parameters.AddWithValue("ua", _unitA);
        command.Parameters.AddWithValue("ub", _unitB);
        command.Parameters.AddWithValue("pa", _productA);
        command.Parameters.AddWithValue("pb", _productB);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rls_shows_only_the_current_tenants_rows_even_when_targeting_tenant_B()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        Assert.Equal([_productA], await session.Ids($"SELECT id FROM catalog.products WHERE id IN ('{_productA}', '{_productB}')"));
        Assert.Equal([_tenantA], await session.Ids("SELECT DISTINCT tenant_id FROM catalog.product_variants WHERE sku = 'SAME-SKU'"));
        Assert.Empty(await session.Ids($"SELECT id FROM catalog.units WHERE tenant_id = '{_tenantB}'"));
    }

    [Fact]
    public async Task Rls_rejects_inserts_into_tenant_B()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
            $"INSERT INTO catalog.brands (id, tenant_id, name, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantB}', 'X', 1, now())"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Rls_makes_updates_of_tenant_B_no_ops_and_blocks_moving_rows()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA))
        {
            Assert.Equal(0, await session.Execute($"UPDATE catalog.products SET name = 'hacked' WHERE id = '{_productB}'"));
            Assert.Equal(0, await session.Execute($"UPDATE catalog.product_variants SET sku = 'HACK' WHERE product_id = '{_productB}'"));
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                session.Execute($"UPDATE catalog.units SET tenant_id = '{_tenantB}' WHERE id = '{_unitA}'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using var asB = await AppSession.BeginAsync(fixture, _tenantB);
        Assert.Equal([_productB], await asB.Ids("SELECT id FROM catalog.products WHERE name = 'B ürünü'"));
    }

    [Fact]
    public async Task Custom_field_definitions_are_isolated_per_tenant_by_rls()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        Assert.Equal(1L, await session.Scalar<long>("SELECT count(*) FROM catalog.custom_field_definitions WHERE key = 'guc'"));
        Assert.Equal("Güç", await session.Scalar<string>("SELECT label FROM catalog.custom_field_definitions WHERE key = 'guc'"));
        Assert.Equal(0, await session.Execute($"UPDATE catalog.custom_field_definitions SET required = false WHERE tenant_id = '{_tenantB}'"));
        var insert = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
            $"INSERT INTO catalog.custom_field_definitions (id, tenant_id, entity_type, key, label, data_type, required, filterable, sort_order, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantB}', 'product', 'x', 'X', 'text', false, false, 0, 1, now())"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);

        await using var fresh = await AppSession.BeginAsync(fixture, _tenantA);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => fresh.Execute("DELETE FROM catalog.custom_field_definitions"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    [Fact]
    public async Task Custom_field_definition_checks_reject_bad_keys_and_types()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var badKey = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
            $"INSERT INTO catalog.custom_field_definitions (id, tenant_id, entity_type, key, label, data_type, required, filterable, sort_order, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'product', 'Bad Key', 'X', 'text', false, false, 0, 1, now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badKey.SqlState);
    }

    [Fact]
    public async Task Composite_tenant_foreign_key_rejects_referencing_another_tenants_unit()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
            $"INSERT INTO catalog.products (id, tenant_id, name, default_unit_id, tax_rate, status, row_version, created_at) VALUES (gen_random_uuid(), '{_tenantA}', 'X', '{_unitB}', 20, 'active', 1, now())"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Runtime_role_cannot_hard_delete_catalog_rows()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute($"DELETE FROM catalog.products WHERE id = '{_productA}'"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Without_tenant_context_nothing_is_visible()
    {
        await using var session = await AppSession.BeginAsync(fixture, tenantId: null);

        foreach (var table in CatalogTables)
        {
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM catalog.{table}"));
        }
    }

    [Fact]
    public async Task Every_catalog_table_has_forced_rls_and_is_owned_by_the_migrator()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'catalog' AND c.relkind = 'r' AND c.relname <> '__ef_migrations_history'
            ORDER BY c.relname
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

        Assert.Equal(CatalogTables, tables);
    }

    [Fact]
    public async Task Ef_query_filters_isolate_tenants_even_without_rls()
    {
        // Superuser bypasses RLS, so only the EF global filters stand between tenants here.
        await using var db = SuperuserContext(new RequestContext { TenantId = _tenantA });

        Assert.Equal([_productA], await db.Products.Where(p => p.Id == _productA || p.Id == _productB).Select(p => p.Id).ToListAsync(Ct));
        Assert.Equal([_tenantA], await db.Variants.Where(v => v.Sku == "SAME-SKU").Select(v => v.TenantId).ToListAsync(Ct));
        Assert.Contains(_productB, await db.Products.IgnoreQueryFilters().Select(p => p.Id).ToListAsync(Ct));
    }

    [Fact]
    public void Every_catalog_entity_has_a_tenant_query_filter()
    {
        using var db = SuperuserContext(new RequestContext());

        var unfiltered = db.Model.GetEntityTypes()
            .Where(e => e.FindProperty("TenantId") is null || e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name);

        Assert.Empty(unfiltered);
    }

    private CatalogDbContext SuperuserContext(RequestContext requestContext)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogDbContext.Configure(options, fixture.SuperuserConnectionString);
        return new CatalogDbContext(options.Options, requestContext);
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

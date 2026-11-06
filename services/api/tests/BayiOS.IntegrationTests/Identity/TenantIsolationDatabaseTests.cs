using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Domain;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>
/// Defence in depth below the API (ADR 0002): connected as <c>bayios_app</c>, PostgreSQL RLS alone must hide and
/// protect other tenants' rows; independently, EF global query filters and the SaveChanges guard must do the same.
/// </summary>
public sealed class TenantIsolationDatabaseTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly string[] IdentityTables = ["tenants", "memberships", "user_profiles", "tenant_settings", "invitations"];

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _tenantWithoutSettings = Guid.CreateVersion7();
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.tenants (id, name, slug, status, sector, default_currency, locale, time_zone, row_version, created_at)
            VALUES (@a, 'A', 'rls-' || @a::text, 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now()),
                   (@b, 'B', 'rls-' || @b::text, 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now()),
                   (@c, 'C', 'rls-' || @c::text, 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now());
            INSERT INTO identity.memberships (id, tenant_id, user_id, role, status, created_at)
            VALUES (gen_random_uuid(), @a, @ua, 'owner', 'active', now()),
                   (gen_random_uuid(), @b, @ub, 'owner', 'active', now());
            INSERT INTO identity.user_profiles (user_id, email, created_at)
            VALUES (@ua, 'a@example.test', now()), (@ub, 'b@example.test', now());
            INSERT INTO identity.tenant_settings (tenant_id, negative_stock_policy, sales_can_approve_orders, sales_max_discount_percent,
                show_cost_to_manager, show_cost_to_viewer, staff_can_assign_barcode, staff_can_approve_receipt, require2fa, row_version, created_at)
            VALUES (@a, 'block', true, 0, false, false, false, false, false, 1, now()),
                   (@b, 'block', true, 0, false, false, false, false, false, 1, now());
            """,
            connection);
        command.Parameters.AddWithValue("a", _tenantA);
        command.Parameters.AddWithValue("b", _tenantB);
        command.Parameters.AddWithValue("c", _tenantWithoutSettings);
        command.Parameters.AddWithValue("ua", _userA);
        command.Parameters.AddWithValue("ub", _userB);

        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rls_shows_only_the_current_tenants_rows()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA, _userA);

        Assert.Equal([_tenantA], await session.Ids("SELECT tenant_id FROM identity.tenant_settings"));
        Assert.Equal([_tenantA], await session.Ids("SELECT id FROM identity.tenants"));
        Assert.Equal([_tenantA], await session.Ids("SELECT tenant_id FROM identity.memberships"));
        Assert.Equal([_userA], await session.Ids("SELECT user_id FROM identity.user_profiles"));
    }

    [Fact]
    public async Task Rls_hides_rows_even_when_the_query_targets_tenant_B_explicitly()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA, _userA);

        Assert.Empty(await session.Ids($"SELECT tenant_id FROM identity.tenant_settings WHERE tenant_id = '{_tenantB}'"));
        Assert.Empty(await session.Ids($"SELECT id FROM identity.tenants WHERE id = '{_tenantB}'"));
        Assert.Empty(await session.Ids($"SELECT user_id FROM identity.user_profiles WHERE user_id = '{_userB}'"));
    }

    [Theory]
    [InlineData("INSERT INTO identity.memberships (id, tenant_id, user_id, role, status, created_at) VALUES (gen_random_uuid(), '{B}', '{USER}', 'owner', 'active', now())")]
    [InlineData("INSERT INTO identity.tenants (id, name, slug, status, sector, default_currency, locale, time_zone, row_version, created_at) VALUES (gen_random_uuid(), 'X', 'rls-x-' || gen_random_uuid(), 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now())")]
    [InlineData("INSERT INTO identity.tenant_settings (tenant_id, negative_stock_policy, sales_can_approve_orders, sales_max_discount_percent, show_cost_to_manager, show_cost_to_viewer, staff_can_assign_barcode, staff_can_approve_receipt, require2fa, row_version, created_at) VALUES ('{C}', 'allow', true, 0, false, false, false, false, false, 1, now())")]
    [InlineData("INSERT INTO identity.user_profiles (user_id, created_at) VALUES (gen_random_uuid(), now())")]
    public async Task Rls_rejects_inserts_for_another_tenant(string sql)
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA, _userA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => session.Execute(
            sql.Replace("{B}", _tenantB.ToString()).Replace("{C}", _tenantWithoutSettings.ToString()).Replace("{USER}", _userA.ToString())));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); // "new row violates row-level security policy"
    }

    [Fact]
    public async Task Rls_makes_updates_and_deletes_of_tenant_B_no_ops()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA, _userA))
        {
            Assert.Equal(0, await session.Execute($"UPDATE identity.tenant_settings SET require2fa = true WHERE tenant_id = '{_tenantB}'"));
            Assert.Equal(0, await session.Execute($"UPDATE identity.tenants SET name = 'hacked' WHERE id = '{_tenantB}'"));
            Assert.Equal(0, await session.Execute($"DELETE FROM identity.memberships WHERE tenant_id = '{_tenantB}'"));
            await session.CommitAsync();
        }

        await using var asB = await AppSession.BeginAsync(fixture, _tenantB, _userB);
        Assert.Equal([_tenantB], await asB.Ids("SELECT tenant_id FROM identity.tenant_settings WHERE require2fa = false"));
        Assert.Equal([_tenantB], await asB.Ids("SELECT id FROM identity.tenants WHERE name = 'B'"));
        Assert.Equal([_tenantB], await asB.Ids("SELECT tenant_id FROM identity.memberships"));
    }

    [Fact]
    public async Task Business_settings_of_tenant_B_cannot_be_changed_and_checks_hold()
    {
        await using (var session = await AppSession.BeginAsync(fixture, _tenantA, _userA))
        {
            Assert.Equal(0, await session.Execute($"UPDATE identity.tenant_settings SET default_tax_rate = 1, prices_include_tax_display = true WHERE tenant_id = '{_tenantB}'"));
            Assert.Equal(0, await session.Execute($"UPDATE identity.tenants SET logo_object_path = '{_tenantA}/x.png', default_currency = 'USD' WHERE id = '{_tenantB}'"));
            Assert.Equal(1, await session.Execute($"UPDATE identity.tenant_settings SET default_tax_rate = 10 WHERE tenant_id = '{_tenantA}'"));
            await session.CommitAsync();
        }

        await using (var asB = await AppSession.BeginAsync(fixture, _tenantB, _userB))
        {
            Assert.Equal(20m, await asB.Scalar<decimal>("SELECT default_tax_rate FROM identity.tenant_settings"));
            Assert.Equal("TRY", await asB.Scalar<string>("SELECT default_currency FROM identity.tenants"));
            Assert.Equal(DBNull.Value, await asB.Scalar<object>("SELECT logo_object_path FROM identity.tenants"));
        }

        await using var checks = await AppSession.BeginAsync(fixture, _tenantA, _userA);
        var currency = await Assert.ThrowsAsync<PostgresException>(() => checks.Execute($"UPDATE identity.tenants SET default_currency = 'tl ' WHERE id = '{_tenantA}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, currency.SqlState);
    }

    [Fact]
    public async Task Rls_moving_own_row_to_tenant_B_is_rejected()
    {
        await using var session = await AppSession.BeginAsync(fixture, _tenantA, _userA);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            session.Execute($"UPDATE identity.memberships SET tenant_id = '{_tenantB}' WHERE tenant_id = '{_tenantA}'"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Without_tenant_or_user_context_nothing_is_visible()
    {
        await using var session = await AppSession.BeginAsync(fixture, tenantId: null, userId: null);

        foreach (var table in IdentityTables)
        {
            Assert.Equal(0L, await session.Scalar<long>($"SELECT count(*) FROM identity.{table}"));
        }
    }

    [Fact]
    public async Task Tenant_setting_does_not_leak_to_the_next_transaction_on_a_pooled_connection()
    {
        await using var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString);
        var context = new RequestContext { TenantId = _tenantA, UserId = _userA };

        await using (var unitOfWork = new UnitOfWork(dataSource, context))
        {
            await unitOfWork.BeginAsync(readOnly: false, Ct);
            await unitOfWork.CommitAsync(Ct);
        }

        await using var connection = await dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(current_setting('app.tenant_id', true), ''), (SELECT count(*) FROM identity.tenant_settings)", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        Assert.Equal(string.Empty, reader.GetString(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Runtime_role_cannot_bypass_rls_and_owns_nothing()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT r.rolsuper, r.rolbypassrls,
                   (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'identity' AND pg_get_userbyid(c.relowner) = 'bayios_app')
            FROM pg_roles r WHERE r.rolname = 'bayios_app'
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Every_identity_table_has_forced_rls_and_is_owned_by_the_migrator()
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'identity' AND c.relkind = 'r' AND c.relname <> '__ef_migrations_history'
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

        Assert.Equal(IdentityTables.Order(), tables);
    }

    [Fact]
    public async Task Ef_query_filters_isolate_tenants_even_without_rls()
    {
        // Superuser bypasses RLS, so only the EF global filters stand between tenants here.
        await using var db = SuperuserContext(new RequestContext { TenantId = _tenantA, UserId = _userA });

        Assert.Equal([_tenantA], await db.TenantSettings.Where(s => s.TenantId == _tenantA || s.TenantId == _tenantB).Select(s => s.TenantId).ToListAsync(Ct));
        Assert.Equal([_tenantA], await db.Tenants.Where(t => t.Id == _tenantA || t.Id == _tenantB).Select(t => t.Id).ToListAsync(Ct));
        Assert.DoesNotContain(_tenantB, await db.Memberships.Select(m => m.TenantId).ToListAsync(Ct));
        Assert.Contains(_tenantB, await db.TenantSettings.IgnoreQueryFilters().Select(s => s.TenantId).ToListAsync(Ct));
    }

    [Fact]
    public void Every_entity_with_a_tenant_id_has_a_query_filter()
    {
        using var db = SuperuserContext(new RequestContext());

        var unfiltered = db.Model.GetEntityTypes()
            .Where(e => e.FindProperty("TenantId") is not null || e.ClrType == typeof(Tenant))
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name);

        Assert.Empty(unfiltered);
    }

    [Fact]
    public async Task Save_guard_rejects_writing_another_tenants_row()
    {
        await using var db = SuperuserContext(new RequestContext { TenantId = _tenantA, UserId = _userA });
        db.TenantSettings.Add(TenantSettings.CreateDefault(_tenantWithoutSettings, _userA, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<TenantMismatchException>(() => db.SaveChangesAsync(Ct));
    }

    private IdentityDbContext SuperuserContext(RequestContext requestContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(options, fixture.SuperuserConnectionString);
        return new IdentityDbContext(options.Options, requestContext);
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

        public static async Task<AppSession> BeginAsync(ApiFixture fixture, Guid? tenantId, Guid? userId)
        {
            var connection = new NpgsqlConnection(fixture.AppConnectionString);
            await connection.OpenAsync(Ct);
            var transaction = await connection.BeginTransactionAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)", connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = tenantId?.ToString() ?? string.Empty });
            command.Parameters.Add(new NpgsqlParameter { Value = userId?.ToString() ?? string.Empty });
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

        public Task CommitAsync() => _transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

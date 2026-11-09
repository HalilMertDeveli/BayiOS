using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>
/// Migration <c>NegativeStockBlockOnly</c> (owner decision OD-09): on a separate database migrated to the step before
/// it, tenants with the former warn / allow policies are converted to block — although FORCE RLS hides the rows from
/// the migrator — FORCE RLS is back afterwards and the tightened CHECK refuses the old values.
/// </summary>
public sealed class NegativeStockMigrationTests(ApiFixture fixture)
{
    private const string PreviousMigration = "20261106061941_MemberInvitations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Existing_warn_and_allow_tenants_become_block_and_the_check_refuses_them_afterwards()
    {
        var database = "od09_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(fixture.SuperuserConnectionString, $"CREATE DATABASE {database}");
        try
        {
            var superuser = WithDatabase(fixture.SuperuserConnectionString, database);
            var migrator = WithDatabase(fixture.MigratorConnectionString, database);
            var bootstrap = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "infra", "docker", "initdb", "01-roles.sql"), Ct);
            await ExecuteAsync(superuser, bootstrap);

            await MigrateAsync(migrator, PreviousMigration);
            var (warn, allow, block) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            await ExecuteAsync(superuser, $"""
                INSERT INTO identity.tenants (id, name, slug, status, sector, default_currency, locale, time_zone, row_version, created_at)
                VALUES ('{warn}', 'W', 'od09-{warn}', 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now()),
                       ('{allow}', 'A', 'od09-{allow}', 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now()),
                       ('{block}', 'B', 'od09-{block}', 'active', 'general', 'TRY', 'tr-TR', 'Europe/Istanbul', 1, now());
                INSERT INTO identity.tenant_settings (tenant_id, negative_stock_policy, sales_can_approve_orders, sales_max_discount_percent,
                    show_cost_to_manager, show_cost_to_viewer, staff_can_assign_barcode, staff_can_approve_receipt, require2fa, row_version, created_at)
                VALUES ('{warn}', 'warn', true, 0, false, false, false, false, false, 3, now()),
                       ('{allow}', 'allow', true, 0, false, false, false, false, false, 3, now()),
                       ('{block}', 'block', true, 0, false, false, false, false, false, 3, now());
                """);

            await MigrateAsync(migrator, null);

            Assert.Equal(
                [("allow", "block", 4L), ("block", "block", 3L), ("warn", "block", 4L)],
                await ReadAsync(superuser, $"""
                    SELECT CASE tenant_id WHEN '{warn}' THEN 'warn' WHEN '{allow}' THEN 'allow' ELSE 'block' END AS before,
                           negative_stock_policy, row_version
                    FROM identity.tenant_settings ORDER BY 1
                    """));
            Assert.True(await ScalarAsync<bool>(superuser, "SELECT relforcerowsecurity FROM pg_class WHERE oid = 'identity.tenant_settings'::regclass"));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                superuser, $"UPDATE identity.tenant_settings SET negative_stock_policy = 'warn' WHERE tenant_id = '{block}'"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(fixture.SuperuserConnectionString, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(options, connectionString);
        await using var db = new IdentityDbContext(options.Options, new RequestContext());
        await db.GetService<IMigrator>().MigrateAsync(target, Ct);
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<(string Before, string Policy, long Version)>> ReadAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string, long)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return rows;
    }
}

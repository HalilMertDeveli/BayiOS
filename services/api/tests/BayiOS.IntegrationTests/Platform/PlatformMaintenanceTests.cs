using System.Reflection;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>
/// Platform operations (#89): audit retention through the single reviewed purge path, queue/outbox cleanup, rejection
/// of unregistered job types and sanitized <c>last_error</c>. Runs with the other global-worker tests: the cleanup
/// jobs act on every tenant's rows.
/// </summary>
[Collection(GlobalWorkersTestGroup.Name)]
public class PlatformMaintenanceTests(ApiFixture fixture)
{
    private const string PurgeMarker = "bayios.audit_retention_purge";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- audit retention

    [Fact]
    public async Task Audit_retention_deletes_exactly_the_events_past_two_years_in_batches()
    {
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        await InsertAuditAsync(tenant, "old-1", "now() - interval '2 years' - interval '1 day'");
        await InsertAuditAsync(tenant, "old-2", "now() - interval '3 years'");
        await InsertAuditAsync(other, "old-3", "now() - interval '5 years'");
        await InsertAuditAsync(tenant, "almost", "now() - interval '2 years' + interval '1 day'");
        await InsertAuditAsync(tenant, "recent", "now() - interval '1 hour'");
        await InsertAuditAsync(other, "recent", "now()");

        // One batch of 2 through the function, as the runtime role, outside any tenant context.
        Assert.Equal(2, await AppScalarAsync<int>("SELECT audit.purge_expired_audit_events(interval '730 days', 2)"));

        await RunAsync(AuditRetentionJob.Type, services => services.GetRequiredService<AuditRetentionJob>());

        Assert.Equal("almost,recent", await AuditMarkersAsync(tenant));
        Assert.Equal("recent", await AuditMarkersAsync(other));
        Assert.Equal(0L, await SuperScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE occurred_at < now() - interval '2 years'"));
    }

    [Fact]
    public async Task Audit_purge_function_refuses_a_shorter_retention_or_a_bad_batch()
    {
        var tenant = Guid.NewGuid();
        await InsertAuditAsync(tenant, "eighteen-months", "now() - interval '18 months'");

        foreach (var sql in new[]
        {
            "SELECT audit.purge_expired_audit_events(interval '1 year', 100)",
            "SELECT audit.purge_expired_audit_events(interval '729 days', 100)",
            "SELECT audit.purge_expired_audit_events(NULL, 100)",
            "SELECT audit.purge_expired_audit_events(interval '3 years', 0)",
            "SELECT audit.purge_expired_audit_events(interval '3 years', 10001)",
        })
        {
            await Assert.ThrowsAsync<PostgresException>(() => AppScalarAsync<int>(sql));
        }

        // A longer configured retention keeps rows the 2-year floor would allow.
        await InsertAuditAsync(tenant, "two-and-a-half-years", "now() - interval '30 months'");
        await AppScalarAsync<int>("SELECT audit.purge_expired_audit_events(interval '3 years', 10000)");
        Assert.Equal("eighteen-months,two-and-a-half-years", await AuditMarkersAsync(tenant));
    }

    [Fact]
    public async Task Audit_rows_stay_append_only_for_every_role_outside_the_purge_function()
    {
        var tenant = Guid.NewGuid();
        await InsertAuditAsync(tenant, "ancient", "now() - interval '4 years'");
        await InsertAuditAsync(tenant, "young", "now() - interval '1 day'");

        // Runtime role: no UPDATE/DELETE/TRUNCATE grant — setting the purge marker itself changes nothing.
        foreach (var sql in new[]
        {
            "DELETE FROM audit.audit_events WHERE tenant_id = $1",
            "UPDATE audit.audit_events SET action = 'tenant.renamed' WHERE tenant_id = $1",
            "TRUNCATE audit.audit_events",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteWithMarkerAsync(fixture.AppConnectionString, tenant, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // Owner (migrator): a direct DELETE without the function's marker is stopped by the trigger, even for an
        // expired row. With the marker, TRUNCATE is still stopped and UPDATE has no owner policy, so it sees no row.
        var direct = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(fixture.MigratorConnectionString, "DELETE FROM audit.audit_events WHERE tenant_id = $1", tenant));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, direct.SqlState);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteWithMarkerAsync(fixture.MigratorConnectionString, tenant, "TRUNCATE audit.audit_events"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);
        Assert.Equal(0, await ExecuteWithMarkerAsync(fixture.MigratorConnectionString, tenant, "UPDATE audit.audit_events SET action = 'tenant.renamed' WHERE tenant_id = $1"));

        // Owner with the marker cannot even see a row younger than the floor (retention policies), so cannot delete it.
        Assert.Equal(0, await ExecuteWithMarkerAsync(fixture.MigratorConnectionString, tenant, "DELETE FROM audit.audit_events WHERE tenant_id = $1 AND occurred_at > now() - interval '2 years'"));

        // The superuser bypasses RLS but is not the owner: the trigger rejects its DELETE and UPDATE even with the
        // marker and an expired row.
        foreach (var sql in new[] { "DELETE FROM audit.audit_events WHERE tenant_id = $1", "UPDATE audit.audit_events SET action = 'tenant.renamed' WHERE tenant_id = $1" })
        {
            var super = await Assert.ThrowsAsync<PostgresException>(() => ExecuteWithMarkerAsync(fixture.SuperuserConnectionString, tenant, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, super.SqlState);
        }

        Assert.Equal("ancient,young", await AuditMarkersAsync(tenant));
    }

    // ---------------------------------------------------------------- queue cleanup

    [Fact]
    public async Task Queue_cleanup_deletes_only_finished_jobs_and_processed_messages_past_retention()
    {
        var marker = "test.cleanup_" + Guid.NewGuid().ToString("N")[..10];
        await ExecuteAsync(
            fixture.SuperuserConnectionString,
            """
            INSERT INTO platform.jobs (id, tenant_id, type, payload, status, priority, run_after, attempts, max_attempts, last_error, correlation_id, created_at, finished_at)
            VALUES (gen_random_uuid(), NULL, $1, '{}', 'succeeded', 0, now(), 1, 5, NULL, 'succeeded-old', now() - interval '9 days', now() - interval '8 days'),
                   (gen_random_uuid(), NULL, $1, '{}', 'succeeded', 0, now(), 1, 5, NULL, 'succeeded-recent', now() - interval '7 days', now() - interval '6 days'),
                   (gen_random_uuid(), NULL, $1, '{}', 'dead', 0, now(), 5, 5, 'x', 'dead-old', now() - interval '40 days', now() - interval '31 days'),
                   (gen_random_uuid(), NULL, $1, '{}', 'dead', 0, now(), 5, 5, 'x', 'dead-recent', now() - interval '20 days', now() - interval '10 days'),
                   (gen_random_uuid(), NULL, $1, '{}', 'queued', 0, now() + interval '1 day', 0, 5, NULL, 'queued-old', now() - interval '90 days', NULL),
                   (gen_random_uuid(), NULL, $1, '{}', 'running', 0, now(), 1, 5, NULL, 'running-old', now() - interval '90 days', NULL)
            """,
            marker);
        await ExecuteAsync(
            fixture.SuperuserConnectionString,
            """
            INSERT INTO platform.outbox_messages (id, tenant_id, type, payload, correlation_id, occurred_at, processed_at, attempts, next_attempt_at)
            VALUES (gen_random_uuid(), NULL, $1, '{}', 'processed-old', now() - interval '9 days', now() - interval '8 days', 1, now()),
                   (gen_random_uuid(), NULL, $1, '{}', 'processed-recent', now() - interval '2 days', now() - interval '1 day', 1, now()),
                   (gen_random_uuid(), NULL, $1, '{}', 'pending-old', now() - interval '90 days', NULL, 3, now() + interval '1 day')
            """,
            marker);

        await RunAsync(QueueCleanupJob.Type, services => services.GetRequiredService<QueueCleanupJob>());

        Assert.Equal(
            "dead-recent,queued-old,running-old,succeeded-recent",
            await SuperScalarAsync<string>("SELECT string_agg(correlation_id, ',' ORDER BY correlation_id) FROM platform.jobs WHERE type = $1", marker));
        Assert.Equal(
            "pending-old,processed-recent",
            await SuperScalarAsync<string>("SELECT string_agg(correlation_id, ',' ORDER BY correlation_id) FROM platform.outbox_messages WHERE type = $1", marker));
    }

    [Fact]
    public void Maintenance_jobs_are_registered_and_scheduled()
    {
        var registry = fixture.Services.GetRequiredService<JobHandlerRegistry>();
        Assert.Equal(
            new[]
            {
                IdempotencyCleanupJob.Type, QueueCleanupJob.Type, AuditRetentionJob.Type, BayiOS.Modules.Integrations.IntegrationsModule.TextRetentionJobType,
                BayiOS.Modules.Billing.BillingModule.ExpiryJobType,
            },
            PlatformWorker.MaintenanceJobTypes);
        Assert.All(PlatformWorker.MaintenanceJobTypes, type => Assert.True(registry.IsRegistered(type), type));
    }

    // ---------------------------------------------------------------- unregistered job types

    [Fact]
    public async Task Enqueue_of_an_unregistered_job_type_fails_inside_the_transaction()
    {
        var member = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unknown = "test.unregistered_" + Guid.NewGuid().ToString("N")[..10];

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var unitOfWork = await BeginAsync(scope.ServiceProvider, member);
            var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobs>();
            var error = await Assert.ThrowsAsync<UnknownJobTypeException>(() => jobs.EnqueueAsync(new JobRequest(unknown), Ct));
            Assert.Equal(unknown, error.JobType);
            Assert.Contains(unknown, error.Message, StringComparison.Ordinal);

            // The reserved delivery type is internal to the outbox dispatcher.
            await Assert.ThrowsAsync<UnknownJobTypeException>(() => jobs.EnqueueAsync(new JobRequest(JobHandlerRegistry.OutboxDeliveryType), Ct));

            // The transaction is still usable for the caller to roll back; nothing was written.
            await unitOfWork.RollbackAsync(Ct);
        }

        Assert.Equal(0L, await SuperScalarAsync<long>("SELECT count(*) FROM platform.jobs WHERE type = $1", unknown));
    }

    [Fact]
    public void Every_job_handler_shipped_in_the_product_is_registered_under_its_type()
    {
        // Every IJobHandler of the product assemblies declares `const string Type`; enqueue sites use that constant,
        // so a handler that is not registered would make its own enqueue fail at runtime.
        var registry = fixture.Services.GetRequiredService<JobHandlerRegistry>();
        var assemblies = typeof(Program).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("BayiOS.", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .Append(typeof(Program).Assembly)
            .Distinct();
        var handlers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IJobHandler).IsAssignableFrom(t))
            .ToList();

        Assert.True(handlers.Count >= 5, $"Only {handlers.Count} job handlers found — scan broken?");
        foreach (var handler in handlers)
        {
            var field = handler.GetField("Type", BindingFlags.Public | BindingFlags.Static);
            Assert.True(field is { IsLiteral: true }, $"{handler.FullName} has no public const string Type");
            var type = (string)field!.GetRawConstantValue()!;
            Assert.True(registry.IsRegistered(type), $"{handler.FullName} ({type}) is not registered");
        }
    }

    // ---------------------------------------------------------------- last_error

    [Fact]
    public async Task Failed_job_stores_a_sanitized_error_without_values()
    {
        var type = "test.failing_" + Guid.NewGuid().ToString("N")[..10];
        var id = await RunAsync(type, services => new FailingSqlHandler(services.GetRequiredService<UnitOfWork>()));

        var (status, error) = await SuperRowAsync(id);
        Assert.Equal("dead", status);
        Assert.StartsWith("PostgresException [22P02]: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain("ayse.yilmaz", error, StringComparison.Ordinal);
        Assert.DoesNotContain("05551234567", error, StringComparison.Ordinal);
        Assert.Contains("[value]", error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Audit:IpHashKey

    [Fact]
    public async Task Ip_hash_key_shorter_than_32_bytes_stops_startup_and_a_long_one_is_accepted()
    {
        await using (var weak = fixture.WithWebHostBuilder(builder => builder.UseSetting("Audit:IpHashKey", "test-only-too-short")))
        {
            var error = Assert.ThrowsAny<Exception>(() => weak.Services);
            Assert.Contains("Audit:IpHashKey", error.ToString(), StringComparison.Ordinal);
        }

        await using (var shortRetention = fixture.WithWebHostBuilder(builder => builder.UseSetting("Audit:Retention", "365.00:00:00")))
        {
            Assert.Contains("Audit:Retention", Assert.ThrowsAny<Exception>(() => shortRetention.Services).ToString(), StringComparison.Ordinal);
        }

        await using var strong = fixture.WithWebHostBuilder(builder => builder.UseSetting("Audit:IpHashKey", new string('k', 32)));
        Assert.NotNull(strong.Services.GetRequiredService<JobHandlerRegistry>());
    }

    // ---- helpers ----

    /// <summary>Enqueues one system job of <paramref name="type"/> (max 1 attempt) and runs the queue dry for that type.</summary>
    private async Task<Guid> RunAsync(string type, Func<IServiceProvider, IJobHandler> create)
    {
        var id = (await fixture.Services.GetRequiredService<JobStore>().InsertAsync(new NewJob(null, type, "{}", null, 0, 1, null, null), Ct))!.Value;
        var registry = new JobHandlerRegistry(
            new Dictionary<string, Func<IServiceProvider, IJobHandler>> { [type] = create },
            new Dictionary<string, IReadOnlyList<OutboxSubscription>>());
        var processor = new JobProcessor(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Services.GetRequiredService<JobStore>(),
            registry,
            new JobsOptions(),
            NullLogger<JobProcessor>.Instance);
        while (await processor.RunOnceAsync(Ct) > 0)
        {
        }

        Assert.Equal(0L, await SuperScalarAsync<long>("SELECT count(*) FROM platform.jobs WHERE type = $1 AND status IN ('queued', 'running')", type));
        return id;
    }

    private static async Task<UnitOfWork> BeginAsync(IServiceProvider services, CatalogClient member)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.UserId = member.UserId;
        context.TenantId = member.TenantId;
        context.Role = Roles.Owner;
        var unitOfWork = services.GetRequiredService<UnitOfWork>();
        await unitOfWork.BeginAsync(readOnly: false, Ct);
        return unitOfWork;
    }

    /// <summary>Superuser insert (bypasses RLS) with a back-dated timestamp; the marker goes into <c>correlation_id</c>.</summary>
    private Task<int> InsertAuditAsync(Guid tenant, string marker, string occurredAt) =>
        ExecuteAsync(
            fixture.SuperuserConnectionString,
            $"""
            INSERT INTO audit.audit_events (id, tenant_id, action, entity_type, correlation_id, occurred_at)
            VALUES (gen_random_uuid(), $1, 'platform.retention_test', 'test', $2, {occurredAt})
            """,
            tenant,
            marker);

    private Task<string> AuditMarkersAsync(Guid tenant) =>
        SuperScalarAsync<string>(
            "SELECT coalesce(string_agg(correlation_id, ',' ORDER BY correlation_id), '') FROM audit.audit_events WHERE tenant_id = $1", tenant);

    /// <summary>Runs <paramref name="sql"/> with a tenant context and the purge marker set, as the given role.</summary>
    private static async Task<int> ExecuteWithMarkerAsync(string connectionString, Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand($"SELECT set_config('app.tenant_id', $1, true), set_config('{PurgeMarker}', 'on', true)", connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter { Value = tenant.ToString() });
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (sql.Contains("$1", StringComparison.Ordinal))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        }

        var affected = await command.ExecuteNonQueryAsync(Ct);
        await transaction.RollbackAsync(Ct);
        return affected;
    }

    private static async Task<int> ExecuteAsync(string connectionString, string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> AppScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<T> SuperScalarAsync<T>(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<(string Status, string Error)> SuperRowAsync(Guid jobId)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT status, last_error FROM platform.jobs WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Fails with a PostgreSQL error whose message echoes personal data (22P02 quotes the bad input).</summary>
    private sealed class FailingSqlHandler(UnitOfWork unitOfWork) : IJobHandler
    {
        public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("SELECT 'ayse.yilmaz@example.com 05551234567'::uuid", unitOfWork.Connection, unitOfWork.Transaction);
            await command.ExecuteScalarAsync(cancellationToken);
        }
    }
}

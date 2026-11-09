using System.Collections.Concurrent;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>
/// Job queue and transactional outbox (ADR 0006). The shared test host runs with <c>Jobs:Enabled=false</c>; these
/// tests drive <see cref="JobProcessor"/> / <see cref="OutboxDispatcher"/> directly with test-only job types. Tests in
/// this class run sequentially and outside the parallel tests (<see cref="GlobalWorkersTestGroup"/>), so only one
/// dispatcher at a time sees the (global) outbox and no other test's processor claims these jobs.
/// </summary>
[Collection(GlobalWorkersTestGroup.Name)]
public class JobsTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_claims_never_hand_out_the_same_job_twice()
    {
        var type = NewType();
        await EnqueueSystemAsync(type, 60);
        var store = fixture.Services.GetRequiredService<JobStore>();

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => store.ClaimAsync($"claimer-{i}", [type], 10, TimeSpan.FromMinutes(1), Ct), Ct)));

        var claimed = claims.SelectMany(c => c).Select(j => j.Id).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.Equal(60, claimed.Count); // 8 × 10 ≥ 60: every job claimed once, none twice
        Assert.Equal(60L, await ScalarAsync<long>("SELECT count(*) FROM platform.jobs WHERE type = $1 AND status = 'running' AND attempts = 1", type));
    }

    [Fact]
    public async Task Concurrent_workers_run_each_job_exactly_once()
    {
        var type = NewType();
        await EnqueueSystemAsync(type, 40);
        var runs = new ConcurrentDictionary<Guid, int>();
        var registry = Registry(type, _ => new DelegateJobHandler(async (job, ct) =>
        {
            runs.AddOrUpdate(job.Id, 1, (_, n) => n + 1);
            await Task.Delay(5, ct);
        }));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(
            async () =>
            {
                var processor = Processor(registry, new JobsOptions { WorkerId = $"worker-{i}", BatchSize = 3 });
                while (await processor.RunOnceAsync(Ct) > 0)
                {
                }
            },
            Ct)));

        Assert.Equal(40, runs.Count);
        Assert.All(runs.Values, n => Assert.Equal(1, n));
        Assert.Equal(40L, await ScalarAsync<long>("SELECT count(*) FROM platform.jobs WHERE type = $1 AND status = 'succeeded' AND finished_at IS NOT NULL", type));
    }

    [Fact]
    public async Task Failing_job_is_retried_with_exponential_backoff_then_dead_lettered()
    {
        // Every processor also claims the reserved outbox-delivery type, and other tests in this serial group run a real
        // dispatcher over the shared outbox, so delivery jobs of other tests may be queued. The job gets the top priority
        // and the processor a batch of one, so each run touches exactly this job; "not claimable" is probed for this
        // type only, so no foreign job is claimed or run by this test.
        var type = NewType();
        var store = fixture.Services.GetRequiredService<JobStore>();
        var id = (await store.InsertAsync(new NewJob(null, type, "{}", null, short.MaxValue, 3, null, null), Ct))!.Value;
        var calls = 0;
        var registry = Registry(type, _ => new DelegateJobHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("synthetic failure");
        }));
        var processor = Processor(registry, new JobsOptions { BackoffBase = TimeSpan.FromMinutes(1), BackoffMax = TimeSpan.FromHours(1), BatchSize = 1 });

        Assert.Equal(1, await processor.RunOnceAsync(Ct));
        var first = await JobAsync(id);
        Assert.Equal(("queued", 1), (first.Status, first.Attempts));
        Assert.Contains("synthetic failure", first.LastError, StringComparison.Ordinal);
        Assert.InRange(first.RunAfterSeconds, 50, 70); // 1 min × 2^0

        Assert.Empty(await store.ClaimAsync("probe", [type], 10, TimeSpan.FromMinutes(1), Ct)); // not due yet
        await MakeDueAsync(id);
        Assert.Equal(1, await processor.RunOnceAsync(Ct));
        var second = await JobAsync(id);
        Assert.Equal(("queued", 2), (second.Status, second.Attempts));
        Assert.InRange(second.RunAfterSeconds, 110, 130); // 1 min × 2^1

        await MakeDueAsync(id);
        Assert.Equal(1, await processor.RunOnceAsync(Ct));
        var dead = await JobAsync(id);
        Assert.Equal(("dead", 3, true), (dead.Status, dead.Attempts, dead.Finished));
        Assert.Equal(3, calls);

        await MakeDueAsync(id);
        Assert.Empty(await store.ClaimAsync("probe", [type], 10, TimeSpan.FromMinutes(1), Ct)); // dead jobs are never claimed again
    }

    [Fact]
    public async Task Expired_lease_returns_the_job_to_the_queue_and_the_old_worker_cannot_complete_it()
    {
        var type = NewType();
        var id = (await fixture.Services.GetRequiredService<JobStore>().InsertAsync(new NewJob(null, type, "{}", null, 0, 5, null, null), Ct))!.Value;
        var store = fixture.Services.GetRequiredService<JobStore>();
        Assert.Single(await store.ClaimAsync("crashed-worker", [type], 1, TimeSpan.FromMinutes(1), Ct));
        await ExecuteAsync("UPDATE platform.jobs SET locked_until = now() - interval '1 second' WHERE id = $1", id);

        var ran = 0;
        var processor = Processor(Registry(type, _ => new DelegateJobHandler((_, _) =>
        {
            ran++;
            return Task.CompletedTask;
        })));
        Assert.Equal(1, await processor.RunOnceAsync(Ct)); // swept, re-claimed and run
        var job = await JobAsync(id);
        Assert.Equal(("succeeded", 2, 1), (job.Status, job.Attempts, ran));
        Assert.Null(await store.FailAsync(id, "crashed-worker", "late failure", TimeSpan.Zero, Ct)); // stale worker: no effect
        Assert.Equal("succeeded", (await JobAsync(id)).Status);
    }

    [Fact]
    public async Task Handler_runs_in_the_job_tenant_context_and_sees_only_that_tenant()
    {
        var tenantA = await CatalogClient.NewTenantOwnerAsync(fixture);
        var tenantB = await CatalogClient.NewTenantOwnerAsync(fixture);
        var type = NewType();
        await using var host = HostKnowing(type);
        var jobA = await EnqueueAsAsync(host.Services, tenantA, new JobRequest(type, new { purpose = "tenant-context" }));
        var system = (await fixture.Services.GetRequiredService<JobStore>().InsertAsync(new NewJob(null, type, "{}", null, 0, 1, null, null), Ct))!.Value;

        var seen = new ConcurrentDictionary<Guid, (Guid? ContextTenant, string Setting, Guid[] VisibleTenants)>();
        var registry = Registry(type, services => new DelegateJobHandler(async (job, ct) =>
        {
            var unitOfWork = services.GetRequiredService<UnitOfWork>();
            await using var command = new NpgsqlCommand(
                "SELECT current_setting('app.tenant_id', true), coalesce(array_agg(DISTINCT tenant_id), '{}') FROM audit.audit_events",
                unitOfWork.Connection,
                unitOfWork.Transaction);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            seen[job.Id] = (services.GetRequiredService<IRequestContext>().TenantId, reader.GetString(0), reader.GetFieldValue<Guid[]>(1));
            await reader.DisposeAsync();
            if (job.TenantId is not null)
            {
                // Jobs can audit as the system actor of their tenant.
                await services.GetRequiredService<IAuditLog>().RecordAsync(new AuditEvent("platform.test_job_ran", "job", job.Id), ct);
            }
        }));

        while (await Processor(registry).RunOnceAsync(Ct) > 0)
        {
        }

        Assert.Equal((tenantA.TenantId, tenantA.TenantId.ToString()), (seen[jobA].ContextTenant, seen[jobA].Setting));
        Assert.Equal(new[] { tenantA.TenantId }, seen[jobA].VisibleTenants);
        Assert.DoesNotContain(tenantB.TenantId, seen[jobA].VisibleTenants);
        Assert.Equal(((Guid?)null, string.Empty), (seen[system].ContextTenant, seen[system].Setting));
        Assert.Empty(seen[system].VisibleTenants);
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'platform.test_job_ran' AND actor_user_id IS NULL",
            tenantA.TenantId));
    }

    [Fact]
    public async Task Enqueue_is_transactional_deduplicated_and_cannot_forge_another_tenant()
    {
        var tenantA = await CatalogClient.NewTenantOwnerAsync(fixture);
        var tenantB = await CatalogClient.NewTenantOwnerAsync(fixture);
        var type = NewType();
        await using var host = HostKnowing(type);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var unitOfWork = await BeginAsync(scope.ServiceProvider, tenantA);
            var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobs>();
            Assert.NotNull(await jobs.EnqueueAsync(new JobRequest(type, DedupeKey: "once"), Ct));
            Assert.Null(await jobs.EnqueueAsync(new JobRequest(type, DedupeKey: "once"), Ct));
            await unitOfWork.RollbackAsync(Ct);
        }

        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM platform.jobs WHERE type = $1", type));

        var committed = await EnqueueAsAsync(host.Services, tenantA, new JobRequest(type, DedupeKey: "once"));
        Assert.Equal(tenantA.TenantId, await ScalarAsync<Guid>("SELECT tenant_id FROM platform.jobs WHERE id = $1", committed));

        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync(Ct);
        await using var transaction = await app.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", app, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter { Value = tenantA.TenantId.ToString() });
            await context.ExecuteNonQueryAsync(Ct);
        }

        foreach (var sql in new[]
        {
            "INSERT INTO platform.jobs (id, tenant_id, type, payload, status, priority, run_after, attempts, max_attempts, created_at) VALUES (gen_random_uuid(), $1, 'x.forged', '{}', 'queued', 0, now(), 0, 1, now())",
            "INSERT INTO platform.outbox_messages (id, tenant_id, type, payload, occurred_at, attempts, next_attempt_at) VALUES (gen_random_uuid(), $1, 'x.forged', '{}', now(), 0, now())",
        })
        {
            await using var savepoint = new NpgsqlCommand("SAVEPOINT forge", app, transaction);
            await savepoint.ExecuteNonQueryAsync(Ct);
            await using var forge = new NpgsqlCommand(sql, app, transaction);
            forge.Parameters.Add(new NpgsqlParameter { Value = tenantB.TenantId });
            var error = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT forge", app, transaction);
            await rollback.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task Outbox_message_is_delivered_only_when_its_transaction_commits()
    {
        var tenant = await CatalogClient.NewTenantOwnerAsync(fixture);
        var messageType = NewType() + ".v1";
        var delivered = new ConcurrentBag<(OutboxMessage Message, Guid? ContextTenant)>();
        var registry = new JobHandlerRegistry(
            new Dictionary<string, Func<IServiceProvider, IJobHandler>>(),
            new Dictionary<string, IReadOnlyList<OutboxSubscription>>
            {
                [messageType] = [new OutboxSubscription("test-subscriber", services => new DelegateOutboxHandler((message, _) =>
                {
                    delivered.Add((message, services.GetRequiredService<IRequestContext>().TenantId));
                    return Task.CompletedTask;
                }))],
            });

        Guid rolledBack;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var unitOfWork = await BeginAsync(scope.ServiceProvider, tenant);
            rolledBack = await scope.ServiceProvider.GetRequiredService<IOutbox>().PublishAsync(messageType, new { n = 1 }, Ct);
            await unitOfWork.RollbackAsync(Ct);
        }

        Guid committed;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var unitOfWork = await BeginAsync(scope.ServiceProvider, tenant);
            committed = await scope.ServiceProvider.GetRequiredService<IOutbox>().PublishAsync(messageType, new { n = 2 }, Ct);
            await unitOfWork.CommitAsync(Ct);
        }

        var dispatcher = new OutboxDispatcher(
            fixture.Services.GetRequiredService<NpgsqlDataSource>(), registry, new JobsOptions(), NullLogger<OutboxDispatcher>.Instance);
        while (await dispatcher.RunOnceAsync(Ct) > 0)
        {
        }

        var processor = Processor(registry);
        while (await processor.RunOnceAsync(Ct) > 0)
        {
        }

        var (message, contextTenant) = Assert.Single(delivered);
        Assert.Equal((committed, tenant.TenantId, tenant.TenantId, 2), (message.Id, message.TenantId, contextTenant, message.Payload.GetProperty("n").GetInt32()));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages WHERE id = $1", rolledBack));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages WHERE id = $1 AND processed_at IS NOT NULL", committed));

        // A second dispatch pass finds nothing new: delivered once.
        Assert.Equal(0, await dispatcher.RunOnceAsync(Ct));
        Assert.Equal(0, await processor.RunOnceAsync(Ct));
        Assert.Single(delivered);
    }

    [Fact]
    public async Task Idempotency_cleanup_job_removes_only_expired_keys()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var marker = "cleanup-" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(
            """
            INSERT INTO platform.idempotency_keys (id, tenant_id, user_id, key, method, path, request_hash, status, created_at, expires_at)
            VALUES (gen_random_uuid(), $1, $2, $3 || '-expired-tenant', 'POST', '/x', '\x00', 'completed', now() - interval '2 days', now() - interval '1 day'),
                   (gen_random_uuid(), NULL, $2, $3 || '-expired-user', 'POST', '/x', '\x00', 'completed', now() - interval '2 days', now() - interval '1 second'),
                   (gen_random_uuid(), $1, $2, $3 || '-live', 'POST', '/x', '\x00', 'completed', now(), now() + interval '1 day')
            """,
            tenant,
            user,
            marker);

        await fixture.Services.GetRequiredService<JobStore>().InsertAsync(
            new NewJob(null, IdempotencyCleanupJob.Type, "{}", null, 0, 1, null, null), Ct);
        var registry = Registry(IdempotencyCleanupJob.Type, services => services.GetRequiredService<IdempotencyCleanupJob>());
        while (await Processor(registry).RunOnceAsync(Ct) > 0)
        {
        }

        var remaining = await ScalarAsync<string[]>(
            "SELECT coalesce(array_agg(key ORDER BY key), '{}') FROM platform.idempotency_keys WHERE key LIKE $1 || '%'", marker);
        Assert.Equal(new[] { marker + "-live" }, remaining);
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.jobs WHERE type = $1 AND status <> 'succeeded' AND created_at > now() - interval '1 minute'",
            IdempotencyCleanupJob.Type));
    }

    [Fact]
    public async Task Hosted_worker_runs_jobs_and_stops_gracefully()
    {
        var type = NewType();
        HostedProbe.Reset(type);
        await using var host = fixture.Derive(builder =>
        {
            builder.UseSetting("Jobs:Enabled", "true");
            builder.UseSetting("Jobs:PollInterval", "00:00:00.100");
            builder.UseSetting("Jobs:MaxIdleInterval", "00:00:00.200");
            builder.ConfigureTestServices(services => services.AddJobHandler<HostedProbe>(type));
        });

        var id = await host.Services.GetRequiredService<JobStore>().InsertAsync(new NewJob(null, type, "{}", null, 0, 1, null, null), Ct);
        Assert.Equal(id, await HostedProbe.Ran.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct));

        for (var i = 0; i < 100 && (await JobAsync(id!.Value)).Status != "succeeded"; i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Equal("succeeded", (await JobAsync(id!.Value)).Status);
    }

    // ---- helpers ----

    private static string NewType() => "test.job_" + Guid.NewGuid().ToString("N")[..12];

    private static JobHandlerRegistry Registry(string type, Func<IServiceProvider, IJobHandler> create) =>
        new(new Dictionary<string, Func<IServiceProvider, IJobHandler>> { [type] = create }, new Dictionary<string, IReadOnlyList<OutboxSubscription>>());

    private JobProcessor Processor(JobHandlerRegistry registry, JobsOptions? options = null) =>
        new(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Services.GetRequiredService<JobStore>(),
            registry,
            options ?? new JobsOptions(),
            NullLogger<JobProcessor>.Instance);

    private async Task EnqueueSystemAsync(string type, int count)
    {
        var store = fixture.Services.GetRequiredService<JobStore>();
        for (var i = 0; i < count; i++)
        {
            await store.InsertAsync(new NewJob(null, type, $$"""{"n":{{i}}}""", null, 0, 5, null, null), Ct);
        }
    }

    /// <summary>A host whose registry knows <paramref name="type"/>: <see cref="IBackgroundJobs"/> rejects unregistered types (#89).</summary>
    private WebApplicationFactory<Program> HostKnowing(string type) =>
        fixture.Derive(builder => builder.ConfigureTestServices(services => services.AddJobHandler<HostedProbe>(type)));

    private static async Task<Guid> EnqueueAsAsync(IServiceProvider services, CatalogClient member, JobRequest job)
    {
        await using var scope = services.CreateAsyncScope();
        var unitOfWork = await BeginAsync(scope.ServiceProvider, member);
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobs>().EnqueueAsync(job, Ct);
        await unitOfWork.CommitAsync(Ct);
        return id!.Value;
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

    private async Task<(string Status, int Attempts, string? LastError, double RunAfterSeconds, bool Finished)> JobAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT status, attempts, last_error, extract(epoch FROM run_after - now())::float8, finished_at IS NOT NULL FROM platform.jobs WHERE id = $1",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDouble(3), reader.GetBoolean(4));
    }

    private Task MakeDueAsync(Guid id) => ExecuteAsync("UPDATE platform.jobs SET run_after = now() WHERE id = $1", id);

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] values)
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

    private sealed class DelegateJobHandler(Func<JobExecution, CancellationToken, Task> handle) : IJobHandler
    {
        public Task HandleAsync(JobExecution job, CancellationToken cancellationToken) => handle(job, cancellationToken);
    }

    private sealed class DelegateOutboxHandler(Func<OutboxMessage, CancellationToken, Task> handle) : IOutboxHandler
    {
        public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) => handle(message, cancellationToken);
    }
}

/// <summary>Job handler resolved from the hosted worker's own DI container.</summary>
internal sealed class HostedProbe : IJobHandler
{
    private static string _type = string.Empty;

    public static TaskCompletionSource<Guid?> Ran { get; private set; } = new();

    public static void Reset(string type)
    {
        _type = type;
        Ran = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.Type == _type)
        {
            Ran.TrySetResult(job.Id);
        }

        return Task.CompletedTask;
    }
}

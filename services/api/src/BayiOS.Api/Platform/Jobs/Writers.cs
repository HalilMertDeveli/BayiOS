using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// <see cref="IBackgroundJobs"/> on the request/job UnitOfWork transaction; tenant = current tenant. A type without a
/// registered handler is rejected here (#89), inside the caller's transaction, so the business change rolls back
/// instead of leaving a job that no worker will ever claim. Every module registers its handlers in this same
/// composition root (also when <c>Jobs:Enabled=false</c>), so the API process knows every type a worker can run.
/// </summary>
internal sealed class BackgroundJobs(UnitOfWork unitOfWork, IRequestContext requestContext, JobHandlerRegistry registry, JobWakeup? wakeup = null) : IBackgroundJobs
{
    public async Task<Guid?> EnqueueAsync(JobRequest job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Type);
        if (!registry.IsRegistered(job.Type))
        {
            throw new UnknownJobTypeException(job.Type);
        }

        var transaction = unitOfWork.Transaction
            ?? throw new InvalidOperationException("Jobs are enqueued inside the unit of work transaction.");
        var id = await JobStore.InsertAsync(
            unitOfWork.Connection,
            transaction,
            new NewJob(
                requestContext.TenantId,
                job.Type,
                JobStore.Serialize(job.Payload),
                job.RunAfter,
                job.Priority,
                job.MaxAttempts,
                job.DedupeKey,
                requestContext.CorrelationId),
            cancellationToken);

        // Due now: wake this instance's worker once the job is visible (after commit; a rollback discards it).
        if (id is not null && wakeup is not null && job.RunAfter is null)
        {
            unitOfWork.OnCommitted(wakeup.Signal);
        }

        return id;
    }
}

/// <summary><see cref="IOutbox"/> on the request/job UnitOfWork transaction; tenant = current tenant.</summary>
internal sealed class Outbox(UnitOfWork unitOfWork, IRequestContext requestContext) : IOutbox
{
    public async Task<Guid> PublishAsync(string type, object payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);
        var transaction = unitOfWork.Transaction
            ?? throw new InvalidOperationException("Outbox messages are written inside the unit of work transaction.");

        var id = Guid.CreateVersion7();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, tenant_id, type, payload, correlation_id, occurred_at, attempts, next_attempt_at)
            VALUES (@id, @tenant, @type, @payload, @correlation, now(), 0, now())
            """,
            unitOfWork.Connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)requestContext.TenantId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<string>("type", type));
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JobStore.Serialize(payload) });
        command.Parameters.Add(new NpgsqlParameter("correlation", NpgsqlDbType.Text) { Value = (object?)requestContext.CorrelationId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }
}

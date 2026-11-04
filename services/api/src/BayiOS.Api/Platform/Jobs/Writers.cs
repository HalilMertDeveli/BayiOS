using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform.Jobs;

/// <summary><see cref="IBackgroundJobs"/> on the request/job UnitOfWork transaction; tenant = current tenant.</summary>
internal sealed class BackgroundJobs(UnitOfWork unitOfWork, IRequestContext requestContext) : IBackgroundJobs
{
    public Task<Guid?> EnqueueAsync(JobRequest job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var transaction = unitOfWork.Transaction
            ?? throw new InvalidOperationException("Jobs are enqueued inside the unit of work transaction.");
        return JobStore.InsertAsync(
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

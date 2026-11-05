using BayiOS.Api.Platform.Audit;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// Deletes finished queue rows (#89): succeeded jobs after <see cref="JobsOptions.SucceededRetention"/>, dead jobs
/// after <see cref="JobsOptions.DeadRetention"/> and processed outbox messages after
/// <see cref="JobsOptions.OutboxRetention"/>. Queued/running jobs and pending messages are never touched. Batched
/// (oldest first, via <c>ix_jobs_finished</c> / <c>ix_outbox_messages_processed</c>); what a run leaves over is
/// picked up by the next maintenance tick. Tenant-less: the queue tables have no RLS (data-model §2.3).
/// </summary>
internal sealed partial class QueueCleanupJob(UnitOfWork unitOfWork, JobsOptions options, ILogger<QueueCleanupJob> logger) : IJobHandler
{
    public const string Type = "platform.queue_cleanup";
    public const int BatchSize = 1000;
    public const int MaxBatches = 50;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var succeeded = await DeleteAsync(
            """
            DELETE FROM platform.jobs WHERE id IN (
                SELECT id FROM platform.jobs
                WHERE status = 'succeeded' AND finished_at < now() - @retention
                ORDER BY finished_at
                LIMIT @limit)
            """,
            options.SucceededRetention,
            cancellationToken);
        var dead = await DeleteAsync(
            """
            DELETE FROM platform.jobs WHERE id IN (
                SELECT id FROM platform.jobs
                WHERE status = 'dead' AND finished_at < now() - @retention
                ORDER BY finished_at
                LIMIT @limit)
            """,
            options.DeadRetention,
            cancellationToken);
        var outbox = await DeleteAsync(
            """
            DELETE FROM platform.outbox_messages WHERE id IN (
                SELECT id FROM platform.outbox_messages
                WHERE processed_at IS NOT NULL AND processed_at < now() - @retention
                ORDER BY processed_at
                LIMIT @limit)
            """,
            options.OutboxRetention,
            cancellationToken);

        LogPurged(logger, succeeded, dead, outbox);
    }

    private async Task<int> DeleteAsync(string sql, TimeSpan retention, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            await using var command = new NpgsqlCommand(sql, unitOfWork.Connection, unitOfWork.Transaction);
            command.Parameters.Add(new NpgsqlParameter<TimeSpan>("retention", retention));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", BatchSize));
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue cleanup deleted {Succeeded} succeeded jobs, {Dead} dead jobs and {Outbox} processed outbox messages")]
    private static partial void LogPurged(ILogger logger, int succeeded, int dead, int outbox);
}

/// <summary>
/// Audit retention (data-policy §4: 2 years, #89). The audit trail is append-only for every role; the only way out is
/// <c>audit.purge_expired_audit_events(retention, limit)</c> — SECURITY DEFINER, owned by the migrator, pinned
/// search_path — which deletes only rows older than both <see cref="AuditOptions.Retention"/> and the hard 2-year
/// floor in the database. The append-only trigger lets nothing else through. Batched, oldest first.
/// </summary>
internal sealed partial class AuditRetentionJob(UnitOfWork unitOfWork, IOptions<AuditOptions> options, ILogger<AuditRetentionJob> logger) : IJobHandler
{
    public const string Type = "platform.audit_retention";
    public const int BatchSize = 1000;
    public const int MaxBatches = 50;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT audit.purge_expired_audit_events(@retention, @limit)", unitOfWork.Connection, unitOfWork.Transaction);
            command.Parameters.Add(new NpgsqlParameter<TimeSpan>("retention", options.Value.Retention));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", BatchSize));
            var deleted = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        LogPurged(logger, total);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit retention deleted {Count} events past the retention period")]
    private static partial void LogPurged(ILogger logger, int count);
}

using System.Text.Json;
using BayiOS.SharedKernel.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>A job claimed by this worker (status <c>running</c>, lease held).</summary>
internal sealed record ClaimedJob(
    Guid Id,
    Guid? TenantId,
    string Type,
    string Payload,
    int Attempts,
    int MaxAttempts,
    string? CorrelationId);

/// <summary>A job row to insert.</summary>
internal sealed record NewJob(
    Guid? TenantId,
    string Type,
    string Payload,
    DateTimeOffset? RunAfter,
    short Priority,
    int MaxAttempts,
    string? DedupeKey,
    string? CorrelationId);

/// <summary>
/// SQL of <c>platform.jobs</c> (ADR 0006). Claiming, failing and lease sweeping run as single autocommit statements
/// on their own connection, without a tenant context (the table has no RLS, data-model §2.3). Completion runs on the
/// handler's transaction so a DB-only handler's effects and the <c>succeeded</c> mark commit together.
/// </summary>
internal sealed class JobStore(NpgsqlDataSource dataSource)
{
    public const string MaxErrorLength = "1000";

    /// <summary>Inserts a job on the given connection/transaction; null when a live duplicate (type, dedupe_key) exists.</summary>
    public static async Task<Guid?> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, NewJob job, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Type);
        if (job.MaxAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(job), "MaxAttempts must be between 1 and 100.");
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.jobs
                (id, tenant_id, type, payload, status, priority, run_after, attempts, max_attempts, dedupe_key, correlation_id, created_at)
            VALUES (@id, @tenant, @type, @payload, 'queued', @priority, coalesce(@run_after, now()), 0, @max_attempts, @dedupe_key, @correlation, now())
            ON CONFLICT (type, dedupe_key) WHERE dedupe_key IS NOT NULL AND status IN ('queued', 'running') DO NOTHING
            RETURNING id
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", Guid.CreateVersion7()));
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)job.TenantId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<string>("type", job.Type));
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = job.Payload });
        command.Parameters.Add(new NpgsqlParameter<short>("priority", job.Priority));
        command.Parameters.Add(new NpgsqlParameter("run_after", NpgsqlDbType.TimestampTz) { Value = (object?)job.RunAfter ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<int>("max_attempts", job.MaxAttempts));
        command.Parameters.Add(new NpgsqlParameter("dedupe_key", NpgsqlDbType.Text) { Value = (object?)job.DedupeKey ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("correlation", NpgsqlDbType.Text) { Value = (object?)job.CorrelationId ?? DBNull.Value });
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    /// <summary>System enqueue outside any request (scheduler): own connection, no tenant context.</summary>
    public async Task<Guid?> InsertAsync(NewJob job, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await InsertAsync(connection, null, job, cancellationToken);
    }

    /// <summary>ADR 0006 claim: <c>FOR UPDATE SKIP LOCKED</c>, so concurrent workers never claim the same job.</summary>
    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId, IReadOnlyCollection<string> types, int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        if (types.Count == 0 || batchSize < 1)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(
            """
            UPDATE platform.jobs
            SET status = 'running', locked_by = @worker, locked_until = now() + @lease, attempts = attempts + 1
            WHERE id IN (
                SELECT id FROM platform.jobs
                WHERE status = 'queued' AND run_after <= now() AND type = ANY(@types)
                ORDER BY priority DESC, run_after
                FOR UPDATE SKIP LOCKED
                LIMIT @limit)
            RETURNING id, tenant_id, type, payload::text, attempts, max_attempts, correlation_id
            """);
        command.Parameters.Add(new NpgsqlParameter<string>("worker", workerId));
        command.Parameters.Add(new NpgsqlParameter<TimeSpan>("lease", lease));
        command.Parameters.Add(new NpgsqlParameter<string[]>("types", [.. types]));
        command.Parameters.Add(new NpgsqlParameter<int>("limit", batchSize));

        var claimed = new List<ClaimedJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedJob(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return claimed;
    }

    /// <summary>Marks the job succeeded on the handler's transaction; false when this worker no longer holds it.</summary>
    public static async Task<bool> CompleteAsync(UnitOfWork unitOfWork, Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE platform.jobs
            SET status = 'succeeded', finished_at = now(), locked_by = NULL, locked_until = NULL, last_error = NULL
            WHERE id = @id AND status = 'running' AND locked_by = @worker
            """,
            unitOfWork.Connection,
            unitOfWork.Transaction ?? throw new InvalidOperationException("The job unit of work has not started."));
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", jobId));
        command.Parameters.Add(new NpgsqlParameter<string>("worker", workerId));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>A failed attempt: back to <c>queued</c> after <paramref name="backoff"/>, or <c>dead</c> when attempts are exhausted.</summary>
    public async Task<string?> FailAsync(Guid jobId, string workerId, string error, TimeSpan backoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE platform.jobs
            SET status = CASE WHEN attempts >= max_attempts THEN 'dead' ELSE 'queued' END,
                run_after = CASE WHEN attempts >= max_attempts THEN run_after ELSE now() + @backoff END,
                finished_at = CASE WHEN attempts >= max_attempts THEN now() END,
                locked_by = NULL, locked_until = NULL, last_error = left(@error, {MaxErrorLength})
            WHERE id = @id AND status = 'running' AND locked_by = @worker
            RETURNING status
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", jobId));
        command.Parameters.Add(new NpgsqlParameter<string>("worker", workerId));
        command.Parameters.Add(new NpgsqlParameter<string>("error", error));
        command.Parameters.Add(new NpgsqlParameter<TimeSpan>("backoff", backoff));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Graceful shutdown: hands an unfinished job back without counting the attempt.</summary>
    public async Task ReleaseAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE platform.jobs
            SET status = 'queued', attempts = greatest(attempts - 1, 0), locked_by = NULL, locked_until = NULL
            WHERE id = @id AND status = 'running' AND locked_by = @worker
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", jobId));
        command.Parameters.Add(new NpgsqlParameter<string>("worker", workerId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Jobs of crashed workers (lease expired) go back to the queue, or to <c>dead</c> when attempts are exhausted.</summary>
    public async Task<int> SweepExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE platform.jobs
            SET status = CASE WHEN attempts >= max_attempts THEN 'dead' ELSE 'queued' END,
                finished_at = CASE WHEN attempts >= max_attempts THEN now() END,
                locked_by = NULL, locked_until = NULL,
                last_error = 'lease expired'
            WHERE id IN (
                SELECT id FROM platform.jobs
                WHERE status = 'running' AND locked_until < now()
                FOR UPDATE SKIP LOCKED)
            """);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string Serialize(object? payload) =>
        payload is null ? "{}" : JsonSerializer.Serialize(payload, payload.GetType(), JsonSerializerOptions.Web);
}

using System.Text.Json;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// Moves committed outbox messages to their subscribers (ADR 0006 §2): in one transaction it locks a batch of pending
/// messages (<c>FOR UPDATE SKIP LOCKED</c>), enqueues one <see cref="JobHandlerRegistry.OutboxDeliveryType"/> job per
/// subscriber (tenant and correlation id carried over, dedupe key = message + subscriber) and marks the message
/// processed. Delivery itself then gets the job queue's retry, backoff, dead-letter and tenant context. A message whose
/// transaction rolled back never exists, so it is never dispatched. Messages without subscribers are just marked.
/// </summary>
internal sealed partial class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    JobHandlerRegistry registry,
    JobsOptions options,
    ILogger<OutboxDispatcher> logger)
{
    private const string Savepoint = "outbox_message";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var messages = new List<(Guid Id, Guid? TenantId, string Type, string Payload, string? CorrelationId, DateTimeOffset OccurredAt, int Attempts)>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, tenant_id, type, payload::text, correlation_id, occurred_at, attempts
            FROM platform.outbox_messages
            WHERE processed_at IS NULL AND next_attempt_at <= now()
            ORDER BY next_attempt_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT @limit
            """,
            connection,
            transaction))
        {
            select.Parameters.Add(new NpgsqlParameter<int>("limit", options.BatchSize));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add((
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetInt32(6)));
            }
        }

        foreach (var message in messages)
        {
            await transaction.SaveAsync(Savepoint, cancellationToken);
            try
            {
                using var payload = JsonDocument.Parse(message.Payload);
                foreach (var subscription in registry.Outbox.GetValueOrDefault(message.Type) ?? [])
                {
                    var delivery = new OutboxDelivery(message.Id, message.Type, subscription.Name, payload.RootElement, message.OccurredAt);
                    await JobStore.InsertAsync(
                        connection,
                        transaction,
                        new NewJob(
                            message.TenantId,
                            JobHandlerRegistry.OutboxDeliveryType,
                            JobStore.Serialize(delivery),
                            RunAfter: null,
                            Priority: 0,
                            MaxAttempts: 10,
                            DedupeKey: $"{message.Id:N}:{subscription.Name}",
                            message.CorrelationId),
                        cancellationToken);
                }

                await Execute(connection, transaction, "UPDATE platform.outbox_messages SET processed_at = now(), attempts = attempts + 1, last_error = NULL WHERE id = @id",
                    message.Id, null, null, cancellationToken);
                await transaction.ReleaseAsync(Savepoint, cancellationToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or JsonException or InvalidOperationException)
            {
                await transaction.RollbackAsync(Savepoint, CancellationToken.None);
                await Execute(
                    connection,
                    transaction,
                    $"UPDATE platform.outbox_messages SET attempts = attempts + 1, next_attempt_at = now() + @backoff, last_error = left(@error, {JobStore.MaxErrorLength}) WHERE id = @id",
                    message.Id,
                    options.Backoff(message.Attempts + 1),
                    JobErrorSanitizer.Sanitize(ex),
                    CancellationToken.None);
                LogDispatchFailed(logger, ex, message.Id, message.Type);
            }
        }

        await transaction.CommitAsync(CancellationToken.None);
        return messages.Count;
    }

    private static async Task Execute(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, TimeSpan? backoff, string? error, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        if (backoff is { } delay)
        {
            command.Parameters.Add(new NpgsqlParameter<TimeSpan>("backoff", delay));
        }

        if (error is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>("error", error));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({MessageType}) could not be dispatched; retrying with backoff")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception, Guid messageId, string messageType);
}

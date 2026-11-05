using BayiOS.Modules.Integrations.Parsing;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>Today's usage of a tenant (Europe/Istanbul day) and the configured limits.</summary>
public sealed record AiUsageSnapshot(
    DateOnly Day,
    int ParseRequests,
    int AiCalls,
    int AiFailures,
    int Fallbacks,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    /// <summary>What the daily token quota counts: uncached input + cache writes + output (cache reads excluded).</summary>
    public long QuotaTokens => InputTokens + CacheWriteTokens + OutputTokens;
}

/// <summary>Outcome of reserving one AI call against the tenant's daily quota.</summary>
public enum AiQuotaDecision
{
    Allowed,
    CallLimitReached,
    TokenLimitReached,
}

/// <summary>
/// Postgres-backed AI metering (#50), multi-instance safe. Every write is an atomic upsert on
/// <c>integrations.ai_usage</c> in its <b>own short transaction</b> on a separate connection (tenant set with
/// <c>set_config</c>, RLS applies): the request transaction stays free of counter row locks while the provider is
/// called, and a call that was made is counted even if the request later rolls back. The daily quota check takes a
/// transaction-scoped advisory lock per tenant, so concurrent requests on any instance cannot overshoot it.
/// </summary>
public sealed class AiUsageMeter(NpgsqlDataSource dataSource, TimeProvider clock)
{
    /// <summary>Turkey has used UTC+3 all year since 2016; the usage day is the tenant's business day.</summary>
    private static readonly TimeSpan Istanbul = TimeSpan.FromHours(3);

    public DateTimeOffset CurrentHour => Hour(clock.GetUtcNow());

    /// <summary>Counts one parse request in the current hour unless <paramref name="hourlyLimit"/> is reached.</summary>
    public async Task<bool> TryStartParseAsync(Guid tenantId, Guid? userId, int hourlyLimit, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(tenantId, userId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO integrations.ai_usage AS u (tenant_id, usage_hour, parse_requests) VALUES (@tenant, @hour, 1)
                ON CONFLICT (tenant_id, usage_hour) DO UPDATE SET parse_requests = u.parse_requests + 1
                WHERE u.parse_requests < @limit
                RETURNING u.parse_requests
                """,
                connection,
                transaction);
            command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("hour", CurrentHour));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", hourlyLimit));
            return hourlyLimit > 0 && await command.ExecuteScalarAsync(cancellationToken) is not null;
        }, cancellationToken);
    }

    /// <summary>Reserves one AI call (increments <c>ai_calls</c>) when today's calls and tokens are under the limits.</summary>
    public async Task<AiQuotaDecision> TryReserveAiCallAsync(
        Guid tenantId, Guid? userId, int dailyCallLimit, long dailyTokenLimit, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(tenantId, userId, async (connection, transaction) =>
        {
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended('integrations.ai_quota:' || @tenant::text, 0))", connection, transaction))
            {
                lockCommand.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var today = await ReadAsync(connection, transaction, tenantId, cancellationToken);
            if (today.AiCalls >= dailyCallLimit)
            {
                return AiQuotaDecision.CallLimitReached;
            }

            if (today.QuotaTokens >= dailyTokenLimit)
            {
                return AiQuotaDecision.TokenLimitReached;
            }

            await UpsertAsync(connection, transaction, tenantId, "ai_calls = u.ai_calls + 1", "ai_calls", cancellationToken);
            return AiQuotaDecision.Allowed;
        }, cancellationToken);
    }

    /// <summary>Adds the tokens of a finished call; <paramref name="failed"/> also counts a provider failure.</summary>
    public Task RecordAiCallAsync(Guid tenantId, Guid? userId, AiTokenUsage usage, bool failed, CancellationToken cancellationToken) =>
        InTransactionAsync<bool>(tenantId, userId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO integrations.ai_usage AS u
                    (tenant_id, usage_hour, ai_failures, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
                VALUES (@tenant, @hour, @failures, @input, @output, @read, @write)
                ON CONFLICT (tenant_id, usage_hour) DO UPDATE SET
                    ai_failures = u.ai_failures + @failures,
                    input_tokens = u.input_tokens + @input,
                    output_tokens = u.output_tokens + @output,
                    cache_read_tokens = u.cache_read_tokens + @read,
                    cache_write_tokens = u.cache_write_tokens + @write
                """,
                connection,
                transaction);
            command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("hour", CurrentHour));
            command.Parameters.Add(new NpgsqlParameter<int>("failures", failed ? 1 : 0));
            command.Parameters.Add(new NpgsqlParameter<long>("input", Math.Max(0, usage.InputTokens)));
            command.Parameters.Add(new NpgsqlParameter<long>("output", Math.Max(0, usage.OutputTokens)));
            command.Parameters.Add(new NpgsqlParameter<long>("read", Math.Max(0, usage.CacheReadInputTokens)));
            command.Parameters.Add(new NpgsqlParameter<long>("write", Math.Max(0, usage.CacheCreationInputTokens)));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Counts one parse that used the rules parser although AI is enabled (quota, provider failure).</summary>
    public Task RecordFallbackAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken) =>
        InTransactionAsync(tenantId, userId, async (connection, transaction) =>
        {
            await UpsertAsync(connection, transaction, tenantId, "fallbacks = u.fallbacks + 1", "fallbacks", cancellationToken);
            return true;
        }, cancellationToken);

    public Task<AiUsageSnapshot> TodayAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken) =>
        InTransactionAsync(tenantId, userId, (connection, transaction) => ReadAsync(connection, transaction, tenantId, cancellationToken), cancellationToken);

    /// <summary>Start of the current usage day (Europe/Istanbul midnight) as an instant.</summary>
    public DateTimeOffset DayStart(out DateOnly day)
    {
        var local = clock.GetUtcNow().ToOffset(Istanbul);
        day = DateOnly.FromDateTime(local.DateTime);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Istanbul).ToUniversalTime();
    }

    /// <summary>Seconds until the next clock hour (the hourly limit resets then): the 429 Retry-After value.</summary>
    public int SecondsUntilNextHour()
    {
        var now = clock.GetUtcNow();
        return Math.Max(1, (int)Math.Ceiling((CurrentHour.AddHours(1) - now).TotalSeconds));
    }

    private async Task<AiUsageSnapshot> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        var start = DayStart(out var day);
        await using var command = new NpgsqlCommand(
            """
            SELECT coalesce(sum(parse_requests), 0)::int, coalesce(sum(ai_calls), 0)::int, coalesce(sum(ai_failures), 0)::int,
                   coalesce(sum(fallbacks), 0)::int, coalesce(sum(input_tokens), 0)::bigint, coalesce(sum(output_tokens), 0)::bigint,
                   coalesce(sum(cache_read_tokens), 0)::bigint, coalesce(sum(cache_write_tokens), 0)::bigint
            FROM integrations.ai_usage
            WHERE tenant_id = @tenant AND usage_hour >= @start
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("start", start));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new AiUsageSnapshot(
            day, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private async Task UpsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, string increment, string column, CancellationToken cancellationToken)
    {
        // column / increment are compile-time constants of this class, never caller input.
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO integrations.ai_usage AS u (tenant_id, usage_hour, {column}) VALUES (@tenant, @hour, 1)
            ON CONFLICT (tenant_id, usage_hour) DO UPDATE SET {increment}
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("hour", CurrentHour));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<T> InTransactionAsync<T>(
        Guid tenantId, Guid? userId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @user, true), set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter<string>("user", NpgsqlDbType.Text) { TypedValue = userId?.ToString() ?? string.Empty });
            context.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenantId.ToString() });
            await context.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await work(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static DateTimeOffset Hour(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}

using System.Globalization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// Deletes expired <c>platform.idempotency_keys</c> rows (ADR 0006 §5, retention 24 h). Runs without a tenant, so it
/// goes through <c>platform.purge_expired_idempotency_keys</c> (SECURITY DEFINER, expired rows only) instead of RLS.
/// </summary>
internal sealed partial class IdempotencyCleanupJob(UnitOfWork unitOfWork, ILogger<IdempotencyCleanupJob> logger) : IJobHandler
{
    public const string Type = "platform.idempotency_cleanup";
    public const int BatchSize = 1000;
    public const int MaxBatches = 100;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT platform.purge_expired_idempotency_keys(@limit)", unitOfWork.Connection, unitOfWork.Transaction);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} expired idempotency keys")]
    private static partial void LogPurged(ILogger logger, int count);
}

/// <summary>
/// The in-process worker (ADR 0006 §3–§5): one loop claims and runs jobs, one dispatches the outbox and enqueues the
/// periodic maintenance jobs (deduplicated per interval, so several instances enqueue once). Polling with idle backoff;
/// stopping the host cancels the loops, hands claimed-but-unfinished jobs back to the queue and waits for them.
/// </summary>
internal sealed partial class PlatformWorker(
    IServiceProvider services,
    IOptions<JobsOptions> jobsOptions,
    TimeProvider clock,
    ILogger<PlatformWorker> logger) : BackgroundService
{
    /// <summary>Tenant-less platform jobs enqueued once per maintenance interval (threat-model §3.6 allowlist).</summary>
    public static readonly IReadOnlyList<string> MaintenanceJobTypes =
        [IdempotencyCleanupJob.Type, QueueCleanupJob.Type, AuditRetentionJob.Type];

    private JobsOptions _options = null!;
    private JobStore _store = null!;
    private OutboxDispatcher _dispatcher = null!;
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobProcessor processor;
        try
        {
            _options = jobsOptions.Value;
            if (!_options.Enabled)
            {
                LogDisabled(logger);
                return Task.CompletedTask;
            }

            // Resolved here, not in the constructor: an unconfigured instance (no database) must still start and
            // serve /health; it is fail-closed instead (readiness 503, no background work).
            processor = services.GetRequiredService<JobProcessor>();
            _dispatcher = services.GetRequiredService<OutboxDispatcher>();
            _store = services.GetRequiredService<JobStore>();
        }
        catch (OptionsValidationException ex)
        {
            LogNotConfigured(logger, ex);
            return Task.CompletedTask;
        }

        LogStarted(logger, processor.WorkerId);
        return Task.WhenAll(
            PollAsync("jobs", processor.RunOnceAsync, stoppingToken),
            PollAsync("outbox", DispatchAndScheduleAsync, stoppingToken));
    }

    private async Task<int> DispatchAndScheduleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now >= _nextMaintenance)
        {
            var bucket = now.Ticks / Math.Max(_options.MaintenanceInterval.Ticks, TimeSpan.TicksPerSecond);
            foreach (var type in MaintenanceJobTypes)
            {
                await _store.InsertAsync(
                    new NewJob(null, type, "{}", null, -1, 3, $"{type}:{bucket.ToString(CultureInfo.InvariantCulture)}", null),
                    cancellationToken);
            }

            _nextMaintenance = now + _options.MaintenanceInterval;
        }

        return await _dispatcher.RunOnceAsync(cancellationToken);
    }

    private async Task PollAsync(string loop, Func<CancellationToken, Task<int>> runOnce, CancellationToken stoppingToken)
    {
        var idle = _options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await runOnce(stoppingToken);
                idle = processed > 0 ? _options.PollInterval : Min(idle * 2, _options.MaxIdleInterval);
                if (processed > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database outage etc.: keep the host alive and retry after the idle interval.
                LogLoopFailed(logger, ex, loop);
                idle = _options.MaxIdleInterval;
            }

            try
            {
                await Task.Delay(idle, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Background worker disabled (Jobs:Enabled=false)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Background worker not started: configuration is invalid")]
    private static partial void LogNotConfigured(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background worker {WorkerId} started")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Background {Loop} loop failed; retrying")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception, string loop);
}

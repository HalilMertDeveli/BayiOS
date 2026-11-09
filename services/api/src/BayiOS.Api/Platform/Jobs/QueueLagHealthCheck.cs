using BayiOS.SharedKernel.Observability;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary><c>Health:*</c> — readiness thresholds (observability.md §4).</summary>
internal sealed class HealthOptions
{
    public const string Section = "Health";

    /// <summary>Oldest due queued job older than this → readiness <c>degraded</c> (HTTP 200, alert). Default 5 min.</summary>
    public TimeSpan QueueLagDegradedAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Older than this → readiness <c>unavailable</c> (HTTP 503) — <b>only on an instance that runs the worker</b>
    /// (<c>Jobs:Enabled=true</c>): replacing it can help. An API-only instance never goes unready for queue lag (that
    /// would take the API down for a worker problem). Default 30 min; <see cref="TimeSpan.Zero"/> disables.
    /// </summary>
    public TimeSpan QueueLagUnhealthyAfter { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Readiness part 2 (#55): the age of the oldest <b>due</b> queued job of a type this deployment can run (handler
/// registered). Also feeds the <c>bayios.jobs.queue_lag</c> / <c>queue_depth</c> gauges. Uses the partial index
/// <c>ix_jobs_queued</c>; <c>platform.jobs</c> has no RLS (data-model §2.3) and the response carries no detail.
/// </summary>
internal sealed class QueueLagHealthCheck(IServiceProvider services, IOptions<HealthOptions> health) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool workerHere;
        (TimeSpan Lag, long Depth) observed;
        try
        {
            workerHere = services.GetRequiredService<IOptions<JobsOptions>>().Value.Enabled;
            var registry = services.GetRequiredService<JobHandlerRegistry>();
            observed = await MeasureAsync(services.GetRequiredService<NpgsqlDataSource>(), [.. registry.JobTypes], cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException
            or OptionsValidationException or ArgumentException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Job queue unavailable", ex);
        }

        BayiOsMetrics.ObserveQueue(observed.Lag, observed.Depth);
        return Classify(observed.Lag, health.Value, workerHere) switch
        {
            HealthStatus.Healthy => HealthCheckResult.Healthy(),
            HealthStatus.Degraded => HealthCheckResult.Degraded("Job queue lag above the degraded threshold"),
            _ => HealthCheckResult.Unhealthy("Job queue lag above the unhealthy threshold"),
        };
    }

    public static HealthStatus Classify(TimeSpan lag, HealthOptions options, bool workerRunsHere)
    {
        if (workerRunsHere && options.QueueLagUnhealthyAfter > TimeSpan.Zero && lag >= options.QueueLagUnhealthyAfter)
        {
            return HealthStatus.Unhealthy;
        }

        return lag >= options.QueueLagDegradedAfter ? HealthStatus.Degraded : HealthStatus.Healthy;
    }

    public static async Task<(TimeSpan Lag, long Depth)> MeasureAsync(NpgsqlDataSource dataSource, string[] types, CancellationToken cancellationToken)
    {
        if (types.Length == 0)
        {
            return (TimeSpan.Zero, 0);
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*), coalesce(extract(epoch FROM now() - min(run_after)), 0)::float8
            FROM platform.jobs
            WHERE status = 'queued' AND run_after <= now() AND type = ANY(@types)
            """);
        command.Parameters.Add(new NpgsqlParameter<string[]>("types", types));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (TimeSpan.FromSeconds(Math.Max(reader.GetDouble(1), 0d)), reader.GetInt64(0));
    }
}

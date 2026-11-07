using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Observability;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace BayiOS.IntegrationTests.Platform;

/// <summary>
/// #55: <c>/health/ready</c> includes the job queue lag (observability.md §4). Runs in the sequential group: the
/// aged job it inserts must not be claimed by another test's processor while the check reads it.
/// </summary>
[Collection(GlobalWorkersTestGroup.Name)]
public class QueueLagReadinessTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ready_reports_degraded_with_200_when_a_due_job_waits_longer_than_the_threshold()
    {
        var jobId = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO platform.jobs (id, type, payload, status, priority, run_after, attempts, max_attempts, dedupe_key, created_at)
            VALUES ($1, $2, '{}', 'queued', 0, now() - interval '2 hours', 0, 3, $3, now() - interval '2 hours')
            """,
            jobId, QueueCleanupJob.Type, "queue-lag-test:" + jobId.ToString("N"));
        try
        {
            await using var factory = fixture.WithWebHostBuilder(builder => builder
                .UseSetting("Health:QueueLagDegradedAfter", "00:01:00")
                .UseSetting("Health:QueueLagUnhealthyAfter", "00:00:00"));

            var response = await factory.CreateClient().GetAsync("/health/ready", Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("degraded", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["status"]);
            Assert.True(ObservedQueueLagSeconds() >= 7000, "queue lag gauge reflects the 2 h old job");
        }
        finally
        {
            await ExecuteAsync("DELETE FROM platform.jobs WHERE id = $1", jobId);
        }
    }

    [Fact]
    public async Task Queue_lag_counts_only_due_jobs_of_registered_types()
    {
        var jobId = Guid.CreateVersion7();
        var futureId = Guid.CreateVersion7();
        var unknownType = "test.unregistered." + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(
            """
            INSERT INTO platform.jobs (id, type, payload, status, priority, run_after, attempts, max_attempts, created_at)
            VALUES ($1, $2, '{}', 'queued', 0, now() - interval '3 days', 0, 3, now()),
                   ($3, $4, '{}', 'queued', 0, now() + interval '3 days', 0, 3, now())
            """,
            jobId, unknownType, futureId, QueueCleanupJob.Type);
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(fixture.AppConnectionString);
            var (lag, _) = await QueueLagHealthCheck.MeasureAsync(dataSource, [QueueCleanupJob.Type], Ct);

            // Neither the unregistered 3-day-old job nor the future job counts as lag of this deployment.
            Assert.True(lag < TimeSpan.FromDays(1), $"lag {lag} must ignore unregistered and future jobs");
        }
        finally
        {
            await ExecuteAsync("DELETE FROM platform.jobs WHERE id = ANY($1)", new[] { jobId, futureId });
        }
    }

    private static double ObservedQueueLagSeconds()
    {
        double observed = -1;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BayiOsMetrics.MeterName && instrument.Name == "bayios.jobs.queue_lag")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => observed = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return observed;
    }

    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}

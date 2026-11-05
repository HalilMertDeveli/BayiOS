using System.Text.Json;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>Payload of an <see cref="JobHandlerRegistry.OutboxDeliveryType"/> job: one message for one subscriber.</summary>
internal sealed record OutboxDelivery(Guid MessageId, string MessageType, string Subscriber, JsonElement Payload, DateTimeOffset OccurredAt);

/// <summary>
/// Claims and runs jobs (ADR 0006). Each job runs in a fresh DI scope whose <see cref="RequestContext"/> carries the
/// job's tenant (no user) and whose UnitOfWork transaction sets <c>app.tenant_id</c> to it — so the handler sees only
/// that tenant's rows through RLS, exactly like a request. Success is committed together with the handler's writes.
/// </summary>
internal sealed partial class JobProcessor(
    IServiceScopeFactory scopeFactory,
    JobStore store,
    JobHandlerRegistry registry,
    JobsOptions options,
    ILogger<JobProcessor> logger)
{
    public string WorkerId { get; } = options.ResolveWorkerId();

    /// <summary>Sweeps expired leases, claims one batch and runs it. Returns the number of jobs claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await store.SweepExpiredLeasesAsync(cancellationToken);
        var jobs = await store.ClaimAsync(WorkerId, [.. registry.JobTypes], options.BatchSize, options.Lease, cancellationToken);
        for (var i = 0; i < jobs.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Graceful shutdown: claimed but not started jobs go straight back to the queue.
                foreach (var pending in jobs.Skip(i))
                {
                    await store.ReleaseAsync(pending.Id, WorkerId, CancellationToken.None);
                }

                break;
            }

            await ExecuteAsync(jobs[i], cancellationToken);
        }

        return jobs.Count;
    }

    private async Task ExecuteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.UserId = null;
        context.Role = null;
        context.TenantId = job.TenantId;
        context.CorrelationId = job.CorrelationId ?? job.Id.ToString("N");
        var unitOfWork = services.GetRequiredService<UnitOfWork>();

        try
        {
            await unitOfWork.BeginAsync(readOnly: false, cancellationToken);
            using var payload = JsonDocument.Parse(job.Payload);
            var execution = new JobExecution(
                job.Id, job.Type, job.TenantId, payload.RootElement.Clone(), job.Attempts, job.MaxAttempts, context.CorrelationId);

            await RunHandlerAsync(services, execution, cancellationToken);

            if (!await JobStore.CompleteAsync(unitOfWork, job.Id, WorkerId, cancellationToken))
            {
                // The lease expired and the job was handed to another worker: do not commit a second effect.
                await unitOfWork.RollbackAsync(CancellationToken.None);
                LogLeaseLost(logger, job.Id, job.Type);
                return;
            }

            await unitOfWork.CommitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryRollbackAsync(unitOfWork);
            await store.ReleaseAsync(job.Id, WorkerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await TryRollbackAsync(unitOfWork);
            var status = await store.FailAsync(
                job.Id, WorkerId, JobErrorSanitizer.Sanitize(ex), options.Backoff(job.Attempts), CancellationToken.None);
            if (status == "dead")
            {
                LogJobDead(logger, ex, job.Id, job.Type, job.Attempts);
            }
            else
            {
                LogJobFailed(logger, ex, job.Id, job.Type, job.Attempts, job.MaxAttempts);
            }
        }
    }

    private async Task RunHandlerAsync(IServiceProvider services, JobExecution execution, CancellationToken cancellationToken)
    {
        if (execution.Type != JobHandlerRegistry.OutboxDeliveryType)
        {
            var handler = registry.Jobs.TryGetValue(execution.Type, out var create)
                ? create(services)
                : throw new InvalidOperationException($"No handler is registered for job type '{execution.Type}'.");
            await handler.HandleAsync(execution, cancellationToken);
            return;
        }

        var delivery = execution.PayloadAs<OutboxDelivery>();
        var subscription = registry.Outbox.GetValueOrDefault(delivery.MessageType)?.FirstOrDefault(s => s.Name == delivery.Subscriber)
            ?? throw new InvalidOperationException($"Outbox subscriber '{delivery.Subscriber}' is not registered.");
        var message = new OutboxMessage(
            delivery.MessageId, delivery.MessageType, execution.TenantId, delivery.Payload, execution.CorrelationId, delivery.OccurredAt);
        await subscription.Create(services).HandleAsync(message, cancellationToken);
    }

    private static async Task TryRollbackAsync(UnitOfWork unitOfWork)
    {
        try
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Broken connection: disposing the scope rolls the transaction back.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) attempt {Attempt}/{MaxAttempts} failed; retrying with backoff")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, Guid jobId, string jobType, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId} ({JobType}) is dead after {Attempts} attempts")]
    private static partial void LogJobDead(ILogger logger, Exception exception, Guid jobId, string jobType, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) lost its lease; its result was rolled back")]
    private static partial void LogLeaseLost(ILogger logger, Guid jobId, string jobType);
}

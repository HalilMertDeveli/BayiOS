using System.Text.Json;
using BayiOS.Api.Platform.Observability;
using BayiOS.SharedKernel.Observability;
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
    ILogger<JobProcessor> logger,
    IErrorReporter? errorReporter = null)
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
            using var payload = JsonDocument.Parse(job.Payload);
            var execution = new JobExecution(
                job.Id, job.Type, job.TenantId, payload.RootElement.Clone(), job.Attempts, job.MaxAttempts, context.CorrelationId);

            // Resolving a handler opens no connection (the UnitOfWork connects on BeginAsync).
            var handler = execution.Type == JobHandlerRegistry.OutboxDeliveryType ? null
                : registry.Jobs.TryGetValue(execution.Type, out var create) ? create(services)
                : throw new InvalidOperationException($"No handler is registered for job type '{execution.Type}'.");
            if (handler is IExternalCallJobHandler external)
            {
                await ExecuteExternalAsync(services, external, job, execution, cancellationToken);
                return;
            }

            await unitOfWork.BeginAsync(readOnly: false, cancellationToken);
            await RunHandlerAsync(services, handler, execution, cancellationToken);

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
            var jobType = new KeyValuePair<string, object?>("job.type", job.Type);
            if (status == "dead")
            {
                BayiOsMetrics.JobsDeadLettered.Add(1, jobType);
                LogJobDead(logger, ex, job.Id, job.Type, job.Attempts);
                errorReporter?.Report(ex, new ErrorContext(ErrorSources.Job, job.Type, job.TenantId, null, context.CorrelationId));
            }
            else if (status is not null)
            {
                BayiOsMetrics.JobsRetried.Add(1, jobType);
                LogJobFailed(logger, ex, job.Id, job.Type, job.Attempts, job.MaxAttempts);
            }
            else
            {
                LogJobFailed(logger, ex, job.Id, job.Type, job.Attempts, job.MaxAttempts);
            }
        }
    }

    /// <summary>
    /// An <see cref="IExternalCallJobHandler"/>: no unit of work is started, so no connection is held while the handler
    /// waits on the external service; it opens short transactions through <see cref="IJobTransactions"/>. When it did
    /// not complete the job itself, the job is marked succeeded on a short transaction of its own afterwards.
    /// </summary>
    private async Task ExecuteExternalAsync(
        IServiceProvider services, IExternalCallJobHandler handler, ClaimedJob job, JobExecution execution, CancellationToken cancellationToken)
    {
        var transactions = services.GetRequiredService<JobTransactions>();
        transactions.Bind(job, WorkerId, execution.CorrelationId);
        await handler.HandleAsync(execution, cancellationToken);
        if (transactions.Outcome is null)
        {
            await transactions.CompleteAsync((_, _) => Task.CompletedTask, cancellationToken);
        }

        if (transactions.Outcome == false)
        {
            LogLeaseLost(logger, job.Id, job.Type);
        }
    }

    private async Task RunHandlerAsync(IServiceProvider services, IJobHandler? handler, JobExecution execution, CancellationToken cancellationToken)
    {
        if (handler is not null)
        {
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

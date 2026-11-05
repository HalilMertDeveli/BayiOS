using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.SharedKernel.Platform.Jobs;

/// <summary>
/// Enqueues background jobs into <c>platform.jobs</c> (ADR 0006) on the current UnitOfWork transaction: the job
/// exists only if the business change commits. The job's tenant is the current tenant (null outside a tenant).
/// </summary>
public interface IBackgroundJobs
{
    /// <returns>The job id, or null when a live job with the same <see cref="JobRequest.DedupeKey"/> already exists.</returns>
    Task<Guid?> EnqueueAsync(JobRequest job, CancellationToken cancellationToken);
}

/// <param name="Type">Handler key, e.g. <c>imports.validate</c>; must have a registered <see cref="IJobHandler"/>.</param>
/// <param name="Payload">Serialized as JSON (web defaults). Ids only — never secrets or personal data.</param>
/// <param name="DedupeKey">At most one queued/running job per (type, key), e.g. <c>reconcile:2026-11-04</c>.</param>
public sealed record JobRequest(
    string Type,
    object? Payload = null,
    DateTimeOffset? RunAfter = null,
    short Priority = 0,
    int MaxAttempts = 5,
    string? DedupeKey = null);

/// <summary>
/// Runs one job. The worker calls it in a fresh DI scope with its own UnitOfWork transaction whose
/// <c>app.tenant_id</c> is the job's tenant, so module DbContexts, RLS and <c>IAuditLog</c> behave as in a request.
/// Handlers must be idempotent: a job can run again after a crash or an expired lease. Throwing fails the attempt
/// (retried with backoff, then <c>dead</c>); the transaction is rolled back.
/// </summary>
public interface IJobHandler
{
    Task HandleAsync(JobExecution job, CancellationToken cancellationToken);
}

/// <summary>A claimed job as seen by its handler.</summary>
public sealed record JobExecution(
    Guid Id,
    string Type,
    Guid? TenantId,
    JsonElement Payload,
    int Attempt,
    int MaxAttempts,
    string? CorrelationId)
{
    public T PayloadAs<T>() => Payload.Deserialize<T>(JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException($"Job {Id} has an empty payload.");
}

/// <summary>
/// Publishes integration events into <c>platform.outbox_messages</c> on the current UnitOfWork transaction
/// (transactional outbox, ADR 0006): a rolled-back business change never publishes.
/// </summary>
public interface IOutbox
{
    /// <param name="type">Versioned event type, e.g. <c>orders.order_approved.v1</c>.</param>
    /// <returns>The message id (consumers are idempotent on it).</returns>
    Task<Guid> PublishAsync(string type, object payload, CancellationToken cancellationToken);
}

/// <summary>
/// Consumes one outbox message type. The dispatcher turns each message into one job per subscribed handler, so
/// delivery is at least once with the job queue's retry, backoff, dead-letter and tenant context.
/// </summary>
public interface IOutboxHandler
{
    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed record OutboxMessage(
    Guid Id,
    string Type,
    Guid? TenantId,
    JsonElement Payload,
    string? CorrelationId,
    DateTimeOffset OccurredAt)
{
    public T PayloadAs<T>() => Payload.Deserialize<T>(JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException($"Outbox message {Id} has an empty payload.");
}

/// <summary>
/// Thrown by <see cref="IBackgroundJobs.EnqueueAsync"/> for a job type with no registered handler
/// (<see cref="JobServiceCollectionExtensions.AddJobHandler{THandler}"/>) or a reserved internal type. A programming
/// error: the surrounding unit of work must not commit.
/// </summary>
public sealed class UnknownJobTypeException(string type)
    : InvalidOperationException($"Job type '{type}' has no registered handler (AddJobHandler); it cannot be enqueued.")
{
    public string JobType { get; } = type;
}

/// <summary>Registration of a job handler for one job type.</summary>
public sealed record JobHandlerRegistration(string Type, Type HandlerType);

/// <summary>Registration of an outbox handler for one message type; <see cref="Name"/> is stable across releases.</summary>
public sealed record OutboxHandlerRegistration(string MessageType, string Name, Type HandlerType);

public static class JobServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="THandler"/> (scoped) as the handler of job <paramref name="type"/>.</summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string type)
        where THandler : class, IJobHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        services.AddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(type, typeof(THandler)));
        return services;
    }

    /// <summary>Subscribes <typeparamref name="THandler"/> (scoped) to outbox messages of <paramref name="messageType"/>.</summary>
    public static IServiceCollection AddOutboxHandler<THandler>(this IServiceCollection services, string messageType)
        where THandler : class, IOutboxHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        services.AddScoped<THandler>();
        services.AddSingleton(new OutboxHandlerRegistration(messageType, typeof(THandler).FullName!, typeof(THandler)));
        return services;
    }
}

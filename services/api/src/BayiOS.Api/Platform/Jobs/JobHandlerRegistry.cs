using BayiOS.SharedKernel.Platform.Jobs;

namespace BayiOS.Api.Platform.Jobs;

internal sealed class JobsOptions
{
    public const string Section = "Jobs";

    /// <summary>Runs the job worker and outbox dispatcher in this process (ADR 0006 §4). Off → a separate worker runs them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lease owner name; defaults to machine name + process id + a random suffix.</summary>
    public string? WorkerId { get; set; }

    public int BatchSize { get; set; } = 10;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Idle polling backs off up to this interval.</summary>
    public TimeSpan MaxIdleInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Retry delay after attempt n: <c>BackoffBase × 2^(n-1)</c>, capped at <see cref="BackoffMax"/>.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the scheduler enqueues maintenance jobs (idempotency keys, queue cleanup, audit retention).</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Succeeded jobs are deleted this long after they finished (<c>platform.queue_cleanup</c>, #89).</summary>
    public TimeSpan SucceededRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Dead jobs are kept longer for investigation, then deleted.</summary>
    public TimeSpan DeadRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Processed outbox messages are deleted this long after dispatch (their deliveries are jobs of their own).</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan Backoff(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var ticks = Math.Min(BackoffBase.Ticks * Math.Pow(2, exponent), BackoffMax.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    public string ResolveWorkerId() =>
        string.IsNullOrWhiteSpace(WorkerId)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}"
            : WorkerId;
}

internal sealed record OutboxSubscription(string Name, Func<IServiceProvider, IOutboxHandler> Create);

/// <summary>Job type → handler and outbox message type → subscribers, built once at startup.</summary>
internal sealed class JobHandlerRegistry
{
    /// <summary>Internal job type that delivers one outbox message to one subscriber.</summary>
    public const string OutboxDeliveryType = "platform.outbox_delivery";

    public JobHandlerRegistry(
        IReadOnlyDictionary<string, Func<IServiceProvider, IJobHandler>> jobs,
        IReadOnlyDictionary<string, IReadOnlyList<OutboxSubscription>> outbox)
    {
        if (jobs.ContainsKey(OutboxDeliveryType))
        {
            throw new InvalidOperationException($"Job type '{OutboxDeliveryType}' is reserved.");
        }

        Jobs = jobs;
        Outbox = outbox;
        JobTypes = [.. jobs.Keys, OutboxDeliveryType];
    }

    public IReadOnlyDictionary<string, Func<IServiceProvider, IJobHandler>> Jobs { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<OutboxSubscription>> Outbox { get; }

    /// <summary>The types this worker claims; jobs of other types stay queued for a worker that knows them.</summary>
    public IReadOnlyList<string> JobTypes { get; }

    /// <summary>True when a handler is registered for <paramref name="type"/>; the reserved delivery type is not.</summary>
    public bool IsRegistered(string type) => Jobs.ContainsKey(type);

    public static JobHandlerRegistry FromRegistrations(
        IEnumerable<JobHandlerRegistration> jobs, IEnumerable<OutboxHandlerRegistration> outbox)
    {
        var jobMap = new Dictionary<string, Func<IServiceProvider, IJobHandler>>(StringComparer.Ordinal);
        foreach (var registration in jobs)
        {
            var type = registration.HandlerType;
            if (!jobMap.TryAdd(registration.Type, sp => (IJobHandler)sp.GetRequiredService(type)))
            {
                throw new InvalidOperationException($"Job type '{registration.Type}' has more than one handler.");
            }
        }

        var outboxMap = outbox
            .GroupBy(r => r.MessageType, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<OutboxSubscription>)[.. g.Select(r =>
                {
                    var type = r.HandlerType;
                    return new OutboxSubscription(r.Name, sp => (IOutboxHandler)sp.GetRequiredService(type));
                })],
                StringComparer.Ordinal);
        return new JobHandlerRegistry(jobMap, outboxMap);
    }
}

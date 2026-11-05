using BayiOS.Api.Platform.Audit;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>Audit log, job queue, outbox and the in-process background worker (ADR 0006).</summary>
internal static class PlatformRuntime
{
    public static IServiceCollection AddPlatformRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        // A wrong secret is a deploy error: fail at startup rather than audit with a weak key (data-policy §8.5).
        services.AddOptions<AuditOptions>().BindConfiguration(AuditOptions.Section)
            .Validate(o => AuditOptions.IsValidIpHashKey(o.IpHashKey), $"Audit:IpHashKey must be empty (no IP hash) or at least {AuditOptions.MinIpHashKeyBytes} bytes.")
            .Validate(o => o.Retention >= AuditOptions.MinRetention, "Audit:Retention cannot be shorter than 730 days (data-policy §4).")
            .ValidateOnStart();
        services.AddScoped<IAuditLog, AuditLog>();

        services.AddOptions<JobsOptions>().BindConfiguration(JobsOptions.Section)
            .Validate(o => o.BatchSize is >= 1 and <= 100, "Jobs:BatchSize must be between 1 and 100.")
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.MaxIdleInterval >= o.PollInterval, "Jobs poll intervals are invalid.")
            .Validate(o => o.Lease >= TimeSpan.FromSeconds(10), "Jobs:Lease must be at least 10 seconds.")
            .Validate(
                o => o.SucceededRetention >= TimeSpan.FromHours(1) && o.DeadRetention >= TimeSpan.FromHours(1) && o.OutboxRetention >= TimeSpan.FromHours(1),
                "Jobs retention periods must be at least 1 hour.");
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<JobsOptions>>().Value);
        services.AddScoped<IBackgroundJobs, BackgroundJobs>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddSingleton<JobStore>();
        services.AddSingleton(sp => JobHandlerRegistry.FromRegistrations(
            sp.GetServices<JobHandlerRegistration>(), sp.GetServices<OutboxHandlerRegistration>()));
        services.AddSingleton<JobProcessor>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService<PlatformWorker>();

        services.AddJobHandler<IdempotencyCleanupJob>(IdempotencyCleanupJob.Type);
        services.AddJobHandler<QueueCleanupJob>(QueueCleanupJob.Type);
        services.AddJobHandler<AuditRetentionJob>(AuditRetentionJob.Type);
        return services;
    }

    public static IEndpointRouteBuilder MapPlatformRuntime(this IEndpointRouteBuilder app) => app.MapAuditEndpoints();
}

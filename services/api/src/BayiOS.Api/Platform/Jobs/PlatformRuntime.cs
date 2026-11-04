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

        services.AddOptions<AuditOptions>().BindConfiguration("Audit");
        services.AddScoped<IAuditLog, AuditLog>();

        services.AddOptions<JobsOptions>().BindConfiguration(JobsOptions.Section)
            .Validate(o => o.BatchSize is >= 1 and <= 100, "Jobs:BatchSize must be between 1 and 100.")
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.MaxIdleInterval >= o.PollInterval, "Jobs poll intervals are invalid.")
            .Validate(o => o.Lease >= TimeSpan.FromSeconds(10), "Jobs:Lease must be at least 10 seconds.");
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
        return services;
    }

    public static IEndpointRouteBuilder MapPlatformRuntime(this IEndpointRouteBuilder app) => app.MapAuditEndpoints();
}

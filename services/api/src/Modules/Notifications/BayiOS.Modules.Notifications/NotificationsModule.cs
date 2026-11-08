using BayiOS.Modules.Notifications.Api;
using BayiOS.Modules.Notifications.Application;
using BayiOS.Modules.Notifications.Infrastructure.Persistence;
using BayiOS.Modules.Notifications.Push;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Notifications;

public static class NotificationsModule
{
    /// <summary>The named HttpClient of the FCM sender and its Google OAuth calls (tests replace its handler).</summary>
    public const string FcmHttpClient = "fcm";

    /// <summary>Tenant job sending one push to one user's devices in the job's tenant.</summary>
    public const string PushSendJobType = "notifications.push_send";

    /// <summary>Tenant-less maintenance job deleting stale registrations (scheduled by the platform worker).</summary>
    public const string DeviceCleanupJobType = "notifications.device_cleanup";

    /// <summary>
    /// Push notifications (issue #44, data-model §5.11): device registrations (<c>/api/v1/devices</c>), the
    /// subscriber that turns <c>warehouse.picking_task_assigned.v1</c> into a push job, the FCM HTTP v1 sender behind
    /// <see cref="IPushSender"/> (no-op unless <c>Push:Enabled=true</c>) and the stale-registration cleanup. The host
    /// schedules <see cref="DeviceCleanupJobType"/> as a tenant-less maintenance job.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<NotificationsDbContext>((sp, options) =>
            NotificationsDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddOptions<PushOptions>().BindConfiguration(PushOptions.Section)
            .ValidateDataAnnotations()
            .Validate<IConfiguration>(
                (o, configuration) => !o.Enabled
                    || !string.IsNullOrWhiteSpace(configuration[PushOptions.ServiceAccountJsonKey])
                    || File.Exists(o.ServiceAccountFile),
                "Push:Enabled=true needs the Firebase service-account key: Push:ServiceAccountJson (secret) or Push:ServiceAccountFile.")
            .ValidateOnStart();

        services.AddHttpClient(FcmHttpClient, (sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<PushOptions>>().Value.TimeoutSeconds));
        services.TryAddSingleton<IPushSender>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PushOptions>>().Value;
            if (!options.Enabled)
            {
                return new DisabledPushSender(sp.GetRequiredService<ILogger<DisabledPushSender>>());
            }

            // Server-side secret only (data-policy §9): parsed once, never logged.
            var json = sp.GetRequiredService<IConfiguration>()[PushOptions.ServiceAccountJsonKey];
            var key = ServiceAccountKey.Parse(string.IsNullOrWhiteSpace(json) ? File.ReadAllText(options.ServiceAccountFile!) : json);
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient Http() => factory.CreateClient(FcmHttpClient);
            var tokens = new GoogleAccessTokenSource(key, Http, sp.GetRequiredService<TimeProvider>());
            return new FcmPushSender(Http, tokens, options.ProjectId ?? key.ProjectId, sp.GetRequiredService<ILogger<FcmPushSender>>());
        });

        services.AddScoped<DeviceService>();
        services.AddOutboxHandler<PickingTaskAssignedHandler>(PickingTaskAssignedHandler.MessageType);
        services.AddJobHandler<PushSendJob>(PushSendJob.Type);
        services.AddJobHandler<StaleDeviceCleanupJob>(StaleDeviceCleanupJob.Type);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsModule(this IEndpointRouteBuilder app) => app.MapDeviceEndpoints();
}

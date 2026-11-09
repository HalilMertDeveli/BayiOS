using BayiOS.Modules.Billing.Api;
using BayiOS.Modules.Billing.Application;
using BayiOS.Modules.Billing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BayiOS.Modules.Billing;

public static class BillingModule
{
    /// <summary>Tenant-less maintenance job that records ended trials (scheduled by the platform worker).</summary>
    public const string ExpiryJobType = "billing.subscription_expiry";

    /// <summary>
    /// Plans, add-ons and entitlements (#51/#52): <see cref="IEntitlements"/> and the entitlement gate for every
    /// module, the trial provisioning of new tenants (<see cref="ITenantSubscriptionProvisioner"/>), the module store
    /// endpoints and the expiry job. The host may provide <see cref="Contracts.IBillingUsageSource"/> for usage numbers.
    /// </summary>
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<BillingDbContext>((sp, options) =>
            BillingDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddOptions<BillingOptions>().BindConfiguration(BillingOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddScoped<EntitlementService>();
        services.AddScoped<IEntitlements>(sp => sp.GetRequiredService<EntitlementService>());
        services.AddScoped<ITenantSubscriptionProvisioner>(sp => sp.GetRequiredService<EntitlementService>());
        services.AddEntitlementGate();
        services.AddScoped<BillingService>();
        services.AddJobHandler<SubscriptionExpiryJob>(ExpiryJobType);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapBillingModule(this IEndpointRouteBuilder app) => app.MapBillingEndpoints();
}

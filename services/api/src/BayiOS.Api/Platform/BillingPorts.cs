using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Identity.Domain;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapter for the Billing usage port: the numbers behind each limit, read from the owning modules
/// (their DbContexts — tenant query filter + RLS on the request connection — and the AI usage meter). Read-only.
/// </summary>
internal static class BillingPorts
{
    public static IServiceCollection AddBillingPorts(this IServiceCollection services)
    {
        services.AddScoped<IBillingUsageSource, ModuleBillingUsageSource>();
        return services;
    }
}

internal sealed class ModuleBillingUsageSource(
    WarehouseDbContext warehouses,
    IdentityDbContext identity,
    AiUsageMeter ai,
    IRequestContext requestContext) : IBillingUsageSource
{
    private const string ActiveWarehouse = "active";

    public async Task<IReadOnlyDictionary<string, long>> CurrentAsync(CancellationToken cancellationToken)
    {
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");
        return new Dictionary<string, long>
        {
            [EntitlementCodes.MaxWarehouses] = await warehouses.Warehouses.CountAsync(w => w.Status == ActiveWarehouse, cancellationToken),
            [EntitlementCodes.MaxUsers] = await identity.Memberships.CountAsync(
                m => m.TenantId == tenantId && m.Status == MembershipStatuses.Active, cancellationToken),
            [EntitlementCodes.AiParsesPerMonth] = await ai.MonthToDateParsesAsync(tenantId, requestContext.UserId, cancellationToken),
        };
    }
}

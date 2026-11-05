using BayiOS.Modules.Identity.Api;
using BayiOS.Modules.Identity.Application;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Domain;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        // The context runs on the request's shared connection; the UnitOfWork owns its lifetime and transaction.
        services.AddDbContext<IdentityDbContext>((sp, options) =>
            IdentityDbContext.Configure(options, sp.GetRequiredService<UnitOfWork>().Connection));
        services.AddScoped<IMembershipLookup, MembershipLookup>();
        services.AddScoped<IdentityService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder app) => app.MapIdentityEndpoints();
}

internal sealed class MembershipLookup(IdentityDbContext db) : IMembershipLookup
{
    public Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.TenantId == tenantId && m.Status == MembershipStatuses.Active)
            .Select(m => m.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<TenantAccess?> FindAccessAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        (from m in db.Memberships.AsNoTracking()
         join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
         where m.UserId == userId && m.TenantId == tenantId && m.Status == MembershipStatuses.Active
         select new TenantAccess(m.Role, t.Status))
        .SingleOrDefaultAsync(cancellationToken);
}

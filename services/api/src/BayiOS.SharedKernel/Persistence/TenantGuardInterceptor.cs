using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BayiOS.SharedKernel.Persistence;

/// <summary>A DbContext whose query filters and writes are bound to the current request's tenant.</summary>
public interface ITenantScopedDbContext
{
    Guid? CurrentTenantId { get; }
}

/// <summary>
/// SaveChanges guard (data-model §2.4): stamps <c>tenant_id</c> on new tenant-owned entities and rejects any
/// insert/update/delete whose <c>tenant_id</c> differs from the request tenant. RLS is the second line of defence.
/// </summary>
public sealed class TenantGuardInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Guard(DbContext? context)
    {
        if (context is not ITenantScopedDbContext scoped)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var tenantId = scoped.CurrentTenantId
                ?? throw new TenantMismatchException("Tenant-owned data cannot be changed without a tenant context.");
            var property = entry.Property(nameof(ITenantOwned.TenantId));

            if (entry.State == EntityState.Added && (Guid)property.CurrentValue! == Guid.Empty)
            {
                property.CurrentValue = tenantId;
            }

            if ((Guid)property.CurrentValue! != tenantId
                || (entry.State != EntityState.Added && (Guid)property.OriginalValue! != tenantId))
            {
                throw new TenantMismatchException("Attempted to write data that belongs to a different tenant.");
            }
        }
    }
}

public sealed class TenantMismatchException(string message) : InvalidOperationException(message);

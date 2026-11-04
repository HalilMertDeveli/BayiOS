using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Warehouse fulfilment ports (same approach as <see cref="InventoryPorts"/>):
/// Catalog through its DbContext (tenant query filters plus RLS, request connection and transaction) and Identity
/// through its membership contract.
/// </summary>
internal static class FulfillmentPorts
{
    public static IServiceCollection AddFulfillmentPorts(this IServiceCollection services)
    {
        services.AddScoped<IFulfillmentCatalog, CatalogFulfillmentCatalog>();
        services.AddScoped<ITaskAssigneeDirectory, MembershipTaskAssigneeDirectory>();
        return services;
    }
}

/// <summary>Scans resolve by barcode first (exact, trimmed — Catalog's barcode rule), then by SKU (upper-case).</summary>
internal sealed class CatalogFulfillmentCatalog(CatalogDbContext catalog) : IFulfillmentCatalog
{
    public async Task<IReadOnlyDictionary<Guid, FulfillmentVariantInfo>> FindVariantsAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        await (from variant in catalog.Variants.AsNoTracking()
               join unit in catalog.Units.AsNoTracking() on variant.UnitId equals unit.Id
               where variantIds.Contains(variant.Id)
               select new FulfillmentVariantInfo(variant.Id, variant.Sku, variant.Barcode, unit.AllowsFraction))
            .ToDictionaryAsync(v => v.Id, cancellationToken);

    public async Task<Guid?> ResolveCodeAsync(string code, CancellationToken cancellationToken)
    {
        var barcode = code.Trim();
        var byBarcode = await catalog.Variants.AsNoTracking()
            .Where(v => v.Barcode == barcode)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (byBarcode is not null)
        {
            return byBarcode;
        }

        var sku = barcode.ToUpperInvariant();
        return await catalog.Variants.AsNoTracking()
            .Where(v => v.Sku == sku)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>Assignees must hold an active membership in the request's tenant.</summary>
internal sealed class MembershipTaskAssigneeDirectory(IMembershipLookup memberships, IRequestContext requestContext) : ITaskAssigneeDirectory
{
    public Task<string?> FindActiveRoleAsync(Guid userId, CancellationToken cancellationToken) =>
        memberships.FindActiveRoleAsync(userId, requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved."), cancellationToken);
}

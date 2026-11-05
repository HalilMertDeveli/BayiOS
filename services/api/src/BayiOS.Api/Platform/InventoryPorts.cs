using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.Modules.Inventory.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Inventory ports that Catalog and Identity do not (yet) expose as contracts.
/// They read through those modules' own DbContexts — tenant query filters plus RLS, same request connection and
/// transaction — and are the only place the host couples to them. Replace with module contracts when available.
/// </summary>
internal static class InventoryPorts
{
    public static IServiceCollection AddInventoryPorts(this IServiceCollection services)
    {
        services.AddScoped<IVariantDirectory, CatalogVariantDirectory>();
        services.AddScoped<INegativeStockPolicyProvider, TenantSettingsNegativeStockPolicy>();
        return services;
    }
}

/// <summary>Stock identity = Catalog variant; fractions follow the variant's unit (<c>allows_fraction</c>).</summary>
internal sealed class CatalogVariantDirectory(CatalogDbContext catalog) : IVariantDirectory
{
    private const string ActiveStatus = "active";

    public async Task<IReadOnlyDictionary<Guid, VariantInfo>> FindVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        await (from variant in catalog.Variants.AsNoTracking()
               join unit in catalog.Units.AsNoTracking() on variant.UnitId equals unit.Id
               where variantIds.Contains(variant.Id)
               select new VariantInfo(variant.Id, variant.Sku, variant.Status == ActiveStatus, unit.AllowsFraction))
            .ToDictionaryAsync(v => v.Id, cancellationToken);

    /// <summary>Same matching as Catalog's own search (pg_trgm GIN indexes on SKU and product name; exact barcode).</summary>
    public async Task<IReadOnlyList<Guid>> SearchVariantIdsAsync(string term, int max, CancellationToken cancellationToken)
    {
        var pattern = CatalogText.ContainsPattern(term);
        return await (from variant in catalog.Variants.AsNoTracking()
                      join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
                      where EF.Functions.ILike(variant.Sku, pattern, "\\")
                          || variant.Barcode == term
                          || (variant.Name != null && EF.Functions.ILike(variant.Name, pattern, "\\"))
                          || EF.Functions.ILike(product.Name, pattern, "\\")
                      select variant.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Tenant setting <c>negative_stock_policy</c> (PRD TEN-12); <c>block</c> when the tenant has no settings row.</summary>
internal sealed class TenantSettingsNegativeStockPolicy(IdentityDbContext identity) : INegativeStockPolicyProvider
{
    public async Task<string> GetPolicyAsync(CancellationToken cancellationToken) =>
        await identity.TenantSettings.AsNoTracking()
            .Select(s => s.NegativeStockPolicy)
            .SingleOrDefaultAsync(cancellationToken)
        ?? NegativeStockPolicies.Block;
}

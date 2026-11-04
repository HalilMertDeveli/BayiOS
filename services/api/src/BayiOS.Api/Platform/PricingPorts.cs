using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Pricing ports that Catalog and Identity do not (yet) expose as contracts. Same
/// approach as <see cref="InventoryPorts"/>: they read through those modules' DbContexts (tenant query filters plus
/// RLS) on the request connection and transaction. The customer port is implemented by the Customers module.
/// </summary>
internal static class PricingPorts
{
    public static IServiceCollection AddPricingPorts(this IServiceCollection services)
    {
        services.AddScoped<IPricingVariantDirectory, CatalogPricingVariantDirectory>();
        services.AddScoped<ITenantCurrencyProvider, IdentityTenantCurrencyProvider>();
        return services;
    }
}

/// <summary>Price identity = Catalog variant; the VAT rate is its product's <c>tax_rate</c> (CAT-01).</summary>
internal sealed class CatalogPricingVariantDirectory(CatalogDbContext catalog) : IPricingVariantDirectory
{
    private const string ActiveStatus = "active";

    public async Task<IReadOnlyDictionary<Guid, PricedVariantInfo>> FindVariantsAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        await (from variant in catalog.Variants.AsNoTracking()
               join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
               where variantIds.Contains(variant.Id)
               select new PricedVariantInfo(variant.Id, variant.Sku, variant.Status == ActiveStatus, product.TaxRate))
            .ToDictionaryAsync(v => v.Id, cancellationToken);
}

/// <summary>The current tenant's <c>default_currency</c> (TEN-05); 'TRY' if the tenant row is not visible.</summary>
internal sealed class IdentityTenantCurrencyProvider(IdentityDbContext identity, IRequestContext requestContext) : ITenantCurrencyProvider
{
    public async Task<string> GetDefaultCurrencyAsync(CancellationToken cancellationToken)
    {
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");
        return await identity.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.DefaultCurrency)
            .SingleOrDefaultAsync(cancellationToken)
            ?? "TRY";
    }
}

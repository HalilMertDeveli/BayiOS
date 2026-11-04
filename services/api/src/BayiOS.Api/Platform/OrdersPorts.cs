using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Orders.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapter for the Orders port Catalog does not (yet) expose as a contract. Same approach as
/// <see cref="InventoryPorts"/>: it reads through the Catalog DbContext (tenant query filters plus RLS) on the request
/// connection and transaction. Customers, warehouses, prices and stock come from module contracts.
/// </summary>
internal static class OrdersPorts
{
    public static IServiceCollection AddOrdersPorts(this IServiceCollection services)
    {
        services.AddScoped<IOrderVariantDirectory, CatalogOrderVariantDirectory>();
        return services;
    }
}

/// <summary>Line snapshot source: SKU, variant (else product) name, unit code; active = variant and product active.</summary>
internal sealed class CatalogOrderVariantDirectory(CatalogDbContext catalog) : IOrderVariantDirectory
{
    private const string ActiveStatus = "active";

    public async Task<IReadOnlyDictionary<Guid, OrderVariantInfo>> FindVariantsAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        await (from variant in catalog.Variants.AsNoTracking()
               join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
               join unit in catalog.Units.AsNoTracking() on variant.UnitId equals unit.Id
               where variantIds.Contains(variant.Id)
               select new OrderVariantInfo(
                   variant.Id,
                   variant.Sku,
                   variant.Name ?? product.Name,
                   unit.Code,
                   variant.Status == ActiveStatus && product.Status == ActiveStatus,
                   unit.AllowsFraction))
            .ToDictionaryAsync(v => v.Id, cancellationToken);
}

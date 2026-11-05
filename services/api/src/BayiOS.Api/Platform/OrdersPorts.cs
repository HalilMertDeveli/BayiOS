using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Warehouse.Infrastructure.Persistence;
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
        services.AddScoped<IOrderReferenceNames, OrderReferenceNames>();
        services.AddScoped<IOrderSettingsProvider, TenantOrderSettings>();
        services.AddScoped<IOrderCustomerAddresses, CustomerOrderAddresses>();
        return services;
    }
}

/// <summary>Order settings from <c>identity.tenant_settings</c> (request tenant: query filter + RLS); defaults without a row.</summary>
internal sealed class TenantOrderSettings(IdentityDbContext identity) : IOrderSettingsProvider
{
    private OrderSettings? _settings;

    public async Task<OrderSettings> GetAsync(CancellationToken cancellationToken) =>
        _settings ??= await identity.TenantSettings.AsNoTracking()
            .Select(s => new OrderSettings(s.RequireOrderApproval, s.OrderNumberPrefix))
            .SingleOrDefaultAsync(cancellationToken)
        ?? OrderSettings.Defaults;
}

/// <summary>Customer addresses through the Customers DbContext (tenant query filter + RLS, request connection).</summary>
internal sealed class CustomerOrderAddresses(CustomersDbContext customers) : IOrderCustomerAddresses
{
    public async Task<IReadOnlyList<OrderCustomerAddress>> ListAsync(Guid customerId, CancellationToken cancellationToken) =>
        await customers.Addresses.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new OrderCustomerAddress(
                a.Id, a.Type, a.IsDefault, a.Title, a.Line1, a.Line2, a.District, a.City, a.PostalCode, a.Country, a.ContactName, a.ContactPhone))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Names for order responses: customers and warehouses through their modules' DbContexts (tenant query filters plus
/// RLS, request connection and transaction), users through Identity's <see cref="IUserDirectory"/>. One query per batch.
/// </summary>
internal sealed class OrderReferenceNames(CustomersDbContext customers, WarehouseDbContext warehouses, IUserDirectory users) : IOrderReferenceNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> CustomerNamesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        customerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await customers.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> WarehouseNamesAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken) =>
        warehouseIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await warehouses.Warehouses.AsNoTracking()
                .Where(w => warehouseIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        users.DisplayNamesAsync(userIds, cancellationToken);
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

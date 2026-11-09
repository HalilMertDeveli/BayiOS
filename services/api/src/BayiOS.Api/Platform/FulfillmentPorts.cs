using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.Modules.Orders.Infrastructure.Persistence;
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
        services.AddScoped<ITaskOrderDirectory, OrdersTaskOrderDirectory>();
        services.AddScoped<IFulfillmentSettings, TenantFulfillmentSettings>();
        return services;
    }
}

/// <summary>
/// Task list facts (WHT-03, #98) through the Orders and Customers DbContexts (tenant query filters plus RLS, request
/// connection): the customer name is read live, so an anonymised customer shows its masked name on tasks too.
/// </summary>
internal sealed class OrdersTaskOrderDirectory(OrdersDbContext orders, CustomersDbContext customers) : ITaskOrderDirectory
{
    public async Task<IReadOnlyDictionary<Guid, TaskOrderInfo>> DescribeAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<Guid, TaskOrderInfo>();
        }

        var rows = await orders.Orders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.CustomerId, o.RequestedDeliveryDate })
            .ToListAsync(cancellationToken);
        var customerIds = rows.Select(r => r.CustomerId).Distinct().ToList();
        var names = await customers.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return rows.ToDictionary(r => r.Id, r => new TaskOrderInfo(names.GetValueOrDefault(r.CustomerId), r.RequestedDeliveryDate));
    }
}

/// <summary><c>identity.tenant_settings.auto_create_picking_task</c> (request tenant); default true without a row.</summary>
internal sealed class TenantFulfillmentSettings(IdentityDbContext identity) : IFulfillmentSettings
{
    public async Task<bool> AutoCreatePickingTaskAsync(CancellationToken cancellationToken) =>
        await identity.TenantSettings.AsNoTracking()
            .Select(s => (bool?)s.AutoCreatePickingTask)
            .SingleOrDefaultAsync(cancellationToken)
        ?? true;
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

/// <summary>Assignees must hold an active membership in the request's tenant; names follow Identity's display-name rule.</summary>
internal sealed class MembershipTaskAssigneeDirectory(IMembershipLookup memberships, IUserDirectory users, IRequestContext requestContext) : ITaskAssigneeDirectory
{
    public Task<string?> FindActiveRoleAsync(Guid userId, CancellationToken cancellationToken) =>
        memberships.FindActiveRoleAsync(userId, requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved."), cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        users.DisplayNamesAsync(userIds, cancellationToken);
}

using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Orders.Application;
using BayiOS.Modules.Orders.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Integrations (AI assistant) ports: the catalog is read through the Catalog
/// DbContext (tenant query filter + RLS, request connection), the draft is created through the Orders create command
/// (its own validation, pricing, history and audit). Integrations writes no other module's tables.
/// </summary>
internal static class IntegrationsPorts
{
    public static IServiceCollection AddIntegrationsPorts(this IServiceCollection services)
    {
        services.AddScoped<IAssistantCatalog, CatalogAssistantCatalog>();
        services.AddScoped<IAssistantOrderDrafts, OrdersAssistantDrafts>();
        return services;
    }
}

/// <summary>Active variants of active products with their unit and brand. Never prices or stock.</summary>
internal sealed class CatalogAssistantCatalog(CatalogDbContext catalog) : IAssistantCatalog
{
    private const string Active = "active";

    public async Task<AssistantCatalogSnapshot> LoadActiveVariantsAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await Query(null).Take(limit + 1).ToListAsync(cancellationToken);
        return new AssistantCatalogSnapshot([.. rows.Take(limit).Select(ToEntry)], rows.Count > limit);
    }

    public async Task<IReadOnlyList<CatalogEntry>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var rows = await Query([.. codes]).ToListAsync(cancellationToken);
        return [.. rows.Select(ToEntry)];
    }

    public async Task<IReadOnlyList<CatalogEntry>> FindByIdsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var rows = await Query(null, [.. variantIds]).ToListAsync(cancellationToken);
        return [.. rows.Select(ToEntry)];
    }

    private sealed record Row(Guid VariantId, Guid ProductId, string Sku, string? Barcode, string Name, string? VariantName, string? Brand, string Unit);

    /// <param name="codes">Null = every active variant (ordered by product name, SKU); else only these SKUs / barcodes.</param>
    /// <param name="ids">Else only these variant ids.</param>
    private IQueryable<Row> Query(string[]? codes, Guid[]? ids = null) =>
        from variant in catalog.Variants.AsNoTracking()
        join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
        join unit in catalog.Units.AsNoTracking() on variant.UnitId equals unit.Id
        join brand in catalog.Brands.AsNoTracking() on product.BrandId equals brand.Id into brands
        from brand in brands.DefaultIfEmpty()
        where variant.Status == Active && product.Status == Active
              && (codes == null || codes.Contains(variant.Sku) || (variant.Barcode != null && codes.Contains(variant.Barcode)))
              && (ids == null || ids.Contains(variant.Id))
        orderby product.Name, variant.Sku
        select new Row(variant.Id, product.Id, variant.Sku, variant.Barcode, product.Name, variant.Name, brand == null ? null : brand.Name, unit.Code);

    private static CatalogEntry ToEntry(Row row) =>
        new(
            row.VariantId,
            row.ProductId,
            row.Sku,
            row.Barcode,
            string.IsNullOrWhiteSpace(row.VariantName) ? row.Name : $"{row.Name} {row.VariantName}",
            row.Brand,
            row.Unit);
}

/// <summary>Creates the <c>whatsapp_ai</c> draft through <see cref="OrderService.CreateAsync"/> (prices resolved by Pricing).</summary>
internal sealed class OrdersAssistantDrafts(OrderService orders) : IAssistantOrderDrafts
{
    private const string WhatsappAiSource = "whatsapp_ai";

    public async Task<AssistantDraftResult> CreateDraftAsync(AssistantDraftOrder draft, string? correlationId, CancellationToken cancellationToken)
    {
        var request = new CreateOrderRequest(
            draft.CustomerId,
            draft.WarehouseId,
            WhatsappAiSource,
            draft.ParseId,
            Currency: null,
            PricesIncludeTax: null,
            RequestedDeliveryDate: null,
            draft.Notes,
            // No unit price and no discount: the AI path never supplies prices; Pricing resolves them.
            [.. draft.Lines.Select(l => new OrderLineInput(null, l.VariantId, l.Quantity, null, null, null))]);
        var result = await orders.CreateAsync(request, correlationId, cancellationToken);
        return result.Error is { } error
            ? new AssistantDraftResult(null, null, error.Status, error.Code, error.Title, error.Errors)
            : new AssistantDraftResult(result.Value!.Order.Id, result.Value.Order.Number);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Parsing;
using BayiOS.Modules.Pricing.Application;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Pricing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform;

/// <summary>
/// Composition-root adapters for the Imports ports. Reads go through the owning modules' DbContexts (tenant query
/// filters plus RLS); every write goes through the owning module's application command — Catalog's
/// <see cref="ProductService"/> and Pricing's <see cref="PriceListService"/> — with their validation, unique-key
/// handling and stamps, on the job's shared connection and transaction. Nothing here touches stock.
/// </summary>
internal static class ImportsPorts
{
    public static IServiceCollection AddImportsPorts(this IServiceCollection services)
    {
        services.AddScoped<IImportCatalogGateway, CatalogImportGateway>();
        services.AddScoped<IImportPriceGateway, PricingImportGateway>();
        services.AddScoped<IImportActorDirectory, MembershipImportActorDirectory>();
        return services;
    }
}

internal sealed class CatalogImportGateway(CatalogDbContext catalog, ProductService products) : IImportCatalogGateway
{
    private const string Archived = "archived";

    public async Task<IReadOnlyList<CatalogVariantSnapshot>> FindVariantsAsync(
        IReadOnlyCollection<string> skus, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken)
    {
        if (skus.Count == 0 && barcodes.Count == 0)
        {
            return [];
        }

        var skuList = skus.ToList();
        var barcodeList = barcodes.ToList();
        return await (from variant in catalog.Variants.AsNoTracking()
                      join product in catalog.Products.AsNoTracking() on variant.ProductId equals product.Id
                      where skuList.Contains(variant.Sku) || (variant.Barcode != null && barcodeList.Contains(variant.Barcode))
                      select new CatalogVariantSnapshot(
                          variant.Id,
                          product.Id,
                          variant.Sku,
                          variant.Barcode,
                          variant.UnitId,
                          variant.Status,
                          variant.RowVersion,
                          product.Name,
                          product.Description,
                          product.TaxRate,
                          product.BrandId,
                          product.CategoryId,
                          product.Status,
                          product.RowVersion))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindUnitsAsync(CancellationToken cancellationToken) =>
        await catalog.Units.AsNoTracking().Where(u => u.ArchivedAt == null).ToDictionaryAsync(u => u.Code, u => u.Id, StringComparer.Ordinal, cancellationToken);

    public async Task<IReadOnlyDictionary<string, Guid>> FindBrandsAsync(CancellationToken cancellationToken) =>
        (await catalog.Brands.AsNoTracking().Where(b => b.ArchivedAt == null).OrderBy(b => b.CreatedAt).Select(b => new { b.Id, b.Name }).ToListAsync(cancellationToken))
        .GroupBy(b => ImportValues.Fold(b.Name), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, CatalogCategoryMatch>> FindCategoriesAsync(CancellationToken cancellationToken) =>
        (await catalog.Categories.AsNoTracking().Where(c => c.ArchivedAt == null).Select(c => new { c.Id, c.Name }).ToListAsync(cancellationToken))
        .GroupBy(c => ImportValues.Fold(c.Name), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => new CatalogCategoryMatch(g.First().Id, g.Count() > 1), StringComparer.Ordinal);

    public async Task<CatalogWriteResult> CreateProductAsync(ImportProductValues values, CancellationToken cancellationToken)
    {
        var result = await products.CreateProductAsync(
            new CreateProductRequest(
                values.Name, values.Description, values.CategoryId, values.BrandId, values.UnitId, values.TaxRate, values.Status, values.Sku, values.Barcode, null),
            cancellationToken);
        // Each row is its own command: a rejected row must not leave entities behind for the next SaveChanges.
        catalog.ChangeTracker.Clear();
        if (result.Error is { } error)
        {
            return Failed(error);
        }

        var product = result.Value!;
        return new CatalogWriteResult("created", product.Variants[0].Id, product.Id, null, null);
    }

    public async Task<CatalogWriteResult> UpdateProductAsync(CatalogVariantSnapshot current, ImportProductValues values, CancellationToken cancellationToken)
    {
        var previous = new Dictionary<string, object?>(StringComparer.Ordinal);
        Optional<T> Change<T>(string name, T? next, T currentValue)
        {
            if (next is null || EqualityComparer<T>.Default.Equals(next, currentValue))
            {
                return default;
            }

            previous[name] = currentValue;
            return new Optional<T>(true, next);
        }

        var name = Change("name", values.Name, current.ProductName);
        var description = Change<string?>("description", values.Description, current.Description);
        var taxRate = values.TaxRate is { } rate && rate != current.TaxRate ? Record(previous, "taxRate", current.TaxRate, (decimal?)rate) : default;
        var brand = values.BrandId is { } brandId && brandId != current.BrandId ? Record(previous, "brandId", current.BrandId, (Guid?)brandId) : default;
        var category = values.CategoryId is { } categoryId && categoryId != current.CategoryId ? Record(previous, "categoryId", current.CategoryId, (Guid?)categoryId) : default;
        var status = Change<string?>("status", values.Status, current.ProductStatus);
        var productChanged = name.IsSet || description.IsSet || taxRate.IsSet || brand.IsSet || category.IsSet || status.IsSet;

        var sku = Change<string?>("sku", values.Sku, current.Sku);
        var barcode = Change<string?>("barcode", values.Barcode, current.Barcode);
        var unit = values.UnitId is { } unitId && unitId != current.UnitId ? Record(previous, "unitId", current.UnitId, (Guid?)unitId) : default;
        var variantChanged = sku.IsSet || barcode.IsSet || unit.IsSet;

        if (!productChanged && !variantChanged)
        {
            return new CatalogWriteResult("unchanged", current.VariantId, current.ProductId, null, null);
        }

        try
        {
            if (productChanged)
            {
                var result = await products.UpdateProductAsync(
                    current.ProductId,
                    new ProductPatch(
                        new Optional<string?>(name.IsSet, name.Value), description, category, brand, default, taxRate, status, default),
                    current.ProductVersion,
                    cancellationToken);
                if (result.Error is { } error)
                {
                    return Failed(error);
                }
            }

            if (variantChanged)
            {
                var result = await products.UpdateVariantAsync(
                    current.VariantId, new VariantPatch(sku, barcode, default, default, unit, default, default), current.VariantVersion, cancellationToken);
                if (result.Error is { } error)
                {
                    return Failed(error);
                }
            }
        }
        finally
        {
            catalog.ChangeTracker.Clear();
        }

        return new CatalogWriteResult("updated", current.VariantId, current.ProductId, JsonSerializer.Serialize(previous, JsonSerializerOptions.Web), null);
    }

    private static Optional<T?> Record<T>(Dictionary<string, object?> previous, string name, T? currentValue, T? next)
        where T : struct
    {
        previous[name] = currentValue;
        return new Optional<T?>(true, next);
    }

    private static CatalogWriteResult Failed(BayiOS.Modules.Catalog.Application.AppError error) =>
        new("failed", null, null, null, error.Errors is { } errors ? errors.ToDictionary() : new Dictionary<string, string[]> { [string.Empty] = [error.Title] }, error.Code);
}

internal sealed partial class PricingImportGateway(IPriceListDirectory directory, PriceListService service, PricingDbContext pricing) : IImportPriceGateway
{
    public async Task<ImportPriceListInfo?> FindPriceListAsync(Guid priceListId, CancellationToken cancellationToken) =>
        await directory.FindAsync(priceListId, cancellationToken) is { } list ? new ImportPriceListInfo(list.Id, list.Name, list.Kind, list.Currency, list.Status) : null;

    public async Task<IReadOnlyDictionary<(Guid VariantId, decimal MinQuantity), decimal>> FindPricesAsync(
        Guid priceListId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<(Guid, decimal), decimal>();
        }

        var ids = variantIds.ToList();
        return (await pricing.Items.AsNoTracking()
                .Where(i => i.PriceListId == priceListId && ids.Contains(i.VariantId))
                .Select(i => new { i.VariantId, i.MinQuantity, i.UnitPrice })
                .ToListAsync(cancellationToken))
            .ToDictionary(i => (i.VariantId, i.MinQuantity), i => i.UnitPrice);
    }

    /// <summary>
    /// One Pricing upsert per batch. Pricing validates the whole request; items it rejects (<c>items[i]…</c>) are
    /// reported and the rest is retried once without them, so one bad row does not fail its neighbours.
    /// </summary>
    public async Task<PriceUpsertResult> UpsertPricesAsync(Guid priceListId, IReadOnlyList<ImportPriceItem> items, CancellationToken cancellationToken)
    {
        var ids = new Guid?[items.Count];
        var errors = new Dictionary<int, string[]>();
        var pending = Enumerable.Range(0, items.Count).ToList();
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var request = new UpsertPriceListItemsRequest(
                [.. pending.Select(i => new PriceListItemInput(items[i].VariantId, items[i].MinQuantity, items[i].UnitPrice))]);
            var result = await service.UpsertItemsAsync(priceListId, request, cancellationToken);
            pricing.ChangeTracker.Clear();
            if (result.Error is null)
            {
                for (var k = 0; k < pending.Count; k++)
                {
                    ids[pending[k]] = result.Value!.Items[k].Id;
                }

                return new PriceUpsertResult(ids, errors, null);
            }

            var itemErrors = (result.Error.Errors ?? new Dictionary<string, string[]>())
                .Select(e => (Match: ItemIndex().Match(e.Key), e.Value))
                .Where(e => e.Match.Success)
                .ToList();
            if (itemErrors.Count == 0)
            {
                foreach (var index in pending)
                {
                    errors[index] = [result.Error.Title];
                }

                return new PriceUpsertResult(ids, errors, null);
            }

            var rejected = new HashSet<int>();
            foreach (var (match, messages) in itemErrors)
            {
                var original = pending[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)];
                errors[original] = messages;
                rejected.Add(original);
            }

            pending = [.. pending.Where(i => !rejected.Contains(i))];
        }

        foreach (var index in pending)
        {
            errors[index] = ["Price list rejected the batch."];
        }

        return new PriceUpsertResult(ids, errors, null);
    }

    [GeneratedRegex(@"^items\[(\d+)\]")]
    private static partial Regex ItemIndex();
}

internal sealed class MembershipImportActorDirectory(IMembershipLookup memberships) : IImportActorDirectory
{
    public Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        memberships.FindActiveRoleAsync(userId, tenantId, cancellationToken);
}

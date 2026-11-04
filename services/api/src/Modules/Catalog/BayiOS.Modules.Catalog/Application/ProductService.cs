using System.Text.Json;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Catalog.Application;

internal sealed record ProductListQuery(
    string? Q,
    Guid? CategoryId,
    Guid? BrandId,
    IReadOnlyList<string> Statuses,
    string Sort,
    int Limit,
    KeysetCursor? Cursor);

internal sealed record VariantListQuery(
    string? Q,
    string? Sku,
    string? Barcode,
    Guid? ProductId,
    IReadOnlyList<string> Statuses,
    int Limit,
    KeysetCursor? Cursor);

internal sealed record ProductPatch(
    Optional<string?> Name,
    Optional<string?> Description,
    Optional<Guid?> CategoryId,
    Optional<Guid?> BrandId,
    Optional<Guid?> DefaultUnitId,
    Optional<decimal?> TaxRate,
    Optional<string?> Status);

internal sealed record VariantPatch(
    Optional<string?> Sku,
    Optional<string?> Barcode,
    Optional<string?> Name,
    Optional<IReadOnlyDictionary<string, string>?> OptionValues,
    Optional<Guid?> UnitId,
    Optional<string?> Status);

/// <summary>Products and their variants (domain-model §3, aggregate <c>Product</c>).</summary>
internal sealed class ProductService(CatalogDbContext db, IRequestContext requestContext, TimeProvider clock)
{
    public const string SortCreatedDesc = "-createdAt";
    public const string SortName = "name";
    public const string VariantSort = "sku";
    public const int MaxVariantsPerRequest = 100;

    public static readonly IReadOnlyList<string> Sorts = [SortCreatedDesc, SortName];

    /// <summary>Statuses a client may set directly; <c>archived</c> is reached through DELETE.</summary>
    private static readonly string[] SettableStatuses = [CatalogStatuses.Active, CatalogStatuses.Inactive];

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    // ---- Queries ----

    /// <summary>
    /// Keyset-paged product list. Search: product name contains <c>q</c> (pg_trgm GIN index, ILIKE), or a variant
    /// whose SKU contains <c>q</c> (pg_trgm GIN index) or whose barcode equals <c>q</c> (unique btree index).
    /// </summary>
    public async Task<PagedResponse<ProductResponse>> ListProductsAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking().Where(p => query.Statuses.Contains(p.Status));

        if (query.BrandId is { } brandId)
        {
            products = products.Where(p => p.BrandId == brandId);
        }

        if (query.CategoryId is { } categoryId)
        {
            // The category and all of its descendants (materialized path prefix).
            var path = await db.Categories.AsNoTracking().Where(c => c.Id == categoryId).Select(c => c.Path).SingleOrDefaultAsync(cancellationToken);
            if (path is null)
            {
                return new PagedResponse<ProductResponse>([], new PageInfo(null, false, query.Limit));
            }

            products = products.Where(p => db.Categories.Any(c => c.Id == p.CategoryId && c.Path.StartsWith(path)));
        }

        if (CatalogText.Clean(query.Q) is { } term)
        {
            var pattern = CatalogText.ContainsPattern(term);
            products = products.Where(p => EF.Functions.ILike(p.Name, pattern, "\\")
                || db.Variants.Any(v => v.ProductId == p.Id && (EF.Functions.ILike(v.Sku, pattern, "\\") || v.Barcode == term)));
        }

        List<Product> page;
        if (query.Sort == SortName)
        {
            if (query.Cursor is { } cursor)
            {
                var (name, id) = (cursor.Key, cursor.Id);
                products = products.Where(p => EF.Functions.GreaterThan(ValueTuple.Create(p.Name, p.Id), ValueTuple.Create(name, id)));
            }

            page = await products.OrderBy(p => p.Name).ThenBy(p => p.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        }
        else
        {
            if (query.Cursor is { } cursor)
            {
                _ = KeysetCursor.TryParseTimestamp(cursor.Key, out var createdAt);
                var id = cursor.Id;
                products = products.Where(p => EF.Functions.LessThan(ValueTuple.Create(p.CreatedAt, p.Id), ValueTuple.Create(createdAt, id)));
            }

            page = await products.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        }

        var hasMore = page.Count > query.Limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore)
        {
            var last = page[^1];
            nextCursor = new KeysetCursor(query.Sort, query.Sort == SortName ? last.Name : KeysetCursor.FormatTimestamp(last.CreatedAt), last.Id).Encode();
        }

        var ids = page.Select(p => p.Id).ToList();
        var variants = (await db.Variants.AsNoTracking()
                .Where(v => ids.Contains(v.ProductId))
                .OrderBy(v => v.Sku)
                .ToListAsync(cancellationToken))
            .ToLookup(v => v.ProductId);

        return new PagedResponse<ProductResponse>(
            page.Select(p => ToResponse(p, variants[p.Id])).ToList(),
            new PageInfo(nextCursor, hasMore, query.Limit));
    }

    public async Task<AppResult<ProductResponse>> GetProductAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return product is null ? AppError.NotFound() : await LoadResponseAsync(product, cancellationToken);
    }

    public async Task<PagedResponse<VariantResponse>> ListVariantsAsync(VariantListQuery query, CancellationToken cancellationToken)
    {
        var variants = db.Variants.AsNoTracking().Where(v => query.Statuses.Contains(v.Status));

        if (query.ProductId is { } productId)
        {
            variants = variants.Where(v => v.ProductId == productId);
        }

        if (CatalogText.Clean(query.Sku) is { } sku)
        {
            var normalized = CatalogCodes.NormalizeSku(sku);
            variants = variants.Where(v => v.Sku == normalized);
        }

        if (CatalogCodes.NormalizeBarcode(query.Barcode) is { } barcode)
        {
            variants = variants.Where(v => v.Barcode == barcode);
        }

        if (CatalogText.Clean(query.Q) is { } term)
        {
            var pattern = CatalogText.ContainsPattern(term);
            variants = variants.Where(v => EF.Functions.ILike(v.Sku, pattern, "\\")
                || v.Barcode == term
                || (v.Name != null && EF.Functions.ILike(v.Name, pattern, "\\")));
        }

        if (query.Cursor is { } cursor)
        {
            var (key, id) = (cursor.Key, cursor.Id);
            variants = variants.Where(v => EF.Functions.GreaterThan(ValueTuple.Create(v.Sku, v.Id), ValueTuple.Create(key, id)));
        }

        var page = await variants.OrderBy(v => v.Sku).ThenBy(v => v.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > query.Limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var nextCursor = hasMore ? new KeysetCursor(VariantSort, page[^1].Sku, page[^1].Id).Encode() : null;
        return new PagedResponse<VariantResponse>(page.Select(ToResponse).ToList(), new PageInfo(nextCursor, hasMore, query.Limit));
    }

    public async Task<AppResult<VariantResponse>> GetVariantAsync(Guid id, CancellationToken cancellationToken)
    {
        var variant = await db.Variants.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        return variant is null ? AppError.NotFound() : ToResponse(variant);
    }

    /// <summary>Barcode scan (mobile): exactly one variant or 404.</summary>
    public async Task<AppResult<VariantResponse>> LookupByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        var normalized = CatalogCodes.NormalizeBarcode(barcode);
        var variant = normalized is null
            ? null
            : await db.Variants.AsNoTracking().SingleOrDefaultAsync(v => v.Barcode == normalized, cancellationToken);
        return variant is null ? AppError.NotFound() : ToResponse(variant);
    }

    // ---- Product commands ----

    public async Task<AppResult<ProductResponse>> CreateProductAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = CatalogText.Clean(request.Name);
        var description = CatalogText.Clean(request.Description);
        var status = request.Status ?? CatalogStatuses.Active;
        ValidateProductFields(errors, name, description, request.TaxRate, status, taxRateRequired: true);
        if (request.DefaultUnitId is null)
        {
            errors["defaultUnitId"] = ["Required."];
        }

        var specs = new List<VariantSpec>();
        if (request.Variants is { } variants)
        {
            if (request.Sku is not null || request.Barcode is not null)
            {
                errors["sku"] = ["Pass either sku/barcode (simple product) or variants, not both."];
            }

            if (variants.Count is 0 or > MaxVariantsPerRequest)
            {
                errors["variants"] = [$"Must contain 1–{MaxVariantsPerRequest} variants."];
            }

            for (var i = 0; i < Math.Min(variants.Count, MaxVariantsPerRequest); i++)
            {
                var v = variants[i];
                specs.Add(ValidateVariant(errors, $"variants[{i}].", v.Sku, v.Barcode, v.Name, v.OptionValues, v.UnitId, v.Status));
            }
        }
        else
        {
            specs.Add(ValidateVariant(errors, string.Empty, request.Sku, request.Barcode, null, null, null, null));
        }

        CheckDuplicatesWithinRequest(errors, specs);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var defaultUnitId = request.DefaultUnitId!.Value;
        await ValidateReferencesAsync(errors, request.CategoryId, request.BrandId, specs.Select(s => s.UnitId ?? defaultUnitId).Append(defaultUnitId), cancellationToken);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var now = CatalogText.Now(clock);
        var userId = UserId;
        var product = Product.Create(TenantId, name!, description, request.CategoryId, request.BrandId, defaultUnitId, request.TaxRate!.Value, status, userId, now);
        var created = specs
            .Select(s => ProductVariant.Create(product, s.Sku, s.Barcode, s.Name, s.OptionValuesJson, s.UnitId ?? defaultUnitId, s.Status, userId, now))
            .ToList();

        db.Products.Add(product);
        db.Variants.AddRange(created);
        var saved = await SaveAsync(cancellationToken);
        return saved ?? (AppResult<ProductResponse>)ToResponse(product, created);
    }

    public async Task<AppResult<ProductResponse>> UpdateProductAsync(Guid id, ProductPatch patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return AppError.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        if (patch.Name.IsSet && CatalogText.Clean(patch.Name.Value) is null)
        {
            errors["name"] = ["Must be 1–200 characters."];
        }

        if (patch.DefaultUnitId.IsSet && patch.DefaultUnitId.Value is null)
        {
            errors["defaultUnitId"] = ["Cannot be null."];
        }

        if (patch.TaxRate.IsSet && patch.TaxRate.Value is null)
        {
            errors["taxRate"] = ["Cannot be null."];
        }

        if (patch.Status.IsSet && !SettableStatuses.Contains(patch.Status.Value))
        {
            errors["status"] = ["Must be active or inactive (archive with DELETE)."];
        }

        var name = patch.Name.IsSet ? CatalogText.Clean(patch.Name.Value) : product.Name;
        var description = patch.Description.IsSet ? CatalogText.Clean(patch.Description.Value) : product.Description;
        var taxRate = patch.TaxRate.Or(product.TaxRate);
        ValidateProductFields(errors, name, description, taxRate, product.Status, taxRateRequired: false);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var categoryId = patch.CategoryId.Or(product.CategoryId);
        var brandId = patch.BrandId.Or(product.BrandId);
        var defaultUnitId = patch.DefaultUnitId.Or(product.DefaultUnitId)!.Value;
        await ValidateReferencesAsync(
            errors,
            patch.CategoryId.IsSet ? categoryId : null,
            patch.BrandId.IsSet ? brandId : null,
            patch.DefaultUnitId.IsSet ? [defaultUnitId] : [],
            cancellationToken);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (product.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = CatalogText.Now(clock);
        product.Name = name!;
        product.Description = description;
        product.CategoryId = categoryId;
        product.BrandId = brandId;
        product.DefaultUnitId = defaultUnitId;
        product.TaxRate = taxRate!.Value;
        if (patch.Status.IsSet)
        {
            product.SetStatus(patch.Status.Value!, now);
        }

        product.Touch(UserId, now);
        if (await SaveAsync(cancellationToken) is { } error)
        {
            return error;
        }

        return await LoadResponseAsync(product, cancellationToken);
    }

    /// <summary>DELETE = archive (PRD CAT-02: never physically deleted; hidden from default lists).</summary>
    public async Task<AppError?> ArchiveProductAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return AppError.NotFound();
        }

        if (product.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = CatalogText.Now(clock);
        product.SetStatus(CatalogStatuses.Archived, now);
        product.Touch(UserId, now);
        return await SaveAsync(cancellationToken);
    }

    // ---- Variant commands ----

    public async Task<AppResult<VariantResponse>> AddVariantAsync(Guid productId, CreateVariantRequest request, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return AppError.NotFound();
        }

        if (product.Status == CatalogStatuses.Archived)
        {
            return AppError.Conflict("catalog.product_archived", "Restore the product before adding variants");
        }

        var errors = new Dictionary<string, string[]>();
        var spec = ValidateVariant(errors, string.Empty, request.Sku, request.Barcode, request.Name, request.OptionValues, request.UnitId, request.Status);
        if (errors.Count == 0)
        {
            await ValidateReferencesAsync(errors, null, null, [spec.UnitId ?? product.DefaultUnitId], cancellationToken);
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var variant = ProductVariant.Create(
            product, spec.Sku, spec.Barcode, spec.Name, spec.OptionValuesJson, spec.UnitId ?? product.DefaultUnitId, spec.Status, UserId, CatalogText.Now(clock));
        db.Variants.Add(variant);
        return await SaveAsync(cancellationToken) ?? (AppResult<VariantResponse>)ToResponse(variant);
    }

    public async Task<AppResult<VariantResponse>> UpdateVariantAsync(Guid id, VariantPatch patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (variant is null)
        {
            return AppError.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        if (patch.Sku.IsSet && patch.Sku.Value is null)
        {
            errors["sku"] = ["Cannot be null."];
        }

        if (patch.UnitId.IsSet && patch.UnitId.Value is null)
        {
            errors["unitId"] = ["Cannot be null."];
        }

        if (patch.Status.IsSet && !SettableStatuses.Contains(patch.Status.Value))
        {
            errors["status"] = ["Must be active or inactive (archive with DELETE)."];
        }

        var spec = ValidateVariant(
            errors,
            string.Empty,
            patch.Sku.Value ?? variant.Sku,
            patch.Barcode.IsSet ? patch.Barcode.Value : variant.Barcode,
            patch.Name.IsSet ? patch.Name.Value : variant.Name,
            patch.OptionValues.IsSet ? patch.OptionValues.Value : CatalogText.ReadOptions(variant.OptionValues),
            patch.UnitId.Value ?? variant.UnitId,
            patch.Status.IsSet ? patch.Status.Value : CatalogStatuses.Active);
        if (errors.Count == 0 && patch.UnitId.IsSet)
        {
            await ValidateReferencesAsync(errors, null, null, [spec.UnitId!.Value], cancellationToken);
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (variant.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = CatalogText.Now(clock);
        variant.Sku = spec.Sku;
        variant.Barcode = spec.Barcode;
        variant.Name = spec.Name;
        variant.OptionValues = spec.OptionValuesJson;
        variant.UnitId = spec.UnitId!.Value;
        if (patch.Status.IsSet)
        {
            variant.SetStatus(spec.Status, now);
        }

        variant.Touch(UserId, now);
        return await SaveAsync(cancellationToken) ?? (AppResult<VariantResponse>)ToResponse(variant);
    }

    /// <summary>Archives a variant. A non-archived product keeps at least one non-archived variant.</summary>
    public async Task<AppError?> ArchiveVariantAsync(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (variant is null)
        {
            return AppError.NotFound();
        }

        if (variant.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        if (variant.Status != CatalogStatuses.Archived)
        {
            var productArchived = await db.Products.AnyAsync(p => p.Id == variant.ProductId && p.Status == CatalogStatuses.Archived, cancellationToken);
            var hasOther = await db.Variants.AnyAsync(
                v => v.ProductId == variant.ProductId && v.Id != id && v.Status != CatalogStatuses.Archived, cancellationToken);
            if (!productArchived && !hasOther)
            {
                return AppError.Conflict("catalog.last_variant", "A product needs at least one variant; archive the product instead");
            }
        }

        var now = CatalogText.Now(clock);
        variant.SetStatus(CatalogStatuses.Archived, now);
        variant.Touch(UserId, now);
        return await SaveAsync(cancellationToken);
    }

    // ---- Helpers ----

    private sealed record VariantSpec(string Sku, string? Barcode, string? Name, string OptionValuesJson, Guid? UnitId, string Status);

    private static void ValidateProductFields(
        Dictionary<string, string[]> errors, string? name, string? description, decimal? taxRate, string status, bool taxRateRequired)
    {
        if (name is null || name.Length > 200)
        {
            errors.TryAdd("name", ["Must be 1–200 characters."]);
        }

        if (description is { Length: > 4000 })
        {
            errors["description"] = ["Must be at most 4000 characters."];
        }

        if (taxRate is null ? taxRateRequired : taxRate is < 0 or > 100 || decimal.Round(taxRate.Value, 2) != taxRate.Value)
        {
            errors.TryAdd("taxRate", ["Must be a percentage between 0 and 100 with at most 2 decimals."]);
        }

        if (!SettableStatuses.Contains(status) && status != CatalogStatuses.Archived)
        {
            errors.TryAdd("status", ["Must be active or inactive."]);
        }
        else if (taxRateRequired && status == CatalogStatuses.Archived)
        {
            errors.TryAdd("status", ["Must be active or inactive."]);
        }
    }

    private static VariantSpec ValidateVariant(
        Dictionary<string, string[]> errors,
        string prefix,
        string? sku,
        string? barcode,
        string? name,
        IReadOnlyDictionary<string, string>? optionValues,
        Guid? unitId,
        string? status)
    {
        var normalizedSku = CatalogCodes.NormalizeSku(sku ?? string.Empty);
        if (normalizedSku.Length is 0 or > 64 || normalizedSku.Any(char.IsControl))
        {
            errors.TryAdd(prefix + "sku", ["Must be 1–64 characters."]);
        }

        var normalizedBarcode = CatalogCodes.NormalizeBarcode(barcode);
        if (normalizedBarcode is not null && !CatalogCodes.IsValidCode(normalizedBarcode))
        {
            errors.TryAdd(prefix + "barcode", ["Must be 1–64 characters without whitespace."]);
        }

        var cleanName = CatalogText.Clean(name);
        if (cleanName is { Length: > 200 })
        {
            errors.TryAdd(prefix + "name", ["Must be at most 200 characters."]);
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (optionValues is not null)
        {
            if (optionValues.Count > 20)
            {
                errors.TryAdd(prefix + "optionValues", ["At most 20 options."]);
            }

            foreach (var (key, value) in optionValues)
            {
                var k = CatalogText.Clean(key);
                var v = CatalogText.Clean(value);
                if (k is null || k.Length > 50 || v is null || v.Length > 100)
                {
                    errors.TryAdd(prefix + "optionValues", ["Option names must be 1–50 and values 1–100 characters."]);
                    continue;
                }

                options[k] = v;
            }
        }

        var effectiveStatus = status ?? CatalogStatuses.Active;
        if (!SettableStatuses.Contains(effectiveStatus))
        {
            errors.TryAdd(prefix + "status", ["Must be active or inactive."]);
        }

        return new VariantSpec(normalizedSku, normalizedBarcode, cleanName, JsonSerializer.Serialize(options), unitId, effectiveStatus);
    }

    private static void CheckDuplicatesWithinRequest(Dictionary<string, string[]> errors, List<VariantSpec> specs)
    {
        if (specs.GroupBy(s => s.Sku, StringComparer.Ordinal).Any(g => g.Key.Length > 0 && g.Count() > 1))
        {
            errors.TryAdd("variants", ["SKUs must be unique within the request."]);
        }

        if (specs.Where(s => s.Barcode is not null).GroupBy(s => s.Barcode, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            errors.TryAdd("variants", ["Barcodes must be unique within the request."]);
        }
    }

    /// <summary>
    /// Referenced category/brand/units must exist in this tenant (query filters) and not be archived. Ids from another
    /// tenant are indistinguishable from unknown ids; the composite tenant FKs reject them again at the database.
    /// </summary>
    private async Task ValidateReferencesAsync(
        Dictionary<string, string[]> errors, Guid? categoryId, Guid? brandId, IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (categoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c && x.ArchivedAt == null, cancellationToken))
        {
            errors["categoryId"] = ["Category not found."];
        }

        if (brandId is { } b && !await db.Brands.AnyAsync(x => x.Id == b && x.ArchivedAt == null, cancellationToken))
        {
            errors["brandId"] = ["Brand not found."];
        }

        var units = unitIds.Distinct().ToList();
        if (units.Count > 0 && await db.Units.CountAsync(u => units.Contains(u.Id) && u.ArchivedAt == null, cancellationToken) != units.Count)
        {
            errors["unitId"] = ["Unit not found."];
        }
    }

    private async Task<AppError?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.VersionMismatch();
        }
        catch (DbUpdateException ex) when (AppError.FromUniqueViolation(ex) is { } conflict)
        {
            return conflict;
        }
    }

    private async Task<ProductResponse> LoadResponseAsync(Product product, CancellationToken cancellationToken)
    {
        var productId = product.Id;
        var variants = await db.Variants.AsNoTracking().Where(v => v.ProductId == productId).OrderBy(v => v.Sku).ToListAsync(cancellationToken);
        return ToResponse(product, variants);
    }

    private static ProductResponse ToResponse(Product p, IEnumerable<ProductVariant> variants) => new(
        p.Id,
        p.Name,
        p.Description,
        p.CategoryId,
        p.BrandId,
        p.DefaultUnitId,
        p.TaxRate,
        p.Status,
        CatalogText.ReadAttributes(p.Attributes),
        variants.Select(ToResponse).ToList(),
        p.RowVersion,
        p.CreatedAt,
        p.CreatedBy,
        p.UpdatedAt,
        p.UpdatedBy);

    private static VariantResponse ToResponse(ProductVariant v) => new(
        v.Id,
        v.ProductId,
        v.Sku,
        v.Barcode,
        v.Name,
        CatalogText.ReadOptions(v.OptionValues),
        v.UnitId,
        v.Status,
        CatalogText.ReadAttributes(v.Attributes),
        v.RowVersion,
        v.CreatedAt,
        v.CreatedBy,
        v.UpdatedAt,
        v.UpdatedBy);
}

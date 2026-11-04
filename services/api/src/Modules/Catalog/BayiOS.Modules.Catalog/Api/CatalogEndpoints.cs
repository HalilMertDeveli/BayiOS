using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Catalog.Application;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Catalog.Api;

/// <summary>
/// Catalog API (api-strategy §12.3). Reads need <c>catalog.read</c>; products, variants and brands need
/// <c>catalog.write</c>; units and categories need <c>catalog.fields.manage</c> (roles-permissions §3.2).
/// TODO(#68): create endpoints must honour Idempotency-Key once the platform filter lands.
/// </summary>
internal static class CatalogEndpoints
{
    private const string MergePatch = "application/merge-patch+json";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Catalog")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Units
        group.MapGet("/units", ListUnits).WithName("Catalog_ListUnits")
            .WithSummary("Birimler (sayfasız, ≤ 1000)").RequirePermission(Permissions.CatalogRead);
        Write(group.MapPost("/units", CreateUnit).WithName("Catalog_CreateUnit")
            .WithSummary("Birim oluşturur").RequirePermission(Permissions.CatalogFieldsManage), conflict: true);
        Update(group.MapPatch("/units/{id:guid}", UpdateUnit).WithName("Catalog_UpdateUnit")
            .WithSummary("Birimi günceller (If-Match zorunlu)").RequirePermission(Permissions.CatalogFieldsManage)
            .Accepts<UpdateUnitRequest>(MergePatch, "application/json"));
        Update(group.MapDelete("/units/{id:guid}", ArchiveUnit).WithName("Catalog_ArchiveUnit")
            .WithSummary("Birimi arşivler (If-Match zorunlu)").RequirePermission(Permissions.CatalogFieldsManage));

        // Brands
        group.MapGet("/brands", ListBrands).WithName("Catalog_ListBrands")
            .WithSummary("Markalar (sayfasız, ≤ 1000)").RequirePermission(Permissions.CatalogRead);
        Write(group.MapPost("/brands", CreateBrand).WithName("Catalog_CreateBrand")
            .WithSummary("Marka oluşturur").RequirePermission(Permissions.CatalogWrite), conflict: true);
        Update(group.MapPatch("/brands/{id:guid}", UpdateBrand).WithName("Catalog_UpdateBrand")
            .WithSummary("Markayı günceller (If-Match zorunlu)").RequirePermission(Permissions.CatalogWrite)
            .Accepts<UpdateBrandRequest>(MergePatch, "application/json"));
        Update(group.MapDelete("/brands/{id:guid}", ArchiveBrand).WithName("Catalog_ArchiveBrand")
            .WithSummary("Markayı arşivler (If-Match zorunlu)").RequirePermission(Permissions.CatalogWrite));

        // Categories
        group.MapGet("/categories", ListCategories).WithName("Catalog_ListCategories")
            .WithSummary("Kategori ağacı, düz liste (parentId/path/depth)").RequirePermission(Permissions.CatalogRead);
        Write(group.MapPost("/categories", CreateCategory).WithName("Catalog_CreateCategory")
            .WithSummary("Kategori oluşturur (derinlik ≤ 4)").RequirePermission(Permissions.CatalogFieldsManage), conflict: true);
        Update(group.MapPatch("/categories/{id:guid}", UpdateCategory).WithName("Catalog_UpdateCategory")
            .WithSummary("Kategori adını/sırasını günceller (If-Match zorunlu)").RequirePermission(Permissions.CatalogFieldsManage)
            .Accepts<UpdateCategoryRequest>(MergePatch, "application/json"));
        Update(group.MapDelete("/categories/{id:guid}", ArchiveCategory).WithName("Catalog_ArchiveCategory")
            .WithSummary("Kategoriyi arşivler (alt kategorisi yoksa, If-Match zorunlu)").RequirePermission(Permissions.CatalogFieldsManage)
            .ProducesProblem(StatusCodes.Status409Conflict));

        // Products
        group.MapGet("/products", ListProducts).WithName("Catalog_ListProducts")
            .WithSummary("Ürün listesi: keyset sayfalama, arama (ad/SKU/barkod), filtre (kategori/marka/durum)")
            .RequirePermission(Permissions.CatalogRead);
        group.MapGet("/products/{id:guid}", GetProduct).WithName("Catalog_GetProduct")
            .WithSummary("Ürün ve varyantları (ETag döner)").RequirePermission(Permissions.CatalogRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Write(group.MapPost("/products", CreateProduct).WithName("Catalog_CreateProduct")
            .WithSummary("Ürün oluşturur (basit ürün: sku/barcode; varyantlı: variants)").RequirePermission(Permissions.CatalogWrite), conflict: true);
        Update(group.MapPatch("/products/{id:guid}", UpdateProduct).WithName("Catalog_UpdateProduct")
            .WithSummary("Ürünü günceller (JSON merge patch, If-Match zorunlu)").RequirePermission(Permissions.CatalogWrite)
            .Accepts<UpdateProductRequest>(MergePatch, "application/json"));
        Update(group.MapDelete("/products/{id:guid}", ArchiveProduct).WithName("Catalog_ArchiveProduct")
            .WithSummary("Ürünü arşivler (If-Match zorunlu)").RequirePermission(Permissions.CatalogWrite));
        Write(group.MapPost("/products/{id:guid}/variants", AddVariant).WithName("Catalog_AddVariant")
            .WithSummary("Ürüne varyant ekler").RequirePermission(Permissions.CatalogWrite)
            .ProducesProblem(StatusCodes.Status404NotFound), conflict: true);

        // Variants
        group.MapGet("/variants", ListVariants).WithName("Catalog_ListVariants")
            .WithSummary("Varyant listesi: keyset sayfalama (SKU sırası), arama ve sku/barcode/productId filtresi")
            .RequirePermission(Permissions.CatalogRead);
        group.MapGet("/variants/lookup", LookupVariant).WithName("Catalog_LookupVariant")
            .WithSummary("Barkod okutma: tek varyant veya 404").RequirePermission(Permissions.CatalogRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/variants/{id:guid}", GetVariant).WithName("Catalog_GetVariant")
            .WithSummary("Varyant (ETag döner)").RequirePermission(Permissions.CatalogRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Update(group.MapPatch("/variants/{id:guid}", UpdateVariant).WithName("Catalog_UpdateVariant")
            .WithSummary("Varyantı günceller (JSON merge patch, If-Match zorunlu)").RequirePermission(Permissions.CatalogWrite)
            .Accepts<UpdateVariantRequest>(MergePatch, "application/json"));
        Update(group.MapDelete("/variants/{id:guid}", ArchiveVariant).WithName("Catalog_ArchiveVariant")
            .WithSummary("Varyantı arşivler (If-Match zorunlu; son varyant arşivlenemez)").RequirePermission(Permissions.CatalogWrite));

        return app;
    }

    private static void Write(RouteHandlerBuilder builder, bool conflict)
    {
        builder.ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        if (conflict)
        {
            builder.ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    private static void Update(RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    // ---- Units ----

    private static async Task<Results<Ok<ListResponse<UnitResponse>>, ProblemHttpResult>> ListUnits(
        HttpRequest http, ReferenceDataService service, bool? includeArchived, CancellationToken cancellationToken) =>
        RejectUnknownQuery(http, "includeArchived") is { } problem
            ? problem
            : TypedResults.Ok(new ListResponse<UnitResponse>(await service.ListUnitsAsync(includeArchived ?? false, cancellationToken)));

    private static async Task<Results<Created<UnitResponse>, ProblemHttpResult>> CreateUnit(
        CreateUnitRequest request, ReferenceDataService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateUnitAsync(request, cancellationToken), u => $"/api/v1/units/{u.Id}", u => u.Version);

    private static async Task<Results<Ok<UnitResponse>, ProblemHttpResult>> UpdateUnit(
        Guid id, UpdateUnitRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, ReferenceDataService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } problem)
        {
            return problem;
        }

        return Ok(http, await service.UpdateUnitAsync(id, request, version, cancellationToken), u => u.Version);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ArchiveUnit(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ReferenceDataService service, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem ? problem : NoContent(await service.ArchiveUnitAsync(id, version, cancellationToken));

    // ---- Brands ----

    private static async Task<Results<Ok<ListResponse<BrandResponse>>, ProblemHttpResult>> ListBrands(
        HttpRequest http, ReferenceDataService service, bool? includeArchived, string? q, CancellationToken cancellationToken) =>
        RejectUnknownQuery(http, "includeArchived", "q") is { } problem
            ? problem
            : TypedResults.Ok(new ListResponse<BrandResponse>(await service.ListBrandsAsync(includeArchived ?? false, q, cancellationToken)));

    private static async Task<Results<Created<BrandResponse>, ProblemHttpResult>> CreateBrand(
        CreateBrandRequest request, ReferenceDataService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateBrandAsync(request, cancellationToken), b => $"/api/v1/brands/{b.Id}", b => b.Version);

    private static async Task<Results<Ok<BrandResponse>, ProblemHttpResult>> UpdateBrand(
        Guid id, UpdateBrandRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, ReferenceDataService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } problem)
        {
            return problem;
        }

        return Ok(http, await service.UpdateBrandAsync(id, request, version, cancellationToken), b => b.Version);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ArchiveBrand(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ReferenceDataService service, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem ? problem : NoContent(await service.ArchiveBrandAsync(id, version, cancellationToken));

    // ---- Categories ----

    private static async Task<Results<Ok<ListResponse<CategoryResponse>>, ProblemHttpResult>> ListCategories(
        HttpRequest http, ReferenceDataService service, bool? includeArchived, CancellationToken cancellationToken) =>
        RejectUnknownQuery(http, "includeArchived") is { } problem
            ? problem
            : TypedResults.Ok(new ListResponse<CategoryResponse>(await service.ListCategoriesAsync(includeArchived ?? false, cancellationToken)));

    private static async Task<Results<Created<CategoryResponse>, ProblemHttpResult>> CreateCategory(
        CreateCategoryRequest request, ReferenceDataService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateCategoryAsync(request, cancellationToken), c => $"/api/v1/categories/{c.Id}", c => c.Version);

    private static async Task<Results<Ok<CategoryResponse>, ProblemHttpResult>> UpdateCategory(
        Guid id, UpdateCategoryRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, ReferenceDataService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } problem)
        {
            return problem;
        }

        return Ok(http, await service.UpdateCategoryAsync(id, request, version, cancellationToken), c => c.Version);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ArchiveCategory(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ReferenceDataService service, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem ? problem : NoContent(await service.ArchiveCategoryAsync(id, version, cancellationToken));

    // ---- Products ----

    /// <param name="q">Ad içinde geçen metin, SKU içinde geçen metin veya tam barkod.</param>
    /// <param name="categoryId">Kategori (alt kategoriler dahil).</param>
    /// <param name="status">Virgülle: active, inactive, archived. Varsayılan active,inactive.</param>
    /// <param name="sort">-createdAt (varsayılan) veya name.</param>
    private static async Task<Results<Ok<PagedResponse<ProductResponse>>, ProblemHttpResult>> ListProducts(
        HttpRequest http,
        ProductService service,
        string? q,
        string? categoryId,
        string? brandId,
        string? status,
        string? sort,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "q", "categoryId", "brandId", "status", "sort", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        var effectiveSort = sort ?? ProductService.SortCreatedDesc;
        if (!ProductService.Sorts.Contains(effectiveSort))
        {
            return Malformed($"sort must be one of: {string.Join(", ", ProductService.Sorts)}");
        }

        if (!TryParseGuid(categoryId, out var category) || !TryParseGuid(brandId, out var brand))
        {
            return Malformed("categoryId and brandId must be UUIDs");
        }

        if (ParsePaging(limit, cursor, effectiveSort, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        if (effectiveSort == ProductService.SortCreatedDesc && keyset is not null && !KeysetCursor.TryParseTimestamp(keyset.Key, out _))
        {
            return Malformed("cursor is invalid");
        }

        if (ParseStatuses(status, out var statuses) is { } statusProblem)
        {
            return statusProblem;
        }

        var query = new ProductListQuery(q, category, brand, statuses, effectiveSort, pageSize, keyset);
        return TypedResults.Ok(await service.ListProductsAsync(query, cancellationToken));
    }

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> GetProduct(
        Guid id, ProductService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.GetProductAsync(id, cancellationToken), p => p.Version);

    private static async Task<Results<Created<ProductResponse>, ProblemHttpResult>> CreateProduct(
        CreateProductRequest request, ProductService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateProductAsync(request, cancellationToken), p => $"/api/v1/products/{p.Id}", p => p.Version);

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> UpdateProduct(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ProductService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } problem)
        {
            return problem;
        }

        var document = await ReadPatchAsync(http.Request, cancellationToken);
        if (document is null)
        {
            return Malformed("Body must be a JSON object");
        }

        var reader = new PatchReader(document);
        var patch = new ProductPatch(
            reader.String("name"),
            reader.String("description"),
            reader.Guid("categoryId"),
            reader.Guid("brandId"),
            reader.Guid("defaultUnitId"),
            reader.Decimal("taxRate"),
            reader.String("status"));
        if (reader.Errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(reader.Errors));
        }

        return Ok(http, await service.UpdateProductAsync(id, patch, version, cancellationToken), p => p.Version);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ArchiveProduct(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ProductService service, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem ? problem : NoContent(await service.ArchiveProductAsync(id, version, cancellationToken));

    private static async Task<Results<Created<VariantResponse>, ProblemHttpResult>> AddVariant(
        Guid id, CreateVariantRequest request, ProductService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.AddVariantAsync(id, request, cancellationToken), v => $"/api/v1/variants/{v.Id}", v => v.Version);

    // ---- Variants ----

    /// <param name="q">SKU veya ad içinde geçen metin ya da tam barkod.</param>
    /// <param name="status">Virgülle: active, inactive, archived. Varsayılan active,inactive.</param>
    private static async Task<Results<Ok<PagedResponse<VariantResponse>>, ProblemHttpResult>> ListVariants(
        HttpRequest http,
        ProductService service,
        string? q,
        string? sku,
        string? barcode,
        string? productId,
        string? status,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "q", "sku", "barcode", "productId", "status", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (!TryParseGuid(productId, out var product))
        {
            return Malformed("productId must be a UUID");
        }

        if (ParsePaging(limit, cursor, ProductService.VariantSort, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        if (ParseStatuses(status, out var statuses) is { } statusProblem)
        {
            return statusProblem;
        }

        var query = new VariantListQuery(q, sku, barcode, product, statuses, pageSize, keyset);
        return TypedResults.Ok(await service.ListVariantsAsync(query, cancellationToken));
    }

    private static async Task<Results<Ok<VariantResponse>, ProblemHttpResult>> LookupVariant(
        HttpRequest http, string? barcode, ProductService service, HttpContext context, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "barcode") is { } unknown)
        {
            return unknown;
        }

        if (string.IsNullOrWhiteSpace(barcode))
        {
            return Malformed("barcode is required");
        }

        return Ok(context, await service.LookupByBarcodeAsync(barcode, cancellationToken), v => v.Version);
    }

    private static async Task<Results<Ok<VariantResponse>, ProblemHttpResult>> GetVariant(
        Guid id, ProductService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.GetVariantAsync(id, cancellationToken), v => v.Version);

    private static async Task<Results<Ok<VariantResponse>, ProblemHttpResult>> UpdateVariant(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ProductService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } problem)
        {
            return problem;
        }

        var document = await ReadPatchAsync(http.Request, cancellationToken);
        if (document is null)
        {
            return Malformed("Body must be a JSON object");
        }

        var reader = new PatchReader(document);
        var patch = new VariantPatch(
            reader.String("sku"),
            reader.String("barcode"),
            reader.String("name"),
            reader.StringMap("optionValues"),
            reader.Guid("unitId"),
            reader.String("status"));
        if (reader.Errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(reader.Errors));
        }

        return Ok(http, await service.UpdateVariantAsync(id, patch, version, cancellationToken), v => v.Version);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ArchiveVariant(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ProductService service, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem ? problem : NoContent(await service.ArchiveVariantAsync(id, version, cancellationToken));

    // ---- Helpers ----

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(HttpContext http, AppResult<T> result, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Ok(result.Value);
    }

    private static Results<Created<T>, ProblemHttpResult> Created<T>(HttpContext http, AppResult<T> result, Func<T, string> location, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Created(location(result.Value!), result.Value);
    }

    private static Results<NoContent, ProblemHttpResult> NoContent(AppError? error) =>
        error is null ? TypedResults.NoContent() : Problem(error);

    private static ProblemHttpResult Problem(AppError error) => TypedResults.Problem(
        error.Errors is null
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors));

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    /// <summary>Unknown query parameters are rejected (api-strategy §5.2).</summary>
    private static ProblemHttpResult? RejectUnknownQuery(HttpRequest request, params string[] allowed)
    {
        var unknown = request.Query.Keys.Where(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        return unknown.Count == 0 ? null : Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
    }

    private static ProblemHttpResult? ParsePaging(string? limit, string? cursor, string sort, out int pageSize, out KeysetCursor? keyset)
    {
        pageSize = DefaultLimit;
        keyset = null;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxLimit))
        {
            return Malformed($"limit must be an integer between 1 and {MaxLimit}");
        }

        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(cursor, sort, out var decoded))
            {
                return Malformed("cursor is invalid for this query");
            }

            keyset = decoded;
        }

        return null;
    }

    private static ProblemHttpResult? ParseStatuses(string? status, out IReadOnlyList<string> statuses)
    {
        statuses = string.IsNullOrWhiteSpace(status)
            ? [CatalogStatuses.Active, CatalogStatuses.Inactive]
            : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        return statuses.All(CatalogStatuses.All.Contains)
            ? null
            : Malformed($"status must be a comma-separated subset of: {string.Join(", ", CatalogStatuses.All)}");
    }

    private static bool TryParseGuid(string? value, out Guid? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Guid.TryParse(value, out var parsed))
        {
            id = parsed;
            return true;
        }

        return false;
    }

    private static ProblemHttpResult? IfMatch(string? ifMatch, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status428PreconditionRequired, "precondition.required", "If-Match header is required"));
        }

        var trimmed = ifMatch.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return long.TryParse(trimmed.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            ? null
            : TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status412PreconditionFailed, "concurrency.version_mismatch", "If-Match does not match the current version"));
    }

    private static async Task<JsonObject?> ReadPatchAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";
}

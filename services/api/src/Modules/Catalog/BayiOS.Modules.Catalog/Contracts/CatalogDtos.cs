using System.Text.Json;

namespace BayiOS.Modules.Catalog.Contracts;

/// <summary>Keyset page metadata (api-strategy §5.1). <c>nextCursor</c> is opaque to clients.</summary>
public sealed record PageInfo(string? NextCursor, bool HasMore, int Limit);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, PageInfo Page);

/// <summary>Unpaged reference list (units, brands, categories; capped at 1000 items).</summary>
public sealed record ListResponse<T>(IReadOnlyList<T> Items);

public sealed record UnitResponse(
    Guid Id,
    string Code,
    string Name,
    bool AllowsFraction,
    bool IsArchived,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CreateUnitRequest(string? Code, string? Name, bool? AllowsFraction);

/// <summary>Merge patch; <c>code</c> is immutable once created.</summary>
public sealed record UpdateUnitRequest(string? Name, bool? AllowsFraction);

public sealed record BrandResponse(
    Guid Id,
    string Name,
    bool IsArchived,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CreateBrandRequest(string? Name);

public sealed record UpdateBrandRequest(string? Name);

public sealed record CategoryResponse(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Path,
    int Depth,
    int SortOrder,
    bool IsArchived,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>The parent is fixed at creation (max depth 4).</summary>
public sealed record CreateCategoryRequest(string? Name, Guid? ParentId, int? SortOrder);

public sealed record UpdateCategoryRequest(string? Name, int? SortOrder);

public sealed record VariantResponse(
    Guid Id,
    Guid ProductId,
    string Sku,
    string? Barcode,
    string? Name,
    IReadOnlyDictionary<string, string> OptionValues,
    Guid UnitId,
    string Status,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    long Version,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy);

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid? CategoryId,
    Guid? BrandId,
    Guid DefaultUnitId,
    decimal TaxRate,
    string Status,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    IReadOnlyList<VariantResponse> Variants,
    long Version,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy);

/// <summary>
/// A variant to create. <c>unitId</c> defaults to the product's default unit; <c>status</c> to <c>active</c>.
/// </summary>
public sealed record CreateVariantRequest(
    string? Sku,
    string? Barcode,
    string? Name,
    IReadOnlyDictionary<string, string>? OptionValues,
    Guid? UnitId,
    string? Status);

/// <summary>
/// Create a product. A simple product passes <c>sku</c> (and optionally <c>barcode</c>) and gets one default variant;
/// a product with options passes <c>variants</c> instead (1–100).
/// </summary>
public sealed record CreateProductRequest(
    string? Name,
    string? Description,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? DefaultUnitId,
    decimal? TaxRate,
    string? Status,
    string? Sku,
    string? Barcode,
    IReadOnlyList<CreateVariantRequest>? Variants);

/// <summary>
/// JSON merge patch (RFC 7396): omitted fields are unchanged, <c>null</c> clears <c>description</c>,
/// <c>categoryId</c> and <c>brandId</c>. <c>status</c> is <c>active</c> or <c>inactive</c> (archive via DELETE;
/// setting <c>active</c> restores an archived product).
/// </summary>
public sealed record UpdateProductRequest(
    string? Name,
    string? Description,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? DefaultUnitId,
    decimal? TaxRate,
    string? Status);

/// <summary>JSON merge patch: omitted fields are unchanged, <c>null</c> clears <c>barcode</c> and <c>name</c>.</summary>
public sealed record UpdateVariantRequest(
    string? Sku,
    string? Barcode,
    string? Name,
    IReadOnlyDictionary<string, string>? OptionValues,
    Guid? UnitId,
    string? Status);

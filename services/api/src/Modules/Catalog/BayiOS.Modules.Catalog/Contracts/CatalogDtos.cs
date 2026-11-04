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
/// <c>attributes</c>: values of the tenant's <c>variant</c> custom fields (see <c>/custom-fields</c>).
/// </summary>
public sealed record CreateVariantRequest(
    string? Sku,
    string? Barcode,
    string? Name,
    IReadOnlyDictionary<string, string>? OptionValues,
    Guid? UnitId,
    string? Status,
    IReadOnlyDictionary<string, JsonElement>? Attributes = null);

/// <summary>
/// Create a product. A simple product passes <c>sku</c> (and optionally <c>barcode</c>, <c>variantAttributes</c>)
/// and gets one default variant; a product with options passes <c>variants</c> instead (1–100).
/// <c>attributes</c>: values of the tenant's <c>product</c> custom fields; unknown keys, wrong types and missing
/// required fields are rejected with 422 (error keys <c>attributes.&lt;key&gt;</c>).
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
    IReadOnlyList<CreateVariantRequest>? Variants,
    IReadOnlyDictionary<string, JsonElement>? Attributes = null,
    IReadOnlyDictionary<string, JsonElement>? VariantAttributes = null);

/// <summary>
/// JSON merge patch (RFC 7396): omitted fields are unchanged, <c>null</c> clears <c>description</c>,
/// <c>categoryId</c> and <c>brandId</c>. <c>status</c> is <c>active</c> or <c>inactive</c> (archive via DELETE;
/// setting <c>active</c> restores an archived product). <c>attributes</c> is merged member by member: a value sets
/// the field, <c>null</c> removes it; the result is validated against the product custom fields.
/// </summary>
public sealed record UpdateProductRequest(
    string? Name,
    string? Description,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? DefaultUnitId,
    decimal? TaxRate,
    string? Status,
    IReadOnlyDictionary<string, JsonElement?>? Attributes = null);

/// <summary>
/// JSON merge patch: omitted fields are unchanged, <c>null</c> clears <c>barcode</c> and <c>name</c>.
/// <c>attributes</c> is merged like on products and validated against the variant custom fields.
/// </summary>
public sealed record UpdateVariantRequest(
    string? Sku,
    string? Barcode,
    string? Name,
    IReadOnlyDictionary<string, string>? OptionValues,
    Guid? UnitId,
    string? Status,
    IReadOnlyDictionary<string, JsonElement?>? Attributes = null);

/// <summary>A tenant's custom field (PRD CAT-05). <c>key</c>, <c>entityType</c> and <c>dataType</c> are immutable.</summary>
public sealed record CustomFieldResponse(
    Guid Id,
    string EntityType,
    string Key,
    string Label,
    string DataType,
    string? Unit,
    IReadOnlyList<string> Options,
    bool Required,
    bool Filterable,
    int SortOrder,
    bool IsArchived,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Create a custom field. <c>entityType</c>: product | variant. <c>key</c>: <c>^[a-z][a-z0-9_]{0,49}$</c>, unique in the
/// tenant across products and variants. <c>dataType</c>: text | number | integer | boolean | enum | multi_enum | range |
/// date. <c>options</c> (1–100 distinct values) only for enum/multi_enum; <c>unit</c> only for number/integer/range.
/// Value shapes: text/date/enum → string (date <c>yyyy-MM-dd</c>), number/integer → number, boolean → bool,
/// multi_enum → string array, range → <c>{"min": n, "max": n}</c>.
/// </summary>
public sealed record CreateCustomFieldRequest(
    string? EntityType,
    string? Key,
    string? Label,
    string? DataType,
    string? Unit,
    IReadOnlyList<string>? Options,
    bool? Required,
    bool? Filterable,
    int? SortOrder);

/// <summary>JSON merge patch: omitted fields are unchanged; <c>null</c> clears <c>unit</c>.</summary>
public sealed record UpdateCustomFieldRequest(
    string? Label,
    string? Unit,
    IReadOnlyList<string>? Options,
    bool? Required,
    bool? Filterable,
    int? SortOrder);

public sealed record SectorTemplateUnit(string Code, string Name, bool AllowsFraction);

public sealed record SectorTemplateField(
    string EntityType,
    string Key,
    string Label,
    string DataType,
    string? Unit,
    IReadOnlyList<string> Options,
    bool Required,
    bool Filterable);

/// <summary>A sector template (PRD TEN-03): configuration, not per-sector tables.</summary>
public sealed record SectorTemplateResponse(
    string Code,
    string Name,
    IReadOnlyList<SectorTemplateUnit> Units,
    IReadOnlyList<string> Categories,
    IReadOnlyList<SectorTemplateField> Fields);

/// <summary>
/// Result of applying a template. Applying is idempotent: units (by code, archived included), root categories (by
/// name) and custom fields (by key) that already exist are skipped, never duplicated or overwritten.
/// </summary>
public sealed record ApplySectorTemplateResponse(
    string Template,
    IReadOnlyList<string> UnitsCreated,
    IReadOnlyList<string> CategoriesCreated,
    IReadOnlyList<string> FieldsCreated,
    int Skipped);

using System.Text.Json.Nodes;
using BayiOS.SharedKernel.Platform.Jobs;

namespace BayiOS.Modules.Imports.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Ports. Imports references no other module (system-architecture §6: imported data is written through the owning
// module's commands). The host implements them over Catalog's ProductService, Pricing's PriceListService and
// Identity's membership lookup, on the request/job UnitOfWork connection — never by writing those tables directly.
// ------------------------------------------------------------------------------------------------------------------

/// <summary>An existing catalog variant with the product fields an import may change (for matching and diffs).</summary>
public sealed record CatalogVariantSnapshot(
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string? Barcode,
    Guid UnitId,
    string VariantStatus,
    long VariantVersion,
    string ProductName,
    string? Description,
    decimal TaxRate,
    Guid? BrandId,
    Guid? CategoryId,
    string ProductStatus,
    long ProductVersion,
    string ProductAttributes = "{}",
    string VariantAttributes = "{}");

/// <summary>A category by name; <see cref="Ambiguous"/> when several non-archived categories share the name.</summary>
public sealed record CatalogCategoryMatch(Guid Id, bool Ambiguous);

/// <summary>
/// Values a product row carries. Null = "not in the file": on create the Catalog default applies, on update the field
/// stays unchanged. Codes are already normalised (SKU upper-case, unit code lower-case).
/// <paramref name="Attributes"/>: custom field key → normalised JSON value (already checked by Catalog's validator);
/// the host routes each key to the product or the variant by the field's entity type.
/// </summary>
public sealed record ImportProductValues(
    string? Sku,
    string? Barcode,
    string? Name,
    string? Description,
    Guid? UnitId,
    decimal? TaxRate,
    Guid? BrandId,
    Guid? CategoryId,
    string? Status,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>Result of one catalog write.</summary>
/// <param name="Outcome"><c>created</c>, <c>updated</c>, <c>unchanged</c>, <c>archived</c> or <c>failed</c>.</param>
/// <param name="PreviousValuesJson">For updates: JSON object of the changed fields' previous values (<see cref="ImportCatalogFields"/> keys).</param>
/// <param name="Errors">Field → messages when <paramref name="Outcome"/> is <c>failed</c>.</param>
/// <param name="AppliedValuesJson">For updates: the same fields as stored after the write (rollback conflict check).</param>
public sealed record CatalogWriteResult(
    string Outcome,
    Guid? VariantId,
    Guid? ProductId,
    string? PreviousValuesJson,
    IReadOnlyDictionary<string, string[]>? Errors,
    string? ErrorCode = null,
    string? AppliedValuesJson = null);

/// <summary>A tenant custom field an import column can target as <c>attr.&lt;key&gt;</c>.</summary>
/// <param name="EntityType"><c>product</c> or <c>variant</c>.</param>
/// <param name="DataType">text, number, integer, boolean, enum, multi_enum, range, date.</param>
public sealed record ImportAttributeField(
    string Key, string Label, string EntityType, string DataType, bool Required, bool Archived, IReadOnlyList<string> Options);

/// <summary>Catalog's custom-field validator for one tenant, loaded once per validation run.</summary>
public interface IImportAttributeNormalizer
{
    IReadOnlyDictionary<string, ImportAttributeField> Fields { get; }

    /// <summary>The stored (normalised) JSON form of <paramref name="value"/>, or null with Catalog's error message.</summary>
    string? Normalize(string key, JsonNode? value, out string? error);
}

/// <summary>Catalog reads and writes for imports (implemented by the host over the Catalog module).</summary>
public interface IImportCatalogGateway
{
    /// <summary>Non-archived and archived variants whose SKU or barcode is in the given (normalised) sets.</summary>
    Task<IReadOnlyList<CatalogVariantSnapshot>> FindVariantsAsync(
        IReadOnlyCollection<string> skus, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken);

    /// <summary>Variants (archived included) by id, with their product.</summary>
    Task<IReadOnlyList<CatalogVariantSnapshot>> FindVariantsByIdAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>The tenant's custom fields with Catalog's validator (for <c>attr.&lt;key&gt;</c> columns).</summary>
    Task<IImportAttributeNormalizer> LoadAttributesAsync(CancellationToken cancellationToken);

    /// <summary>Archives the variant's product through the Catalog archive command (rollback of a created product).</summary>
    Task<CatalogWriteResult> ArchiveProductAsync(CatalogVariantSnapshot current, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="previousValuesJson"/> (<see cref="ImportCatalogFields"/> keys; null clears) back through
    /// the Catalog product/variant commands, with the snapshot's versions as the concurrency check.
    /// </summary>
    Task<CatalogWriteResult> RestoreProductAsync(CatalogVariantSnapshot current, string previousValuesJson, CancellationToken cancellationToken);

    /// <summary>Non-archived units by normalised code.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindUnitsAsync(CancellationToken cancellationToken);

    /// <summary>Non-archived brands by name (case-insensitive, Turkish-folded key).</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindBrandsAsync(CancellationToken cancellationToken);

    /// <summary>Non-archived categories by name (case-insensitive, Turkish-folded key).</summary>
    Task<IReadOnlyDictionary<string, CatalogCategoryMatch>> FindCategoriesAsync(CancellationToken cancellationToken);

    /// <summary>Creates a simple product (one variant) through the Catalog product command.</summary>
    Task<CatalogWriteResult> CreateProductAsync(ImportProductValues values, CancellationToken cancellationToken);

    /// <summary>Applies the non-null values to the variant and its product; <c>unchanged</c> when nothing differs.</summary>
    Task<CatalogWriteResult> UpdateProductAsync(CatalogVariantSnapshot current, ImportProductValues values, CancellationToken cancellationToken);
}

public sealed record ImportPriceListInfo(Guid Id, string Name, string Kind, string Currency, string Status);

public sealed record ImportPriceItem(Guid VariantId, decimal MinQuantity, decimal UnitPrice);

/// <summary>Result of a price upsert batch.</summary>
/// <param name="ItemIds">Price list item id per input index (null for failed inputs).</param>
/// <param name="Errors">Input index → messages for inputs Pricing rejected.</param>
public sealed record PriceUpsertResult(IReadOnlyList<Guid?> ItemIds, IReadOnlyDictionary<int, string[]> Errors, string? BatchError);

/// <summary>Pricing reads and writes for imports (implemented by the host over the Pricing module).</summary>
public interface IImportPriceGateway
{
    Task<ImportPriceListInfo?> FindPriceListAsync(Guid priceListId, CancellationToken cancellationToken);

    /// <summary>Current item prices of the list for the given variants, keyed by (variant, min quantity).</summary>
    Task<IReadOnlyDictionary<(Guid VariantId, decimal MinQuantity), decimal>> FindPricesAsync(
        Guid priceListId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Upserts ≤ 500 items through the Pricing command (keyed by variant + min quantity).</summary>
    Task<PriceUpsertResult> UpsertPricesAsync(Guid priceListId, IReadOnlyList<ImportPriceItem> items, CancellationToken cancellationToken);

    /// <summary>Current items of the list for the given variants, keyed by (variant, min quantity).</summary>
    Task<IReadOnlyDictionary<(Guid VariantId, decimal MinQuantity), ImportPriceItemInfo>> FindPriceItemsAsync(
        Guid priceListId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Deletes one item through the Pricing command; null on success, else the error message.</summary>
    Task<string?> DeletePriceItemAsync(Guid priceListId, Guid itemId, CancellationToken cancellationToken);
}

public sealed record ImportPriceItemInfo(Guid ItemId, decimal UnitPrice);

/// <summary>The current role of a user in the current tenant (active membership), or null.</summary>
public interface IImportActorDirectory
{
    Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>A job of an import run that exhausted its retries (<c>dead</c>) while no job of the same run is live.</summary>
public sealed record DeadImportJob(Guid TenantId, string Type, Guid ImportId, Guid RunId);

/// <summary>
/// System-level job access for the Imports maintenance (raw file purge, stalled import sweep). The platform job API
/// only enqueues for the current tenant; these primitives are what a tenant-less scheduler needs (host-implemented
/// over the platform job store, no new platform API).
/// </summary>
public interface IImportSystemJobs
{
    /// <summary>False when this instance runs no background worker (<c>Jobs:Enabled=false</c>).</summary>
    bool WorkerEnabled { get; }

    /// <summary>Enqueues a tenant-less job on its own connection (scheduler tick); deduplicated by key.</summary>
    Task EnqueueSystemAsync(string type, string dedupeKey, CancellationToken cancellationToken);

    /// <summary>
    /// Dead jobs of the given types that finished within <paramref name="lookback"/> and whose run has no
    /// queued/running job left (payload <c>{importId, runId}</c>). Runs on the current unit of work.
    /// </summary>
    Task<IReadOnlyList<DeadImportJob>> FindDeadJobsAsync(
        IReadOnlyCollection<string> types, TimeSpan lookback, int limit, CancellationToken cancellationToken);

    /// <summary>Enqueues a job for <paramref name="tenantId"/> on the current (tenant-less) unit of work transaction.</summary>
    Task EnqueueForTenantAsync(Guid tenantId, JobRequest job, CancellationToken cancellationToken);
}

/// <summary>
/// The catalog fields an import writes, as one JSON object: <c>name, description, taxRate, brandId, categoryId,
/// status, sku, barcode, unitId</c> and <c>attr.&lt;key&gt;</c> for custom fields. Previous and applied values of
/// import rows use these keys, so execution (host) and rollback (Imports) compare like with like.
/// </summary>
public static class ImportCatalogFields
{
    public const string AttributePrefix = "attr.";

    public static JsonObject Read(CatalogVariantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var values = new JsonObject
        {
            ["name"] = snapshot.ProductName,
            ["description"] = snapshot.Description,
            ["taxRate"] = snapshot.TaxRate,
            ["brandId"] = snapshot.BrandId?.ToString(),
            ["categoryId"] = snapshot.CategoryId?.ToString(),
            ["status"] = snapshot.ProductStatus,
            ["sku"] = snapshot.Sku,
            ["barcode"] = snapshot.Barcode,
            ["unitId"] = snapshot.UnitId.ToString(),
        };
        foreach (var json in new[] { snapshot.ProductAttributes, snapshot.VariantAttributes })
        {
            if (JsonNode.Parse(string.IsNullOrEmpty(json) ? "{}" : json) is JsonObject attributes)
            {
                foreach (var (key, value) in attributes)
                {
                    values[AttributePrefix + key] = value?.DeepClone();
                }
            }
        }

        return values;
    }

    /// <summary>JSON equality where numbers compare by value (<c>18</c> = <c>18.00</c>) and null = absent.</summary>
    public static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        switch (a, b)
        {
            case (JsonObject x, JsonObject y):
                return x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var other) && Same(p.Value, other));
            case (JsonArray x, JsonArray y):
                return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));
            case (JsonValue x, JsonValue y) when x.GetValueKind() == System.Text.Json.JsonValueKind.Number && y.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                return x.TryGetValue<decimal>(out var dx) && y.TryGetValue<decimal>(out var dy) ? dx == dy : JsonNode.DeepEquals(x, y);
            default:
                return JsonNode.DeepEquals(a, b);
        }
    }
}

// ------------------------------------------------------------------------------------------------------------------
// Column suggestion (PRD IMP-03/IMP-04). Suggestions are never applied by the server: the client shows them and
// saves an explicit mapping. The MVP provider is deterministic (header synonyms); an AI provider can implement the
// same interface later, but it may only propose header → field pairs, never row values.
// ------------------------------------------------------------------------------------------------------------------

public sealed record ImportFieldInfo(string Key, string Label, bool RequiredForCreate, bool RequiredAlways);

/// <param name="SampleRows">Up to 5 raw data rows, cells in header order.</param>
public sealed record ColumnSuggestionContext(
    string ImportType,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string?>> SampleRows,
    IReadOnlyList<ImportFieldInfo> Fields);

/// <param name="Confidence">0–1.</param>
/// <param name="Source"><c>synonym</c>, <c>partial</c>, <c>sample_values</c> or <c>template</c>.</param>
public sealed record ColumnSuggestion(string Field, string Header, decimal Confidence, string Source);

public interface IColumnSuggestionProvider
{
    Task<IReadOnlyList<ColumnSuggestion>> SuggestAsync(ColumnSuggestionContext context, CancellationToken cancellationToken);
}

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
    long ProductVersion);

/// <summary>A category by name; <see cref="Ambiguous"/> when several non-archived categories share the name.</summary>
public sealed record CatalogCategoryMatch(Guid Id, bool Ambiguous);

/// <summary>
/// Values a product row carries. Null = "not in the file": on create the Catalog default applies, on update the field
/// stays unchanged. Codes are already normalised (SKU upper-case, unit code lower-case).
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
    string? Status);

/// <summary>Result of one catalog write.</summary>
/// <param name="Outcome"><c>created</c>, <c>updated</c>, <c>unchanged</c> or <c>failed</c>.</param>
/// <param name="PreviousValuesJson">For updates: JSON object of the changed fields' previous values.</param>
/// <param name="Errors">Field → messages when <paramref name="Outcome"/> is <c>failed</c>.</param>
public sealed record CatalogWriteResult(
    string Outcome,
    Guid? VariantId,
    Guid? ProductId,
    string? PreviousValuesJson,
    IReadOnlyDictionary<string, string[]>? Errors,
    string? ErrorCode = null);

/// <summary>Catalog reads and writes for imports (implemented by the host over the Catalog module).</summary>
public interface IImportCatalogGateway
{
    /// <summary>Non-archived and archived variants whose SKU or barcode is in the given (normalised) sets.</summary>
    Task<IReadOnlyList<CatalogVariantSnapshot>> FindVariantsAsync(
        IReadOnlyCollection<string> skus, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken);

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
}

/// <summary>The current role of a user in the current tenant (active membership), or null.</summary>
public interface IImportActorDirectory
{
    Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
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

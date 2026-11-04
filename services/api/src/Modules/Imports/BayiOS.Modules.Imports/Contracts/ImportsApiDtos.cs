using Microsoft.AspNetCore.Http;

namespace BayiOS.Modules.Imports.Contracts;

/// <summary>
/// Multipart upload (≤ 10 MB): <c>file</c> (.xlsx or .csv) and <c>type</c> (<c>products</c> | <c>prices</c>).
/// Only documents the request shape; the endpoint reads the form itself with hard limits.
/// </summary>
public sealed record UploadImportForm(IFormFile File, string Type);

public sealed record ImportPreviewCounts(int Create, int Update, int Skip, int Error);

/// <param name="PlannedRows">Rows the preview will create or update.</param>
/// <param name="Percent">0–100, processed / planned.</param>
public sealed record ImportProgress(int PlannedRows, int ProcessedRows, int Percent, int Created, int Updated, int Skipped, int Failed);

public sealed record ImportFailure(string Code, string Message);

/// <param name="Mapping">Field → source header; null until mapped.</param>
/// <param name="DuplicateOfImportId">
/// A completed import of the same type and identical file content (PRD IMP-09). Only a warning: re-importing is
/// idempotent by SKU/barcode and never duplicates products.
/// </param>
public sealed record ImportJobResponse(
    Guid Id,
    string Type,
    string Status,
    string FileName,
    long FileSizeBytes,
    string FileSha256,
    string FileFormat,
    string? FileEncoding,
    string? Delimiter,
    string? SheetName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string?>> SampleRows,
    int TotalRows,
    IReadOnlyDictionary<string, string>? Mapping,
    string MatchKey,
    string Mode,
    Guid? PriceListId,
    ImportPreviewCounts Preview,
    ImportProgress Progress,
    ImportFailure? Failure,
    Guid? DuplicateOfImportId,
    long Version,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? StartedAt,
    Guid? StartedBy,
    DateTimeOffset? FinishedAt);

public sealed record ImportPageInfo(string? NextCursor, bool HasMore, int Limit);

public sealed record ImportJobPageResponse(IReadOnlyList<ImportJobResponse> Items, ImportPageInfo Page);

/// <summary>
/// Explicit column mapping (PRD IMP-03). <c>columns</c>: field → source header exactly as in <c>headers</c>.
/// Product fields: sku, barcode, name, description, unit, taxRate, brand, category, status, price. Price fields: sku,
/// barcode, price, minQuantity. <c>matchKey</c>: sku (default) | barcode; <c>mode</c>: upsert (default) |
/// create_only | update_only. <c>priceListId</c> is required for price imports and when a product import maps
/// <c>price</c>. <c>saveAsTemplate</c> stores the mapping under that name for the next upload (replacing a
/// template of the same name and type).
/// </summary>
public sealed record SaveImportMappingRequest(
    IReadOnlyDictionary<string, string>? Columns,
    string? MatchKey,
    string? Mode,
    Guid? PriceListId,
    string? SaveAsTemplate);

public sealed record ImportFieldResponse(string Key, string Label, bool RequiredForCreate, bool Required);

/// <param name="Suggestions">Best header per field from the suggestion provider (never auto-applied).</param>
/// <param name="Templates">Saved templates of this type whose headers all exist in the file.</param>
public sealed record MappingSuggestionsResponse(
    string Type,
    IReadOnlyList<string> Headers,
    IReadOnlyList<ImportFieldResponse> Fields,
    IReadOnlyList<ColumnSuggestion> Suggestions,
    IReadOnlyList<MappingTemplateResponse> Templates);

public sealed record ImportRowError(string? Field, string? Column, string Code, string Message);

/// <param name="Action">Validation decision: create | update | skip | error.</param>
/// <param name="Status">valid | invalid | skipped | imported | failed.</param>
/// <param name="Outcome">Execution result: created | updated | unchanged | skipped | failed.</param>
public sealed record ImportRowResponse(
    int RowNumber,
    string Action,
    string Status,
    string? Outcome,
    IReadOnlyList<string?> Raw,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<ImportRowError> Errors,
    Guid? MatchedVariantId,
    Guid? TargetId);

public sealed record ImportRowPageResponse(IReadOnlyList<ImportRowResponse> Items, ImportPageInfo Page);

public sealed record MappingTemplateResponse(
    Guid Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, string> Columns,
    string MatchKey,
    string Mode,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record MappingTemplateListResponse(IReadOnlyList<MappingTemplateResponse> Items);

/// <summary>Create or replace a mapping template. <c>columns</c>: field → source header name.</summary>
public sealed record SaveMappingTemplateRequest(
    string? Name,
    string? Type,
    IReadOnlyDictionary<string, string>? Columns,
    string? MatchKey,
    string? Mode);

using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Imports.Domain;

/// <summary>Import types (PRD IMP-02). Stock and customer imports are not built yet (ROADMAP).</summary>
internal static class ImportTypes
{
    public const string Products = "products";
    public const string Prices = "prices";

    public static readonly IReadOnlyList<string> All = [Products, Prices];
}

/// <summary>
/// Lifecycle: <c>uploaded → mapped → validating → validated → running → completed</c>; <c>failed</c> and
/// <c>cancelled</c> are terminal for a run but a validated import can be started again (resume from its checkpoint).
/// </summary>
internal static class ImportStatuses
{
    public const string Uploaded = "uploaded";
    public const string Mapped = "mapped";
    public const string Validating = "validating";
    public const string Validated = "validated";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Uploaded, Mapped, Validating, Validated, Running, Completed, Failed, Cancelled];
}

/// <summary>PRD IMP-06: what happens to rows that match / do not match an existing record.</summary>
internal static class ImportModes
{
    public const string Upsert = "upsert";
    public const string CreateOnly = "create_only";
    public const string UpdateOnly = "update_only";

    public static readonly IReadOnlyList<string> All = [Upsert, CreateOnly, UpdateOnly];
}

/// <summary>Match key (PRD IMP-06): SKU by default, or barcode.</summary>
internal static class MatchKeys
{
    public const string Sku = "sku";
    public const string Barcode = "barcode";

    public static readonly IReadOnlyList<string> All = [Sku, Barcode];
}

/// <summary>The planned action of a row after validation (the preview).</summary>
internal static class RowActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Skip = "skip";
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All = [Create, Update, Skip, Error];
}

/// <summary>
/// Row processing state: <c>valid</c> rows (create/update) wait for execution; <c>invalid</c> and <c>skipped</c> are
/// decided by validation; execution turns <c>valid</c> into <c>imported</c>, <c>skipped</c> (e.g. nothing changed any
/// more) or <c>failed</c>. A row that left <c>valid</c> is never processed again (row checkpoint).
/// </summary>
internal static class RowStatuses
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Skipped = "skipped";
    public const string Imported = "imported";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Valid, Invalid, Skipped, Imported, Failed];
}

/// <summary>What execution actually did to a row.</summary>
internal static class RowOutcomes
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>Shared columns: tenant, audit stamps, optimistic concurrency (ETag source).</summary>
internal abstract class ImportsEntity : ITenantOwned
{
    public Guid Id { get; protected init; }

    public Guid TenantId { get; protected init; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; protected init; }

    public Guid? CreatedBy { get; protected init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Touch(Guid? userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId ?? UpdatedBy;
    }
}

/// <summary>
/// imports.import_jobs (data-model §5.9): one uploaded file and its import run. Counters are denormalised from the
/// rows so status polling is a single-row read.
/// </summary>
internal sealed class ImportJob : ImportsEntity
{
    private ImportJob()
    {
    }

    public string Type { get; private init; } = ImportTypes.Products;

    public string Status { get; set; } = ImportStatuses.Uploaded;

    public string FileName { get; private init; } = string.Empty;

    public long FileSizeBytes { get; private init; }

    /// <summary>Lower-case hex SHA-256 of the uploaded bytes (PRD IMP-09 duplicate warning).</summary>
    public string FileSha256 { get; private init; } = string.Empty;

    /// <summary><c>xlsx</c> or <c>csv</c>.</summary>
    public string FileFormat { get; private init; } = string.Empty;

    /// <summary>Detected text encoding of a CSV (<c>utf-8</c>, <c>windows-1254</c>, …); null for xlsx.</summary>
    public string? FileEncoding { get; private init; }

    /// <summary>Detected CSV delimiter; null for xlsx.</summary>
    public string? Delimiter { get; private init; }

    public string? SheetName { get; private init; }

    /// <summary>jsonb string array: the header row (normalised: unique, never empty).</summary>
    public string Headers { get; private init; } = "[]";

    /// <summary>jsonb array of up to 5 raw data rows (for the mapping screen).</summary>
    public string SampleRows { get; private init; } = "[]";

    public int TotalRows { get; private init; }

    /// <summary>A completed import of the same type and file hash, if any (warning only, PRD IMP-09).</summary>
    public Guid? DuplicateOfImportId { get; private init; }

    /// <summary>jsonb object field → source header; null until mapped.</summary>
    public string? Mapping { get; set; }

    public string MatchKey { get; set; } = MatchKeys.Sku;

    public string Mode { get; set; } = ImportModes.Upsert;

    /// <summary>Target price list (price imports, or product imports that map a price column).</summary>
    public Guid? PriceListId { get; set; }

    public int PreviewCreate { get; set; }

    public int PreviewUpdate { get; set; }

    public int PreviewSkip { get; set; }

    public int PreviewError { get; set; }

    public int ProcessedRows { get; set; }

    public int CreatedRows { get; set; }

    public int UpdatedRows { get; set; }

    public int SkippedRows { get; set; }

    public int FailedRows { get; set; }

    /// <summary>Highest source row number whose execution is committed (row checkpoint).</summary>
    public int CheckpointRow { get; set; }

    /// <summary>Identifies the current validate/execute run; jobs of an older run are ignored.</summary>
    public Guid? RunId { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    public DateTimeOffset? ValidatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>The user whose permissions the execution runs with (re-checked on every chunk).</summary>
    public Guid? StartedBy { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int PlannedRows => PreviewCreate + PreviewUpdate;

    public static ImportJob Create(
        Guid tenantId,
        string type,
        string fileName,
        long fileSizeBytes,
        string fileSha256,
        string fileFormat,
        string? fileEncoding,
        string? delimiter,
        string? sheetName,
        string headersJson,
        string sampleRowsJson,
        int totalRows,
        Guid? duplicateOf,
        Guid userId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Type = type,
            FileName = fileName,
            FileSizeBytes = fileSizeBytes,
            FileSha256 = fileSha256,
            FileFormat = fileFormat,
            FileEncoding = fileEncoding,
            Delimiter = delimiter,
            SheetName = sheetName,
            Headers = headersJson,
            SampleRows = sampleRowsJson,
            TotalRows = totalRows,
            DuplicateOfImportId = duplicateOf,
            CreatedAt = now,
            CreatedBy = userId,
        };

    public void ResetPreview()
    {
        PreviewCreate = PreviewUpdate = PreviewSkip = PreviewError = 0;
        ValidatedAt = null;
        FailureCode = FailureMessage = null;
    }
}

/// <summary>
/// imports.import_files: the raw uploaded bytes (≤ 10 MB) behind <see cref="Infrastructure.Files.IImportFileStore"/>.
/// Kept in the database until hosted object storage exists; the store interface lets Supabase Storage replace it.
/// </summary>
internal sealed class ImportFile : ITenantOwned
{
    private ImportFile()
    {
    }

    public Guid ImportJobId { get; private init; }

    public Guid TenantId { get; private init; }

    public byte[] Content { get; private init; } = [];

    public DateTimeOffset CreatedAt { get; private init; }

    public static ImportFile Create(Guid tenantId, Guid importJobId, byte[] content, DateTimeOffset now) => new()
    {
        ImportJobId = importJobId,
        TenantId = tenantId,
        Content = content,
        CreatedAt = now,
    };
}

/// <summary>imports.import_rows: one staged data row with its preview decision and execution result.</summary>
internal sealed class ImportRow : ITenantOwned
{
    private ImportRow()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid ImportJobId { get; private init; }

    /// <summary>1-based row number in the source file (the header is row 1).</summary>
    public int RowNumber { get; private init; }

    /// <summary>jsonb string array: the raw cells in header order.</summary>
    public string Raw { get; private init; } = "[]";

    /// <summary>jsonb object of normalised field values.</summary>
    public string Normalized { get; private init; } = "{}";

    public string Action { get; private init; } = RowActions.Error;

    public string Status { get; set; } = RowStatuses.Invalid;

    /// <summary>jsonb array of <c>{field, column, code, message}</c>.</summary>
    public string Errors { get; set; } = "[]";

    /// <summary>The existing variant matched during validation, if any.</summary>
    public Guid? MatchedVariantId { get; private init; }

    public string? Outcome { get; set; }

    /// <summary>The created/updated record (variant id; price list item for price imports).</summary>
    public Guid? TargetId { get; set; }

    /// <summary>jsonb: previous values of the fields execution changed (for a later rollback, PRD IMP-12).</summary>
    public string? PreviousValues { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public static ImportRow Create(
        Guid tenantId, Guid importJobId, int rowNumber, string rawJson, string normalizedJson, string action, string status, string errorsJson, Guid? matchedVariantId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ImportJobId = importJobId,
            RowNumber = rowNumber,
            Raw = rawJson,
            Normalized = normalizedJson,
            Action = action,
            Status = status,
            Errors = errorsJson,
            MatchedVariantId = matchedVariantId,
        };
}

/// <summary>imports.mapping_templates: a saved column mapping (PRD IMP-03), keyed by source header names.</summary>
internal sealed class MappingTemplate : ImportsEntity
{
    private MappingTemplate()
    {
    }

    public string Name { get; set; } = string.Empty;

    public string Type { get; private init; } = ImportTypes.Products;

    /// <summary>jsonb object field → source header.</summary>
    public string Mapping { get; set; } = "{}";

    public string MatchKey { get; set; } = MatchKeys.Sku;

    public string Mode { get; set; } = ImportModes.Upsert;

    public static MappingTemplate Create(Guid tenantId, string name, string type, string mappingJson, string matchKey, string mode, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Name = name,
        Type = type,
        Mapping = mappingJson,
        MatchKey = matchKey,
        Mode = mode,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

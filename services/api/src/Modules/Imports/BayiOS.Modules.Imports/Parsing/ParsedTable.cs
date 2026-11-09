namespace BayiOS.Modules.Imports.Parsing;

/// <summary>
/// Upload and parsing limits (threat-model §6.2). File size and row count follow the MVP job contract
/// (20 MB — owner decision OD-04, 2026-11-09 — / 10,000 data rows); the zip limits guard xlsx decompression. The
/// decompressed limits deliberately do not grow with the upload size: they bound the work of parsing at most
/// 10,000 rows × 100 columns, and an .xlsx that is legitimately large is so because of already-compressed parts
/// (images), which decompress to about their own size.
/// </summary>
internal static class ImportLimits
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const int MaxDataRows = 10_000;
    public const int MaxColumns = 100;
    public const int MaxCellLength = 4_000;
    public const int MaxHeaderLength = 200;
    public const int SampleRowCount = 5;

    public const int MaxZipEntries = 1_000;
    public const long MaxZipEntryBytes = 50L * 1024 * 1024;
    public const long MaxZipTotalBytes = 100L * 1024 * 1024;

    /// <summary>Compression ratio limit for the archive as a whole and for any entry larger than 1 MB.</summary>
    public const int MaxCompressionRatio = 100;
    public const long RatioCheckThresholdBytes = 1024 * 1024;

    public static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(60);
}

/// <summary>
/// A file the import refuses. <see cref="Code"/> is the stable problem code (api-strategy §6); the message is safe to
/// show (never contains file content beyond a row number).
/// </summary>
internal sealed class ImportFileRejectedException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public static ImportFileRejectedException TooLarge() =>
        new(413, "imports.file_too_large", $"The file exceeds {ImportLimits.MaxFileBytes / (1024 * 1024)} MB.");

    public static ImportFileRejectedException TypeNotAllowed(string message) => new(415, "imports.file_type_not_allowed", message);

    public static ImportFileRejectedException MacroNotAllowed() =>
        new(415, "imports.file_macro_not_allowed", "Macro-enabled workbooks (.xlsm) are not accepted; save the file as .xlsx.");

    public static ImportFileRejectedException ContentMismatch(string message) => new(415, "imports.file_content_mismatch", message);

    public static ImportFileRejectedException ArchiveLimits(string message) => new(422, "imports.file_archive_limits", message);

    public static ImportFileRejectedException Invalid(string code, string message) => new(422, code, message);
}

/// <summary>Detected container format of an upload.</summary>
internal static class FileFormats
{
    public const string Xlsx = "xlsx";
    public const string Csv = "csv";
}

/// <summary>A parsed sheet: normalised headers and the non-empty data rows (cells are text, never formulas).</summary>
/// <param name="Encoding">CSV only: <c>utf-8</c>, <c>utf-16</c> or <c>windows-1254</c>.</param>
/// <param name="Delimiter">CSV only: <c>;</c>, <c>,</c> or a tab.</param>
internal sealed record ParsedTable(
    string Format,
    string? Encoding,
    string? Delimiter,
    string? SheetName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<ParsedRow> Rows);

/// <param name="RowNumber">1-based physical row in the file; the header is row 1.</param>
/// <param name="Cells">One entry per header; null for empty cells. Trimmed text.</param>
internal sealed record ParsedRow(int RowNumber, string?[] Cells);

namespace BayiOS.Modules.Reports.Export;

/// <summary>Cell type of an export column. Only <see cref="Text"/> cells carry caller-influenced text.</summary>
internal enum ReportColumnType
{
    /// <summary>Free text (names, codes, statuses): always formula-neutralised.</summary>
    Text,

    /// <summary>Whole number, written as a number.</summary>
    Integer,

    /// <summary>Quantity or amount, written as a number.</summary>
    Decimal,

    /// <summary>Calendar day (Europe/Istanbul).</summary>
    Date,
}

internal sealed record ReportColumn(string Header, ReportColumnType Type);

/// <summary>
/// One export: a title (sheet name), a file name stem and typed rows. A cell is <c>null</c> (empty), a
/// <see cref="string"/> (text), <see cref="long"/> / <see cref="int"/>, <see cref="decimal"/> or <see cref="DateOnly"/>
/// matching its column type. Bounded by the caller (<see cref="ReportExportLimits.MaxRows"/>).
/// </summary>
internal sealed class ReportTable(string title, string fileStem, IReadOnlyList<ReportColumn> columns)
{
    private readonly List<object?[]> _rows = [];

    public string Title { get; } = title;

    public string FileStem { get; } = fileStem;

    public IReadOnlyList<ReportColumn> Columns { get; } = columns;

    public IReadOnlyList<object?[]> Rows => _rows;

    public ReportTable Add(params object?[] cells)
    {
        if (cells.Length != Columns.Count)
        {
            throw new ArgumentException($"Expected {Columns.Count} cells, got {cells.Length}.", nameof(cells));
        }

        _rows.Add(cells);
        return this;
    }
}

internal static class ReportExportLimits
{
    /// <summary>Most data rows one export may hold (the import limit's order of magnitude; a few MB per file).</summary>
    public const int MaxRows = 20_000;

    /// <summary>Longest text written to one cell (Excel's own cell limit is 32,767).</summary>
    public const int MaxCellLength = 4_000;
}

/// <summary>
/// Spreadsheet formula-injection neutraliser (threat-model §6.1, OWASP CSV injection) — the same rule as the import
/// error report (Imports <c>CsvSanitizer</c>): a text value starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, TAB
/// or CR gets a leading apostrophe, so no spreadsheet evaluates it. Applied to every text cell of every export, in CSV
/// and in XLSX (an XLSX string cell is not a formula, but the value must stay inert when re-saved or re-typed).
/// Numbers, dates and empty cells are written typed and are not text.
/// </summary>
internal static class SpreadsheetSanitizer
{
    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value.Length > ReportExportLimits.MaxCellLength ? value[..ReportExportLimits.MaxCellLength] : value;
        return text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
    }
}

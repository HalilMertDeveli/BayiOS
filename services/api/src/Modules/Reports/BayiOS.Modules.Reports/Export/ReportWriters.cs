using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BayiOS.Modules.Reports.Export;

/// <summary>A rendered export file.</summary>
internal sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// CSV for Turkish Excel: UTF-8 with BOM (Excel otherwise guesses the ANSI code page and breaks "ş/ğ/ı"), <c>;</c>
/// as delimiter (the list separator of the tr-TR locale, where <c>,</c> is the decimal separator — a comma-delimited
/// file opens as a single column there), CRLF line ends and RFC 4180 quoting. Decimals use the Turkish decimal comma
/// without thousands separators (<c>1234,5</c>) so Excel-TR reads them as numbers; dates are ISO <c>yyyy-MM-dd</c>.
/// Machine consumers use the JSON report endpoints instead. Every text cell is formula-neutralised.
/// </summary>
internal static class CsvReportWriter
{
    public const char Delimiter = ';';
    public const string ContentType = "text/csv; charset=utf-8";

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static ExportFile Write(ReportTable table)
    {
        var builder = new StringBuilder();
        builder.AppendJoin(Delimiter, table.Columns.Select(c => Field(SpreadsheetSanitizer.Neutralize(c.Header)))).Append("\r\n");
        foreach (var row in table.Rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(Delimiter);
                }

                builder.Append(Field(Format(table.Columns[i].Type, row[i])));
            }

            builder.Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
        return new ExportFile(table.FileStem + ".csv", ContentType, bytes);
    }

    /// <summary>The cell text before quoting: text neutralised, numbers in tr-TR, dates ISO.</summary>
    public static string Format(ReportColumnType type, object? value) => value switch
    {
        null => string.Empty,
        _ when type == ReportColumnType.Text => SpreadsheetSanitizer.Neutralize(Convert.ToString(value, CultureInfo.InvariantCulture)),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal number => number.ToString("0.############", Turkish),
        long or int => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Unsupported {type} cell value {value.GetType().Name}."),
    };

    private static string Field(string value) =>
        value.IndexOfAny([Delimiter, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

/// <summary>
/// XLSX through the Open XML SDK (DocumentFormat.OpenXml, MIT, maintained by Microsoft): the worksheet is written
/// forward-only with <see cref="OpenXmlWriter"/> (no DOM of the sheet in memory). Text cells are inline strings —
/// never formulas, the writer emits no <c>&lt;f&gt;</c> element at all — and are formula-neutralised like CSV so a
/// value stays inert when the file is re-saved as CSV or a cell is re-entered. Numbers are numeric cells, days are
/// date-formatted serial numbers. Characters XML 1.0 cannot carry are replaced with U+FFFD.
/// </summary>
internal static class XlsxReportWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const uint HeaderStyle = 1;
    private const uint DateStyle = 2;

    public static ExportFile Write(ReportTable table)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = Stylesheet();

            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(sheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                WriteRow(writer, 1, table.Columns.Select(c => (object?)c.Header).ToArray(), table.Columns.Select(_ => ReportColumnType.Text).ToArray(), HeaderStyle);
                var types = table.Columns.Select(c => c.Type).ToArray();
                for (var r = 0; r < table.Rows.Count; r++)
                {
                    WriteRow(writer, (uint)r + 2, table.Rows[r], types, null);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = SheetName(table.Title) }));
            workbook.Workbook.Save();
        }

        return new ExportFile(table.FileStem + ".xlsx", ContentType, stream.ToArray());
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowIndex, object?[] cells, ReportColumnType[] types, uint? style)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } value)
            {
                continue;
            }

            var cell = new Cell { CellReference = ColumnName(i) + rowIndex.ToString(CultureInfo.InvariantCulture) };
            if (style is { } s)
            {
                cell.StyleIndex = s;
            }

            switch (value)
            {
                case var _ when types[i] == ReportColumnType.Text:
                    cell.DataType = CellValues.InlineString;
                    cell.InlineString = new InlineString(new Text(XmlSafe(SpreadsheetSanitizer.Neutralize(Convert.ToString(value, CultureInfo.InvariantCulture))))
                    {
                        Space = SpaceProcessingModeValues.Preserve,
                    });
                    break;
                case DateOnly date:
                    cell.StyleIndex = DateStyle;
                    cell.CellValue = new CellValue(date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture));
                    break;
                case decimal number:
                    cell.DataType = CellValues.Number;
                    cell.CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture));
                    break;
                case long or int:
                    cell.DataType = CellValues.Number;
                    cell.CellValue = new CellValue(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new ArgumentException($"Unsupported {types[i]} cell value {value.GetType().Name}.");
            }

            writer.WriteElement(cell);
        }

        writer.WriteEndElement();
    }

    /// <summary>Minimal stylesheet: 0 default, 1 bold header, 2 date (built-in number format 14).</summary>
    private static Stylesheet Stylesheet() => new(
        new Fonts(new Font(), new Font(new Bold())) { Count = 2 },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border()) { Count = 1 },
        new CellStyleFormats(new CellFormat()) { Count = 1 },
        new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1, ApplyFont = true },
            new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true })
        { Count = 3 });

    /// <summary>A–Z, AA–ZZ … (reports have far fewer than 26 columns, the general form costs nothing).</summary>
    public static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    /// <summary>Excel sheet names: ≤ 31 characters, none of <c>: \ / ? * [ ]</c>.</summary>
    private static string SheetName(string title)
    {
        var cleaned = new string([.. title.Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? ' ' : c)]).Trim();
        return cleaned.Length > 31 ? cleaned[..31] : cleaned.Length == 0 ? "Rapor" : cleaned;
    }

    private static string XmlSafe(string value)
    {
        if (value.All(c => XmlConvertible(c) && !char.IsSurrogate(c)))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else
            {
                builder.Append(XmlConvertible(c) && !char.IsSurrogate(c) ? c : '�');
            }
        }

        return builder.ToString();
    }

    private static bool XmlConvertible(char c) => c is '\t' or '\n' or '\r' || (c >= ' ' && c != '￾' && c != '￿');
}

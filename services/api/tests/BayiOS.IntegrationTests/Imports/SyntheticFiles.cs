using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace BayiOS.TestSupport.BulkImport;

/// <summary>
/// Builds synthetic spreadsheets in memory (no fixtures on disk, no real data): minimal OOXML workbooks with inline
/// strings and CSV in a chosen encoding. Also builds hostile files for the upload security tests.
/// Shared by the unit and integration test projects (linked source).
/// </summary>
internal static class SyntheticFiles
{
    /// <summary>A cell: string → inline string, number → numeric cell, <see cref="Formula"/> → formula + cached value.</summary>
    public sealed record Formula(string Expression, double CachedValue);

    static SyntheticFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static byte[] Xlsx(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows,
        IReadOnlyDictionary<string, byte[]>? extraEntries = null,
        string? contentTypesExtra = null,
        string? workbookPrefix = null,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>{contentTypesExtra}</Types>
                """, level);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """, level);
            Add(zip, "xl/workbook.xml", (workbookPrefix ?? """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""") + """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Urunler" sheetId="1" r:id="rId1"/></sheets></workbook>
                """, level);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                """, level);

            var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", level);
            using (var writer = new StreamWriter(sheet.Open(), new UTF8Encoding(false)))
            {
                writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
                WriteRow(writer, 1, headers.Cast<object?>().ToList());
                var r = 1;
                foreach (var row in rows)
                {
                    WriteRow(writer, ++r, row);
                }

                writer.Write("</sheetData></worksheet>");
            }

            foreach (var (name, bytes) in extraEntries ?? new Dictionary<string, byte[]>())
            {
                var entry = zip.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows, char delimiter = ';', Encoding? encoding = null, bool bom = false)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(delimiter, headers.Select(h => Quote(h, delimiter)))).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(delimiter, row.Select(v => Quote(Format(v), delimiter)))).Append("\r\n");
        }

        encoding ??= new UTF8Encoding(false);
        var body = encoding.GetBytes(builder.ToString());
        return bom ? [.. encoding.GetPreamble(), .. body] : body;
    }

    public static Encoding Windows1254 => Encoding.GetEncoding(1254);

    /// <summary>Motorcycle-parts and LED products with valid EAN-13 barcodes: SKU, name, unit, VAT, barcode, price.</summary>
    public static IEnumerable<IReadOnlyList<object?>> ProductRows(int count, string prefix = "MTR", int start = 1, decimal priceBase = 100m)
    {
        for (var i = start; i < start + count; i++)
        {
            var led = i % 2 == 0;
            yield return
            [
                $"{prefix}-{i:D5}",
                led ? $"LED Modül P{(i % 10) + 1} İç Mekân #{i}" : $"Debriyaj Balatası Şanzıman Dişlisi #{i}",
                "adet",
                20,
                Ean13($"869{i:D9}"),
                priceBase + (i % 97) + 0.5m,
            ];
        }
    }

    public static readonly string[] ProductHeaders = ["Stok Kodu", "Ürün Adı", "Birim", "KDV Oranı", "Barkod", "Fiyat"];

    /// <summary>Appends the GTIN check digit to 12 digits.</summary>
    public static string Ean13(string twelveDigits)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var digit = twelveDigits[i] - '0';
            sum += i % 2 == 0 ? digit : digit * 3;
        }

        return twelveDigits + ((10 - (sum % 10)) % 10).ToString(CultureInfo.InvariantCulture);
    }

    private static void Add(ZipArchive zip, string name, string content, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }

    private static void WriteRow(StreamWriter writer, int rowNumber, IReadOnlyList<object?> cells)
    {
        writer.Write($"<row r=\"{rowNumber}\">");
        for (var c = 0; c < cells.Count; c++)
        {
            var reference = Column(c) + rowNumber.ToString(CultureInfo.InvariantCulture);
            switch (cells[c])
            {
                case null:
                    break;
                case Formula f:
                    writer.Write($"<c r=\"{reference}\"><f>{SecurityElement.Escape(f.Expression)}</f><v>{f.CachedValue.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case int or long or decimal or double:
                    writer.Write($"<c r=\"{reference}\"><v>{Format(cells[c])}</v></c>");
                    break;
                default:
                    writer.Write($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(Format(cells[c]))}</t></is></c>");
                    break;
            }
        }

        writer.Write("</row>");
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (index++; index > 0; index = (index - 1) / 26)
        {
            name = (char)('A' + ((index - 1) % 26)) + name;
        }

        return name;
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string value, char delimiter) =>
        value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

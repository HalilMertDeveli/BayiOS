using System.IO.Compression;
using System.Text;
using BayiOS.Modules.Reports.Export;
using ExcelDataReader;

namespace BayiOS.UnitTests.Reports;

/// <summary>
/// #53 / threat-model §6.1 (<c>CsvExportSanitizerTests</c>): formula-injection neutralisation of export cells and the
/// CSV / XLSX writers — every text cell neutralised, numbers and dates typed, the XLSX parses back and holds no formula.
/// </summary>
public class ReportExportWritersTests
{
    // ExcelDataReader's configuration asks for code page 1252 even for xlsx.
    static ReportExportWritersTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static TheoryData<string> Dangerous() =>
    [
        "=HYPERLINK(\"http://evil.example/x\",\"Tıkla\")",
        "+cmd|' /C calc'!A0",
        "-2+3",
        "@SUM(1+1)",
        "\t=1+1",
        "\r=1+1",
        "=",
    ];

    [Theory]
    [MemberData(nameof(Dangerous))]
    public void Values_starting_with_a_formula_character_get_a_leading_apostrophe(string value)
    {
        Assert.Equal("'" + value, SpreadsheetSanitizer.Neutralize(value));

        // Same rule as the import error report (Imports CsvSanitizer).
        Assert.Equal(BayiOS.Modules.Imports.Parsing.CsvSanitizer.Neutralize(value), SpreadsheetSanitizer.Neutralize(value));
    }

    [Theory]
    [InlineData("Fren balatası CG125", "Fren balatası CG125")]
    [InlineData("LED-0001", "LED-0001")]
    [InlineData(" =boşlukla başlar", " =boşlukla başlar")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Other_values_are_unchanged(string? value, string expected) => Assert.Equal(expected, SpreadsheetSanitizer.Neutralize(value));

    [Fact]
    public void Very_long_text_is_cut_to_the_cell_limit()
    {
        Assert.Equal(ReportExportLimits.MaxCellLength, SpreadsheetSanitizer.Neutralize(new string('x', 10_000)).Length);
        Assert.Equal(ReportExportLimits.MaxCellLength + 1, SpreadsheetSanitizer.Neutralize("=" + new string('x', 10_000)).Length);
    }

    [Fact]
    public void Csv_is_bom_semicolon_crlf_with_turkish_decimals_neutralised_text_and_unprefixed_numbers()
    {
        var file = CsvReportWriter.Write(Table());
        Assert.Equal("kritik.csv", file.FileName);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.Content[..3]);
        var text = Encoding.UTF8.GetString(file.Content[3..]);
        Assert.Equal(
            "Gün;Ürün;Adet;Miktar\r\n"
            + "2026-11-05;\"'=HYPERLINK(\"\"x\"\")\";12;-3,5\r\n"
            + "2026-11-06;\"Ad; virgül \"\"tırnak\"\"\";-4;1234,125\r\n"
            + "2026-11-07;\"'\r=1+1\";0;0\r\n"
            + ";'-5;;\r\n",
            text);
    }

    [Fact]
    public void Xlsx_parses_back_with_neutralised_text_typed_numbers_and_dates_and_no_formula()
    {
        var file = XlsxReportWriter.Write(Table());
        Assert.Equal("kritik.xlsx", file.FileName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);

        using (var zip = new ZipArchive(new MemoryStream(file.Content)))
        {
            var sheet = zip.Entries.Single(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal));
            using var reader = new StreamReader(sheet.Open());
            var xml = reader.ReadToEnd();
            Assert.DoesNotContain("<x:f", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
        }

        using var stream = new MemoryStream(file.Content);
        using var excel = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var rows = new List<object?[]>();
        while (excel.Read())
        {
            var cells = new object?[excel.FieldCount];
            for (var i = 0; i < excel.FieldCount; i++)
            {
                cells[i] = excel.GetValue(i);
            }

            rows.Add(cells);
        }

        Assert.Equal("Rapor  Kritik", excel.Name); // ':' is not allowed in sheet names
        Assert.Equal(["Gün", "Ürün", "Adet", "Miktar"], rows[0].Select(c => (string)c!));
        Assert.Equal(new DateTime(2026, 11, 5), rows[1][0]);
        Assert.Equal("'=HYPERLINK(\"x\")", rows[1][1]);
        Assert.Equal(12d, rows[1][2]);
        Assert.Equal(-3.5d, rows[1][3]);
        Assert.Equal("Ad; virgül \"tırnak\"", rows[2][1]);
        Assert.Equal(1234.125d, rows[2][3]);
        Assert.StartsWith("'", (string)rows[3][1]!, StringComparison.Ordinal);
        Assert.Null(rows[4][0]);
        Assert.Equal("'-5", rows[4][1]);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Xlsx_replaces_characters_xml_cannot_carry()
    {
        var table = new ReportTable("T", "t", [new("Ad", ReportColumnType.Text)]).Add("a\u0001b\uD800c");
        using var stream = new MemoryStream(XlsxReportWriter.Write(table).Content);
        using var excel = ExcelReaderFactory.CreateOpenXmlReader(stream);
        excel.Read();
        excel.Read();
        Assert.Equal("a�b�c", excel.GetValue(0));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void Column_names_follow_spreadsheet_letters(int index, string name) => Assert.Equal(name, XlsxReportWriter.ColumnName(index));

    [Fact]
    public void A_row_must_match_the_columns() =>
        Assert.Throws<ArgumentException>(() => new ReportTable("T", "t", [new("A", ReportColumnType.Text)]).Add("a", "b"));

    private static ReportTable Table() =>
        new ReportTable("Rapor: Kritik", "kritik",
            [
                new("Gün", ReportColumnType.Date),
                new("Ürün", ReportColumnType.Text),
                new("Adet", ReportColumnType.Integer),
                new("Miktar", ReportColumnType.Decimal),
            ])
            .Add(new DateOnly(2026, 11, 5), "=HYPERLINK(\"x\")", 12L, -3.5m)
            .Add(new DateOnly(2026, 11, 6), "Ad; virgül \"tırnak\"", -4, 1234.125m)
            .Add(new DateOnly(2026, 11, 7), "\r=1+1", 0L, 0.000m)
            .Add(null, "-5", null, null);
}

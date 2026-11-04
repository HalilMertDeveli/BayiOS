using System.IO.Compression;
using System.Text;
using BayiOS.Modules.Imports.Parsing;
using BayiOS.TestSupport.BulkImport;

namespace BayiOS.UnitTests.BulkImport;

/// <summary>Upload safety and parsing (#21, threat-model §6.1–§6.2) on synthetic files built in memory.</summary>
public class TabularFileReaderTests
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] XlsxHeaders = ["Stok Kodu", "Ürün Adı", "KDV", "Barkod", "Toplam"];
    private static readonly string[] CsvHeaders = ["Stok Kodu", "Ürün Adı", "Açıklama"];
    private static readonly string?[] DelimitedCells = ["A-1", "1.234,50", "adet"];
    private static readonly string[] NormalisedHeaders = ["SKU", "Sütun 2", "sku (2)", "Fiyat"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Reads_xlsx_inline_strings_numbers_and_formula_cached_values_without_evaluating()
    {
        var file = SyntheticFiles.Xlsx(
            ["Stok Kodu", "Ürün Adı", "KDV", "Barkod", "Toplam"],
            [
                ["MTR-00001", "Debriyaj Balatası", 20, 8690000000019L, new SyntheticFiles.Formula("1+1", 2)],
                [null, null, null, null, null], // empty row is dropped
                ["LED-P10", "LED Modül P10", 10.5m, null, new SyntheticFiles.Formula("HYPERLINK(\"http://x\")", 0)],
            ]);

        var table = TabularFileReader.Read(file, "urunler.xlsx", XlsxType, Ct);

        Assert.Equal(FileFormats.Xlsx, table.Format);
        Assert.Equal(XlsxHeaders, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal((2, "MTR-00001", "20", "8690000000019", "2"), (table.Rows[0].RowNumber, table.Rows[0].Cells[0], table.Rows[0].Cells[2], table.Rows[0].Cells[3], table.Rows[0].Cells[4]));
        Assert.Equal((4, "10.5", "0"), (table.Rows[1].RowNumber, table.Rows[1].Cells[2], table.Rows[1].Cells[4]));
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf8")]
    [InlineData("windows-1254")]
    [InlineData("utf16")]
    public void Reads_csv_in_turkish_encodings(string encoding)
    {
        var (enc, bom, expected) = encoding switch
        {
            "utf8-bom" => ((Encoding)new UTF8Encoding(false), true, "utf-8"),
            "utf8" => (new UTF8Encoding(false), false, "utf-8"),
            "windows-1254" => (SyntheticFiles.Windows1254, false, "windows-1254"),
            _ => (Encoding.Unicode, true, "utf-16"),
        };
        var file = SyntheticFiles.Csv(
            ["Stok Kodu", "Ürün Adı", "Açıklama"],
            [["MTR-1", "Şanzıman Dişlisi İğne", "Çift sıra; ağır hizmet"], ["MTR-2", "Göstergeli Işık", "Ölçü \"12V\"\nikinci satır"]],
            ';',
            enc,
            bom);

        var table = TabularFileReader.Read(file, "urunler.csv", "text/csv", Ct);

        Assert.Equal((expected, ";"), (table.Encoding, table.Delimiter));
        Assert.Equal(CsvHeaders, table.Headers);
        Assert.Equal("Şanzıman Dişlisi İğne", table.Rows[0].Cells[1]);
        Assert.Equal("Çift sıra; ağır hizmet", table.Rows[0].Cells[2]);
        Assert.Equal("Ölçü \"12V\"\nikinci satır", table.Rows[1].Cells[2]);
    }

    [Theory]
    [InlineData(',', ",")]
    [InlineData('\t', "\t")]
    [InlineData(';', ";")]
    public void Detects_the_csv_delimiter(char delimiter, string expected)
    {
        var file = SyntheticFiles.Csv(["SKU", "Fiyat", "Birim"], [["A-1", "1.234,50", "adet"]], delimiter);
        var table = TabularFileReader.Read(file, "f.csv", null, Ct);
        Assert.Equal(expected, table.Delimiter);
        Assert.Equal(DelimitedCells, table.Rows[0].Cells);
    }

    [Fact]
    public void Normalises_blank_and_duplicate_headers()
    {
        var file = SyntheticFiles.Csv(["SKU", "", "sku", "Fiyat"], [["A", "x", "y", "1"]]);
        Assert.Equal(NormalisedHeaders, TabularFileReader.Read(file, "f.csv", null, Ct).Headers);
    }

    [Theory]
    [InlineData("urunler.xlsm", XlsxType, "imports.file_macro_not_allowed")]
    [InlineData("urunler.xltm", XlsxType, "imports.file_macro_not_allowed")]
    [InlineData("urunler.xlsx", "application/vnd.ms-excel.sheet.macroEnabled.12", "imports.file_macro_not_allowed")]
    [InlineData("urunler.xls", "application/vnd.ms-excel", "imports.file_type_not_allowed")]
    [InlineData("urunler.exe", "application/octet-stream", "imports.file_type_not_allowed")]
    [InlineData("urunler.xlsx", "text/html", "imports.file_type_not_allowed")]
    [InlineData("urunler.csv", "application/pdf", "imports.file_type_not_allowed")]
    public void Rejects_disallowed_extensions_and_content_types(string fileName, string contentType, string code)
    {
        var file = SyntheticFiles.Xlsx(["SKU"], [["A"]]);
        Assert.Equal(code, Assert.Throws<ImportFileRejectedException>(() => TabularFileReader.Read(file, fileName, contentType, Ct)).Code);
    }

    [Fact]
    public void Rejects_wrong_magic_bytes()
    {
        var csv = SyntheticFiles.Csv(["SKU"], [["A"]]);
        var xlsx = SyntheticFiles.Xlsx(["SKU"], [["A"]]);
        Assert.Equal("imports.file_content_mismatch", Reject(csv, "renamed.xlsx").Code);
        Assert.Equal("imports.file_content_mismatch", Reject(xlsx, "renamed.csv").Code);
        Assert.Equal("imports.file_content_mismatch", Reject("%PDF-1.7\n%binary"u8.ToArray(), "evil.csv").Code);
        Assert.Equal("imports.file_content_mismatch", Reject([0x4D, 0x5A, 0x90, 0x00, 0x03], "evil.csv").Code);
        Assert.Equal("imports.file_content_mismatch", Reject([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0], "legacy.csv").Code);
        Assert.Equal("imports.file_content_mismatch", Reject([0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5], "broken.xlsx").Code);

        // A zip that is not a workbook (e.g. a renamed .docx/.zip).
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("<document/>");
        }

        Assert.Equal("imports.file_content_mismatch", Reject(buffer.ToArray(), "doc.xlsx").Code);
    }

    [Fact]
    public void Rejects_vba_parts_and_macro_content_types_inside_an_xlsx()
    {
        var vba = SyntheticFiles.Xlsx(["SKU"], [["A"]], new Dictionary<string, byte[]> { ["xl/vbaProject.bin"] = [1, 2, 3] });
        Assert.Equal("imports.file_macro_not_allowed", Reject(vba, "x.xlsx").Code);

        var declared = SyntheticFiles.Xlsx(
            ["SKU"], [["A"]], contentTypesExtra: """<Override PartName="/xl/workbook.xml" ContentType="application/vnd.ms-excel.sheet.macroEnabled.main+xml"/>""");
        Assert.Equal("imports.file_macro_not_allowed", Reject(declared, "x.xlsx").Code);
    }

    [Fact]
    public void Rejects_xml_document_type_declarations()
    {
        var xxe = SyntheticFiles.Xlsx(
            ["SKU"],
            [["A"]],
            workbookPrefix: """<?xml version="1.0"?><!DOCTYPE workbook [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>""");
        Assert.Equal("imports.file_xml_dtd_not_allowed", Reject(xxe, "x.xlsx").Code);
    }

    [Fact]
    public void Rejects_zip_bombs_by_real_decompressed_size_and_ratio()
    {
        // 60 MB of zeros compresses to ~60 KB: over the 50 MB entry limit and the 100:1 ratio.
        var bomb = SyntheticFiles.Xlsx(["SKU"], [["A"]], new Dictionary<string, byte[]> { ["xl/media/padding.xml"] = new byte[60 * 1024 * 1024] }, level: CompressionLevel.SmallestSize);
        Assert.True(bomb.Length < ImportLimits.MaxFileBytes);
        Assert.Equal("imports.file_archive_limits", Reject(bomb, "bomb.xlsx").Code);

        // 1:1000 ratio well under the size limits is still refused.
        var ratio = SyntheticFiles.Xlsx(["SKU"], [["A"]], new Dictionary<string, byte[]> { ["xl/media/padding.bin.txt"] = new byte[4 * 1024 * 1024] }, level: CompressionLevel.SmallestSize);
        Assert.Equal("imports.file_archive_limits", Reject(ratio, "ratio.xlsx").Code);

        // Many tiny parts.
        var parts = Enumerable.Range(0, ImportLimits.MaxZipEntries + 1).ToDictionary(i => $"xl/custom/p{i}.txt", _ => new byte[] { 1 });
        Assert.Equal("imports.file_archive_limits", Reject(SyntheticFiles.Xlsx(["SKU"], [["A"]], parts), "parts.xlsx").Code);
    }

    [Fact]
    public void Enforces_size_row_column_and_cell_limits()
    {
        Assert.Equal("imports.file_too_large", Reject(new byte[ImportLimits.MaxFileBytes + 1], "big.csv").Code);
        Assert.Equal("imports.file_empty", Reject([], "empty.csv").Code);
        Assert.Equal("imports.file_empty", Reject(SyntheticFiles.Csv(["SKU"], []), "headers-only.csv").Code);

        var tooMany = SyntheticFiles.Csv(["SKU"], Enumerable.Range(0, ImportLimits.MaxDataRows + 1).Select(i => (IReadOnlyList<object?>)[$"S{i}"]));
        Assert.Equal("imports.file_too_many_rows", Reject(tooMany, "rows.csv").Code);
        var exactly = SyntheticFiles.Csv(["SKU"], Enumerable.Range(0, ImportLimits.MaxDataRows).Select(i => (IReadOnlyList<object?>)[$"S{i}"]));
        Assert.Equal(ImportLimits.MaxDataRows, TabularFileReader.Read(exactly, "rows.csv", null, Ct).Rows.Count);

        var tooManyXlsx = SyntheticFiles.Xlsx(["SKU"], Enumerable.Range(0, ImportLimits.MaxDataRows + 1).Select(i => (IReadOnlyList<object?>)[$"S{i}"]));
        Assert.Equal("imports.file_too_many_rows", Reject(tooManyXlsx, "rows.xlsx").Code);

        var wide = Enumerable.Range(0, ImportLimits.MaxColumns + 1).Select(i => $"K{i}").ToList();
        Assert.Equal("imports.file_too_many_columns", Reject(SyntheticFiles.Csv(wide, [wide.Cast<object?>().ToList()]), "wide.csv").Code);

        Assert.Equal("imports.file_cell_too_long", Reject(SyntheticFiles.Csv(["SKU", "Ad"], [["A", new string('x', ImportLimits.MaxCellLength + 1)]]), "long.csv").Code);
        Assert.Equal("imports.file_cell_too_long", Reject(SyntheticFiles.Xlsx(["SKU", "Ad"], [["A", new string('x', ImportLimits.MaxCellLength + 1)]]), "long.xlsx").Code);
        Assert.Equal("imports.file_unreadable", Reject("SKU;Ad\r\nA;\"unterminated"u8.ToArray(), "q.csv").Code);
    }

    private static ImportFileRejectedException Reject(byte[] file, string name) =>
        Assert.Throws<ImportFileRejectedException>(() => TabularFileReader.Read(file, name, null, Ct));
}

using BayiOS.Modules.Imports.Application;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Parsing;

namespace BayiOS.UnitTests.BulkImport;

/// <summary>Value parsing (PRD IMP-05), formula-injection neutralising (threat-model §6.1) and column suggestions (#22).</summary>
public class ImportValuesTests
{
    [Theory]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("12,5", 12.5)]
    [InlineData("%20", 20)]
    [InlineData("20%", 20)]
    [InlineData("1 250,00 TL", 1250)]
    [InlineData("₺99,90", 99.9)]
    [InlineData("-5", -5)]
    public void Parses_turkish_and_english_number_formats(string text, double expected)
    {
        Assert.True(ImportValues.TryParseDecimal(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3.4.5x")]
    [InlineData("=1+1")]
    public void Rejects_non_numbers(string text) => Assert.False(ImportValues.TryParseDecimal(text, out _));

    [Theory]
    [InlineData("8690000000012", true)] // EAN-13
    [InlineData("8690000000013", false)]
    [InlineData("96385074", true)] // EAN-8
    [InlineData("96385075", false)]
    [InlineData("036000291452", true)] // UPC-A
    [InlineData("INT-CODE-77", true)] // internal code
    [InlineData("A B", false)]
    public void Validates_barcode_check_digits(string barcode, bool valid) => Assert.Equal(valid, ImportValues.IsValidBarcode(barcode));

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\",\"x\")", "'=HYPERLINK(\"http://x\",\"x\")")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(1+1)", "'@SUM(1+1)")]
    [InlineData("\tTAB", "'\tTAB")]
    [InlineData("\rCR", "'\rCR")]
    [InlineData("Debriyaj", "Debriyaj")]
    public void Neutralises_formula_prefixes_on_export(string value, string expected) => Assert.Equal(expected, CsvSanitizer.Neutralize(value));

    [Fact]
    public void Csv_fields_are_quoted_after_neutralising()
    {
        Assert.Equal("\"'=A1;B1\"", CsvSanitizer.Field("=A1;B1", ';'));
        Assert.Equal("\"Ölçü \"\"12V\"\"\"", CsvSanitizer.Field("Ölçü \"12V\"", ';'));
    }

    [Fact]
    public void Folds_turkish_headers() => Assert.Equal("kdvorani", ImportValues.Fold("KDV Oranı (%)"));

    [Fact]
    public async Task Suggests_columns_from_turkish_and_english_headers_once_each()
    {
        var provider = new HeaderSynonymSuggestionProvider();
        var headers = new[] { "Stok Kodu", "ÜRÜN ADI", "Birim", "KDV %", "Marka", "Kategori", "Satış Fiyatı", "Açıklama", "Durum", "EAN", "Renk" };
        var result = await provider.SuggestAsync(Context(ImportTypes.Products, headers, []), TestContext.Current.CancellationToken);

        var map = result.ToDictionary(s => s.Field, s => s.Header);
        Assert.Equal("Stok Kodu", map["sku"]);
        Assert.Equal("ÜRÜN ADI", map["name"]);
        Assert.Equal("Birim", map["unit"]);
        Assert.Equal("KDV %", map["taxRate"]);
        Assert.Equal("Marka", map["brand"]);
        Assert.Equal("Kategori", map["category"]);
        Assert.Equal("Satış Fiyatı", map["price"]);
        Assert.Equal("Açıklama", map["description"]);
        Assert.Equal("Durum", map["status"]);
        Assert.Equal("EAN", map["barcode"]);
        Assert.DoesNotContain(result, s => s.Header == "Renk");
        Assert.Equal(result.Count, result.Select(s => s.Header).Distinct().Count());
        Assert.All(result, s => Assert.Equal(HeaderSynonymSuggestionProvider.ExactScore, s.Confidence));

        var english = await provider.SuggestAsync(Context(ImportTypes.Prices, ["Item Code", "Unit Price", "Min Qty"], []), TestContext.Current.CancellationToken);
        Assert.Equal(["sku", "price", "minQuantity"], english.Select(s => s.Field));
    }

    [Fact]
    public async Task Suggests_barcode_from_sample_values_and_partial_headers()
    {
        var provider = new HeaderSynonymSuggestionProvider();
        var result = await provider.SuggestAsync(
            Context(ImportTypes.Products, ["Kolon A", "Ürün Adı Uzun", "Tedarikçi Stok Kodu"], [["8690000000019", "x", "y"], ["96385074", "z", "w"]]),
            TestContext.Current.CancellationToken);

        Assert.Contains(result, s => s is { Field: "barcode", Header: "Kolon A", Source: "sample_values" });
        Assert.Contains(result, s => s is { Field: "sku", Header: "Tedarikçi Stok Kodu", Source: "partial" });
        Assert.Contains(result, s => s is { Field: "name", Header: "Ürün Adı Uzun", Source: "partial" });
    }

    private static ColumnSuggestionContext Context(string type, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> samples) =>
        new(type, headers, samples, [.. ImportFields.For(type).Select(f => f.ToInfo())]);
}

using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Parsing;

namespace BayiOS.Modules.Imports.Application;

/// <summary>A target field of an import type, with the header synonyms the deterministic suggester knows.</summary>
/// <param name="RequiredForCreate">Must be mapped when the mode can create records.</param>
/// <param name="RequiredAlways">Must be mapped in every mode.</param>
internal sealed record ImportField(string Key, string Label, bool RequiredForCreate, bool RequiredAlways, IReadOnlyList<string> Synonyms)
{
    /// <summary>Synonyms folded with <see cref="ImportValues.Fold"/>.</summary>
    public IReadOnlyList<string> FoldedSynonyms { get; } = [.. Synonyms.Select(ImportValues.Fold).Distinct()];

    public ImportFieldInfo ToInfo() => new(Key, Label, RequiredForCreate, RequiredAlways);
}

internal static class ImportFields
{
    public const string Sku = "sku";
    public const string Barcode = "barcode";
    public const string Name = "name";
    public const string Description = "description";
    public const string Unit = "unit";
    public const string TaxRate = "taxRate";
    public const string Brand = "brand";
    public const string Category = "category";
    public const string Status = "status";
    public const string Price = "price";
    public const string MinQuantity = "minQuantity";

    private static readonly ImportField SkuField = new(Sku, "Stok kodu (SKU)", true, false,
        ["SKU", "Stok Kodu", "Stok Kod", "Ürün Kodu", "Urun Kodu", "Malzeme Kodu", "Kod", "Parça No", "Parca No", "Parça Numarası", "Item Code",
         "Product Code", "Stock Code", "Article", "Part Number", "Part No", "Code"]);

    private static readonly ImportField BarcodeField = new(Barcode, "Barkod", false, false,
        ["Barkod", "Barkod No", "Barkod Numarası", "Barcode", "EAN", "EAN13", "EAN-13", "EAN8", "GTIN", "UPC"]);

    private static readonly ImportField PriceField = new(Price, "Fiyat", false, false,
        ["Fiyat", "Satış Fiyatı", "Satis Fiyati", "Birim Fiyat", "Birim Fiyatı", "Liste Fiyatı", "Fiyat (TL)", "Fiyat TL", "Tutar", "Price",
         "Unit Price", "Sales Price", "List Price"]);

    /// <summary>Product import fields (PRD IMP-02 "Ürün"). No stock column exists: stock is a separate import type.</summary>
    public static readonly IReadOnlyList<ImportField> Products =
    [
        SkuField,
        BarcodeField,
        new(Name, "Ürün adı", true, false,
            ["Ürün Adı", "Urun Adi", "Ürün", "Ürün İsmi", "Stok Adı", "Stok Adi", "Malzeme Adı", "Ad", "Adı", "İsim", "Name", "Product Name", "Item Name", "Title"]),
        new(Description, "Açıklama", false, false, ["Açıklama", "Aciklama", "Detay", "Ürün Açıklaması", "Description", "Details"]),
        new(Unit, "Birim", true, false, ["Birim", "Ölçü Birimi", "Olcu Birimi", "Birim Kodu", "Unit", "UOM", "Unit of Measure"]),
        new(TaxRate, "KDV oranı (%)", true, false, ["KDV", "KDV Oranı", "KDV Orani", "KDV %", "KDV (%)", "Vergi", "Vergi Oranı", "VAT", "VAT Rate", "Tax", "Tax Rate"]),
        new(Brand, "Marka", false, false, ["Marka", "Üretici", "Uretici", "Brand", "Manufacturer", "Make"]),
        new(Category, "Kategori", false, false, ["Kategori", "Ürün Grubu", "Urun Grubu", "Grup", "Category", "Group", "Product Group"]),
        new(Status, "Durum", false, false, ["Durum", "Aktif", "Status", "Active"]),
        PriceField,
    ];

    /// <summary>Price list import fields (PRD IMP-02 "Fiyat listesi"). Never creates products.</summary>
    public static readonly IReadOnlyList<ImportField> Prices =
    [
        SkuField with { RequiredForCreate = false },
        BarcodeField,
        PriceField with { RequiredAlways = true },
        new(MinQuantity, "Minimum miktar", false, false,
            ["Min Miktar", "Minimum Miktar", "Min. Miktar", "Min Adet", "Kademe", "Miktar Kademesi", "Min Qty", "Minimum Quantity", "Min Quantity", "Tier"]),
    ];

    public static IReadOnlyList<ImportField> For(string type) => type == ImportTypes.Prices ? Prices : Products;
}

using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Catalog.Domain;

namespace BayiOS.Modules.Catalog.Application;

/// <summary>
/// Sector templates (PRD TEN-03, sector-scenarios.md §1.2 / §2.2) as code configuration: sector differences are
/// expressed only through units, categories and custom fields — never per-sector tables or code paths. Codes match
/// <c>identity.tenants.sector</c>. Option values are generic/synthetic.
/// </summary>
internal static class SectorTemplates
{
    public const string MotorcycleParts = "motorcycle_parts";
    public const string LedElectronics = "led_electronics";
    public const string General = "general";

    public static readonly IReadOnlyList<SectorTemplateResponse> All =
    [
        new(
            MotorcycleParts,
            "Motosiklet yedek parça",
            [
                new("adet", "Adet", false),
                new("takim", "Takım", false),
                new("kutu", "Kutu", false),
                new("litre", "Litre", true),
                new("metre", "Metre", true),
            ],
            ["Motor", "Debriyaj", "Fren", "Elektrik", "Aktarma (zincir-dişli)", "Süspansiyon", "Kaporta", "Filtre & Yağ", "Lastik & Jant", "Aksesuar"],
            [
                Field(CustomFieldEntityTypes.Product, "uyumlu_markalar", "Uyumlu motosiklet markaları", CustomFieldDataTypes.MultiEnum, null, ["Honda", "Yamaha", "Bajaj", "Mondial", "Kuba"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "uyumlu_model", "Uyumlu model", CustomFieldDataTypes.Text, null, [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "model_yili", "Model yılı aralığı", CustomFieldDataTypes.Range, "yıl", [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "oem_no", "OEM / orijinal parça no", CustomFieldDataTypes.Text, null, [], filterable: false),
                Field(CustomFieldEntityTypes.Product, "taraf", "Taraf / konum", CustomFieldDataTypes.Enum, null, ["Ön", "Arka", "Sağ", "Sol"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "garanti_ay", "Garanti", CustomFieldDataTypes.Integer, "ay", [], filterable: false),
                Field(CustomFieldEntityTypes.Variant, "parca_tipi", "Parça tipi", CustomFieldDataTypes.Enum, null, ["Orijinal", "Muadil", "Yan sanayi"], filterable: true),
            ]),
        new(
            LedElectronics,
            "LED / elektronik",
            [
                new("adet", "Adet", false),
                new("kutu", "Kutu", false),
                new("koli", "Koli", false),
                new("metre", "Metre", true),
            ],
            ["LED Modül", "Kabinet", "Kontrol Kartı", "Video İşlemci", "Güç Kaynağı", "Kablo & Konnektör", "Montaj Aparatı", "Yedek LED/Lamba"],
            [
                Field(CustomFieldEntityTypes.Product, "guc", "Güç", CustomFieldDataTypes.Number, "W", [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "renk_sicakligi", "Renk sıcaklığı", CustomFieldDataTypes.Integer, "K", [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "voltaj", "Voltaj", CustomFieldDataTypes.Number, "V", [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "ip_sinifi", "IP sınıfı", CustomFieldDataTypes.Enum, null, ["IP20", "IP44", "IP54", "IP65", "IP67", "IP68"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "pixel_pitch", "Pixel pitch", CustomFieldDataTypes.Number, "mm", [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "kullanim", "Kullanım", CustomFieldDataTypes.Enum, null, ["İç mekân", "Dış mekân", "Yarı dış"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "modul_olcusu", "Modül ölçüsü (mm)", CustomFieldDataTypes.Text, null, [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "parlaklik", "Parlaklık", CustomFieldDataTypes.Integer, "nit", [], filterable: true),
                Field(CustomFieldEntityTypes.Product, "tarama", "Tarama (scan)", CustomFieldDataTypes.Enum, null, ["1/8", "1/16", "1/32"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "led_tipi", "LED tipi", CustomFieldDataTypes.Enum, null, ["SMD2121", "SMD1515", "DIP"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "kontrol_karti_uyumu", "Kontrol kartı uyumu", CustomFieldDataTypes.MultiEnum, null, ["Kart ailesi A", "Kart ailesi B", "Kart ailesi C"], filterable: true),
                Field(CustomFieldEntityTypes.Product, "seri_no_takibi", "Seri no takibi", CustomFieldDataTypes.Boolean, null, [], filterable: false),
            ]),
        new(
            General,
            "Genel",
            [
                new("adet", "Adet", false),
                new("koli", "Koli", false),
                new("kg", "Kilogram", true),
            ],
            [],
            []),
    ];

    public static SectorTemplateResponse? Find(string code) => All.FirstOrDefault(t => t.Code == code);

    private static SectorTemplateField Field(
        string entityType, string key, string label, string dataType, string? unit, IReadOnlyList<string> options, bool filterable) =>
        new(entityType, key, label, dataType, unit, options, Required: false, filterable);
}

using BayiOS.SharedKernel.Entitlements;

namespace BayiOS.Modules.Billing.Domain;

/// <summary>A plan of the catalog: the features it grants and its limits (null = unlimited).</summary>
public sealed record PlanDefinition(
    string Code,
    string Name,
    string Description,
    int SortOrder,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long?> Limits);

/// <summary>
/// An add-on module. <see cref="Available"/> = false: announced in the store but not built yet (PRD SUB-02 "MVP dışı");
/// it cannot be activated.
/// </summary>
public sealed record ModuleDefinition(
    string Code,
    string Name,
    string Description,
    int SortOrder,
    bool Available,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long?> Limits);

/// <summary>
/// The versioned plan / add-on catalog (PRD SUB-01, SUB-02, SUB-05, SUB-09; roles-permissions §5). Code, not a table:
/// it ships and is reviewed with the code that enforces it, and every tenant resolves against the same version.
/// Change <see cref="Version"/> with every edit. Owner decisions 2026-11-09: Basic = 999 users, 999 warehouses,
/// unlimited products, barcode operations / mobile scanning included (OD-02); AI add-on 300 parses per month (OD-08).
/// Professional is never below Basic.
/// </summary>
public static class BillingCatalog
{
    /// <summary>Recorded on each subscription when it is created or changes.</summary>
    public const string Version = "2026-11-09.2";

    public const string Basic = "basic";
    public const string Professional = "professional";

    /// <summary>The AI WhatsApp assistant add-on (WAI).</summary>
    public const string AiAssistantModule = "ai_assistant";

    /// <summary>Basic users and active warehouses (owner decision OD-02, 2026-11-09).</summary>
    public const long BasicLimit = 999;

    public static readonly PlanDefinition BasicPlan = new(
        Basic,
        "Temel",
        "Ürün, temel stok, sipariş, barkod okutma ve mobil toplama, temel rapor. Dışa aktarma, audit, 2FA ve stok doğruluğu her pakette açıktır.",
        1,
        [EntitlementCodes.BarcodeOperations], // OD-02: barcode scanning / mobile picking in Basic
        new Dictionary<string, long?>
        {
            [EntitlementCodes.MaxWarehouses] = BasicLimit, // OD-02
            [EntitlementCodes.MaxUsers] = BasicLimit, // OD-02
            [EntitlementCodes.MaxProducts] = null, // OD-02: unlimited (no product cap)
        });

    public static readonly PlanDefinition ProfessionalPlan = new(
        Professional,
        "Profesyonel",
        "Gelişmiş depo: sınırsız depo, raf/lokasyon ve barkodlu raf işlemleri.",
        2,
        [EntitlementCodes.WarehouseLocations, EntitlementCodes.BarcodeOperations],
        new Dictionary<string, long?>
        {
            [EntitlementCodes.MaxWarehouses] = null,
            // OD-02 set Basic only; Professional users kept equal to Basic (an upgrade never lowers a limit).
            [EntitlementCodes.MaxUsers] = BasicLimit,
            [EntitlementCodes.MaxProducts] = null,
        });

    public static readonly IReadOnlyList<PlanDefinition> Plans = [BasicPlan, ProfessionalPlan];

    public static readonly IReadOnlyList<ModuleDefinition> Modules =
    [
        new(
            AiAssistantModule,
            "AI WhatsApp asistanı",
            "WhatsApp sipariş metnini ayrıştırır, katalogla eşleştirir; sipariş her zaman insan onayıyla kesinleşir.",
            1,
            true,
            [EntitlementCodes.AiOrderParsing],
            new Dictionary<string, long?> { [EntitlementCodes.AiParsesPerMonth] = 300 }), // OD-08 (2026-11-09)
        new("dealer_portal", "Bayi portalı", "Bayilerin kendi siparişlerini girdiği portal.", 2, false, [EntitlementCodes.DealerPortal], Empty),
        new("serial_warranty", "Seri/Garanti takibi", "Seri numarası ve garanti kayıtları.", 3, false, [EntitlementCodes.SerialWarranty], Empty),
        new("erp_integrations", "ERP entegrasyonları", "Muhasebe/ERP sistemleriyle senkronizasyon.", 4, false, [EntitlementCodes.ErpIntegrations], Empty),
        new("whatsapp_automation", "Resmî WhatsApp otomasyonu", "WhatsApp Business API ile otomatik mesajlaşma.", 5, false, [EntitlementCodes.WhatsappAutomation], Empty),
        new("advanced_analytics", "Gelişmiş analitik", "Temel raporların ötesinde analiz panoları.", 6, false, [EntitlementCodes.AdvancedAnalytics], Empty),
    ];

    /// <summary>The plan a new tenant tries (PRD SUB-05: "tüm modüller açık"), together with every add-on.</summary>
    public static PlanDefinition TrialPlan => ProfessionalPlan;

    /// <summary>The plan a tenant falls back to when its trial or subscription ends (SUB-05: "Temel'e iner").</summary>
    public static PlanDefinition FallbackPlan => BasicPlan;

    public static PlanDefinition? FindPlan(string? code) => Plans.FirstOrDefault(p => p.Code == code);

    public static ModuleDefinition? FindModule(string? code) => Modules.FirstOrDefault(m => m.Code == code);

    /// <summary>
    /// The cheapest catalog entry that grants <paramref name="code"/> (feature) or raises it (limit) above the
    /// fallback plan: the first plan in sort order, else the first available module.
    /// </summary>
    public static EntitlementSuggestion Suggest(string code)
    {
        var fallback = FallbackPlan.Limits.TryGetValue(code, out var floor) ? floor : 0;
        foreach (var plan in Plans.OrderBy(p => p.SortOrder))
        {
            if (plan.Features.Contains(code) || (plan.Limits.TryGetValue(code, out var value) && IsHigher(value, fallback)))
            {
                return plan.Code == FallbackPlan.Code ? EntitlementSuggestion.None : new EntitlementSuggestion(plan.Code, null);
            }
        }

        var module = Modules.Where(m => m.Available).OrderBy(m => m.SortOrder)
            .FirstOrDefault(m => m.Features.Contains(code) || m.Limits.ContainsKey(code));
        return module is null ? EntitlementSuggestion.None : new EntitlementSuggestion(null, module.Code);
    }

    /// <summary>null (unlimited) beats any number.</summary>
    public static bool IsHigher(long? candidate, long? current) =>
        current is not null && (candidate is null || candidate > current);

    private static IReadOnlyDictionary<string, long?> Empty => new Dictionary<string, long?>();
}

/**
 * Turkish labels for the plan / add-on catalog codes (services/api BillingCatalog, EntitlementCodes). Codes are the
 * API's stable keys; names shown on the plan page come from GET /billing/plans and /billing/modules when loaded — these
 * labels cover places that only have a code (403 problems, /me entitlements, gates). Unknown codes fall back to the code.
 * No imports on purpose: problems.ts uses this file.
 */

export const PLAN_PAGE_HREF = "/ayarlar/paket";

export const planLabels: Record<string, string> = {
  basic: "Temel",
  professional: "Profesyonel",
};

export const moduleLabels: Record<string, string> = {
  ai_assistant: "AI WhatsApp asistanı",
  dealer_portal: "Bayi portalı",
  serial_warranty: "Seri/Garanti takibi",
  erp_integrations: "ERP entegrasyonları",
  whatsapp_automation: "Resmî WhatsApp otomasyonu",
  advanced_analytics: "Gelişmiş analitik",
};

/** Feature flags (EntitlementCodes.Features). */
export const featureLabels: Record<string, string> = {
  warehouse_locations: "Raf/göz (lokasyon) takibi",
  barcode_operations: "Barkodlu depo işlemleri",
  ai_order_parsing: "WhatsApp siparişini AI ile ayrıştırma",
  dealer_portal: "Bayi portalı",
  serial_warranty: "Seri/Garanti takibi",
  erp_integrations: "ERP entegrasyonları",
  whatsapp_automation: "Resmî WhatsApp otomasyonu",
  advanced_analytics: "Gelişmiş analitik",
};

/** Numeric limits (EntitlementCodes.Limits). */
export const limitLabels: Record<string, string> = {
  max_warehouses: "Aktif depo sayısı",
  max_users: "Kullanıcı sayısı",
  ai_parses_per_month: "Aylık AI ayrıştırma",
};

export const planLabel = (code: string | null | undefined): string => (code ? (planLabels[code] ?? code) : "—");
export const moduleLabel = (code: string | null | undefined): string => (code ? (moduleLabels[code] ?? code) : "—");
export const featureLabel = (code: string | null | undefined): string => (code ? (featureLabels[code] ?? limitLabels[code] ?? code) : "Bu özellik");
export const limitLabel = (code: string | null | undefined): string => (code ? (limitLabels[code] ?? featureLabels[code] ?? code) : "Paket sınırı");

export type SubscriptionStatus = "trialing" | "active" | "expired" | "cancelled";

export const subscriptionStatusLabels: Record<SubscriptionStatus, string> = {
  trialing: "Ücretsiz deneme",
  active: "Etkin",
  expired: "Süresi doldu",
  cancelled: "İptal edildi",
};

export type ModuleStatus = "included_in_trial" | "trialing" | "active" | "expired" | "cancelled" | "available" | "unavailable";

export const moduleStatusLabels: Record<ModuleStatus, string> = {
  included_in_trial: "Denemeye dahil",
  trialing: "Ücretsiz deneme",
  active: "Etkin",
  expired: "Süresi doldu",
  cancelled: "Kapatıldı",
  available: "Etkinleştirilebilir",
  unavailable: "Yakında",
};

/**
 * Where a gated feature / raised limit lives — the same answer the API's catalog gives in 403 `suggestedPlan` /
 * `suggestedModule` (BillingCatalog.Suggest). Used only for the locked state shown *before* a request; a 403 always
 * carries the API's own suggestion, which wins.
 */
export const SUGGESTIONS: Record<string, { plan: string | null; module: string | null }> = {
  warehouse_locations: { plan: "professional", module: null },
  barcode_operations: { plan: "professional", module: null },
  max_warehouses: { plan: "professional", module: null },
  max_users: { plan: "professional", module: null },
  ai_order_parsing: { plan: null, module: "ai_assistant" },
  ai_parses_per_month: { plan: null, module: "ai_assistant" },
};

/** "Profesyonel pakette" / "AI WhatsApp asistanı ek modülünde"; null when nothing in the catalog grants it. */
export function whereAvailable(suggestedPlan: string | null | undefined, suggestedModule: string | null | undefined): string | null {
  if (suggestedPlan) return `${planLabel(suggestedPlan)} pakette`;
  if (suggestedModule) return `${moduleLabel(suggestedModule)} ek modülünde`;
  return null;
}

import { CONDITIONS, type Access } from "@/lib/session/access";

/** The reports the API has (api-strategy §12.13): URL slug on the web ↔ API path segment. */
export type ReportId = "critical-stock" | "orders" | "top-products" | "stock-movements" | "picking-tasks" | "ai-usage";

export type ReportDefinition = {
  id: ReportId;
  /** /raporlar/<slug>. */
  slug: string;
  title: string;
  description: string;
  /** Takes a date range (every report but critical stock). */
  periodic: boolean;
  /** An order report (sales sees only these — roles-permissions footnote 4). */
  orderReport: boolean;
  /** Read and export need billing.read instead of reports.read / reports.export (owner, admin). */
  billing: boolean;
};

export const REPORTS: readonly ReportDefinition[] = [
  {
    id: "orders",
    slug: "siparisler",
    title: "Sipariş durumları",
    description: "Seçilen günlerde oluşturulan siparişlerin güncel durumlarına göre adet ve tutarları.",
    periodic: true,
    orderReport: true,
    billing: false,
  },
  {
    id: "top-products",
    slug: "en-cok-siparis-edilenler",
    title: "En çok sipariş edilen ürünler",
    description: "Onaylanmış ve sonrası siparişlerde en çok istenen ürünler: miktar ve sipariş sayısı.",
    periodic: true,
    orderReport: true,
    billing: false,
  },
  {
    id: "critical-stock",
    slug: "kritik-stok",
    title: "Kritik stok",
    description: "Satılabilir miktarı belirlediğiniz eşiğin altında veya eşit olan aktif ürünler.",
    periodic: false,
    orderReport: false,
    billing: false,
  },
  {
    id: "stock-movements",
    slug: "stok-hareketleri",
    title: "Stok hareketleri",
    description: "Gün ve hareket tipine göre stok girişleri ve çıkışları (maliyet içermez).",
    periodic: true,
    orderReport: false,
    billing: false,
  },
  {
    id: "picking-tasks",
    slug: "toplama-gorevleri",
    title: "Toplama görevleri",
    description: "Oluşturulan, tamamlanan, eksikle tamamlanan ve iptal edilen toplama görevleri, ortalama toplama süresi.",
    periodic: true,
    orderReport: false,
    billing: false,
  },
  {
    id: "ai-usage",
    slug: "ai-kullanimi",
    title: "AI asistan kullanımı",
    description: "WhatsApp sipariş asistanının gün bazında ayrıştırma, AI çağrısı, hata ve token sayıları.",
    periodic: true,
    orderReport: false,
    billing: true,
  },
];

export const REPORTS_HREF = "/raporlar";

export function reportDefinition(id: ReportId): ReportDefinition {
  const found = REPORTS.find((r) => r.id === id);
  if (!found) throw new Error(`Unknown report: ${id}`);
  return found;
}

export const reportHref = (id: ReportId) => `${REPORTS_HREF}/${reportDefinition(id).slug}`;

export type ReportAccess = { read: boolean; export: boolean };

/**
 * What the caller may do with a report — visibility only (the API checks again). sales holds reports.read/export under
 * footnote 4 (`reports.orders_and_customers_only`): order reports only. The AI usage report is billing.read (owner/admin).
 */
export function reportAccess(access: Access, report: ReportDefinition): ReportAccess {
  if (report.billing) {
    const billing = access.can("billing.read");
    return { read: billing, export: billing };
  }
  const allowed = (permission: "reports.read" | "reports.export") => {
    if (access.can(permission)) return true;
    if (!access.canSome(permission)) return false;
    // A conditional grant: only the order-report condition is known; anything else is left to the API (shown).
    return !access.hasCondition(permission, CONDITIONS.reportsOrdersOnly) || report.orderReport;
  };
  return { read: allowed("reports.read"), export: allowed("reports.export") };
}

/** The reports the caller may open, in hub order. */
export function visibleReports(access: Access): ReportDefinition[] {
  return REPORTS.filter((r) => reportAccess(access, r).read);
}

/** True when the caller sees only the order reports (sales). */
export function ordersOnly(access: Access): boolean {
  return !access.can("reports.read") && access.hasCondition("reports.read", CONDITIONS.reportsOrdersOnly);
}

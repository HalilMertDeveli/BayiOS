/**
 * RFC 9457 Problem Details → Turkish, user-facing messages.
 * The API's stable `code` is the localisation key (api-strategy §6); `title`/`detail` are developer aids
 * in English and are never shown to users.
 */
import { featureLabel, limitLabel, whereAvailable } from "@/lib/billing/labels";
export type ProblemDetails = {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  instance?: string | null;
  code?: string | null;
  correlationId?: string | null;
  errors?: Record<string, string[]> | null;
  /** Extension members (RFC 9457 §3.2), e.g. inventory `shortages` / `confirmationRequired`. */
  [extension: string]: unknown;
};

const MESSAGES: Record<string, string> = {
  "auth.invalid_token": "Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.",
  "auth.forbidden": "Bu işlem için yetkiniz yok. Gerekirse işletme yöneticinizle görüşün.",
  "tenant.access_denied": "Bu işletmeye erişiminiz yok. Lütfen erişebildiğiniz bir işletme seçin.",
  "tenant.inactive": "Bu işletme askıya alınmış veya kapatılmış; işlem yapılamıyor. Başka bir işletme seçin veya işletme sahibiyle görüşün.",
  "tenant.slug_taken": "Bu işletme adresi başka bir işletme tarafından kullanılıyor. Lütfen farklı bir adres girin.",
  "validation.failed": "Bazı alanlar geçersiz. Lütfen işaretli alanları düzeltip tekrar deneyin.",
  "request.malformed": "İstek işlenemedi. Sayfayı yenileyip tekrar deneyin.",
  "resource.not_found": "Aradığınız kayıt bulunamadı.",
  "idempotency.in_progress": "İsteğiniz hâlâ işleniyor. Birkaç saniye sonra tekrar deneyin.",
  "idempotency.key_reused": "Bu işlem daha önce farklı bilgilerle gönderilmiş. Sayfayı yenileyip tekrar deneyin.",
  "concurrency.version_mismatch": "Kayıt siz düzenlerken değişti. Sayfayı yenileyip tekrar deneyin.",
  "precondition.required": "Kayıt sürümü doğrulanamadı. Sayfayı yenileyip tekrar deneyin.",
  "entitlement.required": "Bu özellik mevcut paketinizde bulunmuyor.",
  "entitlement.limit_exceeded": "Paketinizin sınırına ulaştınız.",
  "rate_limit.exceeded": "Çok sayıda istek gönderildi. Lütfen biraz bekleyip tekrar deneyin.",
  "reports.orders_and_customers_only": "Rolünüz yalnızca sipariş raporlarını (sipariş durumları, en çok sipariş edilen ürünler) görebilir.",
  "reports.export_too_large": "Dosya 20.000 satır sınırını aşıyor. Tarih aralığını veya filtreleri daraltıp tekrar deneyin.",
};

/** Where plan details live, for the entitlement messages (the page itself is for owner/admin). */
const PLAN_HINT = "Ayrıntılar Ayarlar → Paket ve modüller sayfasında (işletme sahibi ve yöneticiler). Mevcut verileriniz silinmez, görüntülenmeye devam eder.";

const count = (n: number) => n.toLocaleString("tr-TR");

/**
 * 403 `entitlement.required` / `entitlement.limit_exceeded` (#51) → a Turkish sentence naming the feature or limit and,
 * when the catalog has one, the plan or add-on that grants it (`suggestedPlan` / `suggestedModule`).
 */
export function entitlementMessage(problem: ProblemDetails | null | undefined): string | null {
  const code = problem?.code;
  if (code !== "entitlement.required" && code !== "entitlement.limit_exceeded") return null;
  const feature = problemString(problem, "feature");
  const where = whereAvailable(problemString(problem, "suggestedPlan"), problemString(problem, "suggestedModule"));
  if (code === "entitlement.required") {
    const head = `${featureLabel(feature)} mevcut paketinizde yok.`;
    return [head, where ? `${upperFirst(where)} bulunur.` : null, PLAN_HINT].filter(Boolean).join(" ");
  }
  const limit = problemNumber(problem, "limit");
  const current = problemNumber(problem, "current");
  const numbers = limit !== null && current !== null ? ` (${count(current)}/${count(limit)})` : "";
  const head = `${limitLabel(feature)} sınırına ulaşıldı${numbers}.`;
  return [head, where ? `Daha yüksek sınır ${where}.` : null, PLAN_HINT].filter(Boolean).join(" ");
}

const upperFirst = (text: string) => text.charAt(0).toLocaleUpperCase("tr-TR") + text.slice(1);

const BY_STATUS: Record<number, string> = {
  400: MESSAGES["request.malformed"],
  401: MESSAGES["auth.invalid_token"],
  403: MESSAGES["auth.forbidden"],
  404: MESSAGES["resource.not_found"],
  409: "İşlem mevcut durumla çakışıyor. Sayfayı yenileyip tekrar deneyin.",
  412: MESSAGES["concurrency.version_mismatch"],
  422: MESSAGES["validation.failed"],
  428: MESSAGES["precondition.required"],
  429: MESSAGES["rate_limit.exceeded"],
};

export const NETWORK_ERROR_MESSAGE = "Sunucuya ulaşılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.";
export const SERVER_ERROR_MESSAGE = "Beklenmeyen bir hata oluştu. Lütfen biraz sonra tekrar deneyin.";

/**
 * 403 `auth.disabled_by_tenant_rule` (#111): the role holds the permission conditionally and the business switched the
 * rule off. The `permission` extension says which one; an unknown permission gets the generic text.
 */
const DISABLED_BY_RULE: Record<string, string> = {
  "orders.approve": "Satış personelinin sipariş onaylaması işletme ayarlarında kapalı. Siparişi onay için yöneticinize iletin.",
  "orders.price.override":
    "İşletmenizde satış personeli için iskonto sınırı %0; iskonto ve manuel fiyat giremezsiniz. Fiyatları fiyat listesinden bırakın.",
  "catalog.barcode.assign": "Depo personelinin barkod ataması işletme ayarlarında kapalı. Barkodu bir yöneticiye atatın.",
  "inventory.receive.approve": "Depo personelinin mal kabulünü onaylaması işletme ayarlarında kapalı. Onay için yöneticinize başvurun.",
};
export const DISABLED_BY_RULE_MESSAGE = "Bu işlem işletmenizin ayarlarında rolünüz için kapatılmış. Gerekirse işletme yöneticinizle görüşün.";

/** A string extension member of a problem (e.g. `permission`), or null. */
export function problemString(problem: ProblemDetails | null | undefined, name: string): string | null {
  const value = problem?.[name];
  return typeof value === "string" && value ? value : null;
}

/** A numeric extension member (e.g. `limitPercent`; the API writes decimals as JSON numbers), or null. */
export function problemNumber(problem: ProblemDetails | null | undefined, name: string): number | null {
  const value = problem?.[name];
  const n = typeof value === "number" ? value : typeof value === "string" && value.trim() ? Number(value) : NaN;
  return Number.isFinite(n) ? n : null;
}

/** Turkish message for a failed response: by `code` first, then by HTTP status. */
export function problemMessage(status: number, problem?: ProblemDetails | null): string {
  const code = problem?.code ?? undefined;
  if (code === "auth.disabled_by_tenant_rule") {
    const permission = problemString(problem, "permission");
    return (permission && DISABLED_BY_RULE[permission]) || DISABLED_BY_RULE_MESSAGE;
  }
  const entitlement = entitlementMessage(problem);
  if (entitlement) return entitlement;
  if (code && MESSAGES[code]) return MESSAGES[code];
  if (status >= 500) return SERVER_ERROR_MESSAGE;
  return BY_STATUS[status] ?? SERVER_ERROR_MESSAGE;
}

/** Reads a problem body if the response carries one; never throws. */
export async function readProblem(response: Response): Promise<ProblemDetails | null> {
  const type = response.headers.get("Content-Type") ?? "";
  if (!type.includes("json")) return null;
  try {
    const body: unknown = await response.json();
    return body && typeof body === "object" ? (body as ProblemDetails) : null;
  } catch {
    return null;
  }
}

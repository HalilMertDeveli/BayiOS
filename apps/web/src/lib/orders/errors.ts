import { ApiError } from "@/lib/api/client";
import { problemNumber, problemString } from "@/lib/api/problems";
import { translateFieldMessage } from "@/lib/catalog/errors";
import { readShortages, type Shortage } from "@/lib/inventory/errors";
import { formatPercent } from "@/lib/pricing/money";
import { orderStatusLabel } from "./labels";

export { readShortages, type Shortage };

/** Orders validation messages (English developer text from the API) → Turkish. */
const KNOWN: [RegExp, string][] = [
  [/^Customer not found\.$/, "Müşteri bulunamadı. Başka bir müşteri seçin."],
  [/^Customer is not active\.$/, "Bu müşteri aktif değil; pasif veya arşivlenmiş müşteriye sipariş verilemez."],
  [/^Warehouse not found\.$/, "Depo bulunamadı. Sayfayı yenileyin."],
  [/^Warehouse is inactive\.$/, "Bu depo pasif. Aktif bir depo seçin."],
  [/^Cannot be cleared\.$/, "Bu alan boş bırakılamaz."],
  [/^An order needs at least one line\.$/, "Onaya göndermek için en az bir satır ekleyin."],
  [/^At most (\d+) lines\.$/, "Satır sayısı sınırı aşıldı."],
  [/^Must be greater than 0 with at most 3 decimals\.$/, "0'dan büyük, en fazla 3 ondalıklı bir miktar girin."],
  [/^Must be 0 or more with at most 4 decimals\.$/, "0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin."],
  [/^Must be between 0 and 100 with at most 2 decimals\.$/, "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin."],
  [/^Must be 0 or more with at most 2 decimals\.$/, "0 veya daha büyük, en fazla 2 ondalıklı bir tutar girin."],
  [/^Give either discountPercent or discountAmount, not both\.$/, "İskontoyu ya oran ya tutar olarak girin, ikisini birden değil."],
  [/^Line not found in this order/, "Bu satır siparişte artık yok. Sayfayı yeniden yükleyin."],
  [/^Variant not found\.$/, "Ürün bulunamadı. Satırı silip ürünü yeniden ekleyin."],
  [/^Variant is not active\.$/, "Bu ürün aktif değil; siparişe eklenemez."],
  [/^The variant's unit does not allow fractions\.$/, "Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin."],
  [/^No price for /, "Bu satırın fiyatı yok."],
  [/^The line amount is too large or the discount exceeds the line subtotal\.$/, "Satır tutarı çok büyük ya da iskonto satır tutarını aşıyor."],
  [/^The order total is too large\.$/, "Sipariş toplamı izin verilen sınırı aşıyor."],
  [/^At most (\d+) characters\.$/, "Metin çok uzun; kısaltın."],
  [/^Not a shipping address of this customer\.$/, "Bu adres seçili müşterinin teslimat adresi değil. Listeden bir adres seçin."],
  [/^Not a billing address of this customer\.$/, "Bu adres seçili müşterinin fatura adresi değil. Listeden bir adres seçin."],
  [/^The chosen address no longer exists/, "Seçili adres müşteriden silinmiş. Başka bir adres seçip taslağı kaydedin."],
];

export function translateOrderFieldMessage(message: string | undefined): string {
  if (message) {
    for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  }
  return translateFieldMessage(message);
}

/** First message per field, translated; keys as the API sent them (e.g. `lines[0].quantity`). */
export function orderFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateOrderFieldMessage(messages[0]);
  return result;
}

/** Orders problem codes → Turkish ("what happened + what to do"). */
export const ORDER_MESSAGES: Record<string, string> = {
  "order.invalid_transition": "Bu işlem siparişin şu anki durumunda yapılamaz; sipariş siz bakarken değişmiş olabilir. Güncel hâli yüklendi.",
  "order.not_editable": "Yalnızca taslak siparişler düzenlenebilir; sipariş artık taslak değil. Güncel hâli yüklendi.",
  "order.busy": "Sipariş şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "order.price_missing":
    "Fiyatı olmayan satırlar var. Fiyat listesine fiyat ekleyin veya yetkili bir kullanıcıdan manuel fiyat girmesini isteyin.",
  "inventory.insufficient_stock": "Stok yetersiz: siparişteki hiçbir satır için stok ayrılmadı (ya hep ya hiç).",
  "inventory.busy": "Stok kayıtları şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
};

/** Shown when `order.busy` / `inventory.busy` persists after the single automatic retry. */
export const ORDER_BUSY_FINAL_MESSAGE =
  "Sipariş hâlâ başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra yeniden deneyin; aynı işlem gönderilir, çift kayıt oluşmaz.";

/** 409 `order.busy` or `inventory.busy` (lock timeout / deadlock; the API sends Retry-After: 1). */
export function isOrderBusy(error: unknown): boolean {
  return error instanceof ApiError && (error.code === "order.busy" || error.code === "inventory.busy");
}

/** The view is stale: the order moved to another status (or is no longer a draft). Reload it. */
export function isStaleState(error: unknown): boolean {
  return error instanceof ApiError && (error.code === "order.invalid_transition" || error.code === "order.not_editable");
}

/** 412 / concurrency.version_mismatch: someone else changed the order; reload before trying again. */
export function isOrderVersionConflict(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 412 || error.code === "concurrency.version_mismatch");
}

/** 409 `inventory.insufficient_stock` on approve: nothing was reserved (all or nothing). */
export function isInsufficientStock(error: unknown): boolean {
  return error instanceof ApiError && error.code === "inventory.insufficient_stock";
}

export function isPriceMissing(error: unknown): boolean {
  return error instanceof ApiError && error.code === "order.price_missing";
}

/** Line numbers (1-based) the API reported without a price (`lines[i].unitPrice` keys of order.price_missing). */
export function unpricedLineIndexes(error: unknown): number[] {
  if (!(error instanceof ApiError)) return [];
  const result: number[] = [];
  for (const key of Object.keys(error.fieldErrors)) {
    const match = /^lines\[(\d+)\]\.unitPrice$/.exec(key);
    if (match) result.push(Number(match[1]));
  }
  return result.sort((a, b) => a - b);
}

/**
 * Submit answered with an order that is already approved: the tenant does not require approval and the submitter may
 * approve (#92), so the API reserved the stock in the same transaction.
 */
export function isAutoApproved(statusAfterSubmit: string): boolean {
  return statusAfterSubmit !== "pending_approval" && statusAfterSubmit !== "draft";
}

/** 422 on submit/save naming the shipping / billing address (removed meanwhile, or not the customer's): Turkish text, else null. */
export function addressErrorText(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.status !== 422) return null;
  const messages = ["shippingAddressId", "billingAddressId"]
    .filter((key) => error.fieldErrors[key]?.length)
    .map((key) => `${key === "shippingAddressId" ? "Teslimat adresi" : "Fatura adresi"}: ${translateOrderFieldMessage(error.fieldErrors[key][0])}`);
  return messages.length > 0 ? messages.join(" ") : null;
}

/** `%12,5` for a percentage from a problem extension (Turkish decimal comma). */
const percentText = (value: number | null): string | null => (value === null ? null : formatPercent(value));

/**
 * Orders' dedicated 403 codes (#111), chosen by `code` and their extension members — never by the problem title, which
 * is a developer aid. Returns null for other errors (the plain `auth.forbidden` keeps the generic message).
 */
export function recordForbiddenMessage(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.status !== 403) return null;
  const problem = error.problem;
  switch (error.code) {
    case "orders.discount_limit_exceeded": {
      const limit = percentText(problemNumber(problem, "limitPercent"));
      return limit
        ? `İskonto en fazla ${limit} olabilir. Daha düşük bir iskonto girin veya daha yüksek iskonto için yöneticinizden onay isteyin.`
        : "İskonto, işletmenizin satış personeli için belirlediği sınırı aşıyor. Daha düşük bir iskonto girin veya yöneticinizden onay isteyin.";
    }
    case "orders.manual_price_not_allowed": {
      const limit = percentText(problemNumber(problem, "limitPercent"));
      return `Manuel birim fiyat giremezsiniz; yalnızca ${limit ? `en fazla ${limit}` : "iskonto sınırınız içinde"} iskonto uygulayabilirsiniz. Birim fiyatı boş bırakın.`;
    }
    case "orders.not_owner":
      return problemString(problem, "permission") === "orders.cancel"
        ? "Yalnızca kendi oluşturduğunuz siparişleri iptal edebilirsiniz. Bu sipariş için yöneticinize başvurun."
        : "Bu taslağı yalnızca oluşturan kişi veya yetkili bir yönetici düzenleyebilir.";
    case "orders.cancel_not_allowed_in_status": {
      const status = problemString(problem, "orderStatus");
      const where = status ? `; bu sipariş “${orderStatusLabel(status)}” durumunda` : "";
      return `Kendi siparişlerinizi yalnızca Taslak veya Onay bekliyor durumundayken iptal edebilirsiniz${where}. İptal için yöneticinize başvurun.`;
    }
    default:
      return null;
  }
}

/** User-facing message for any failed orders call. */
export function orderErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && ORDER_MESSAGES[error.code]) return ORDER_MESSAGES[error.code];
    const forbidden = recordForbiddenMessage(error);
    if (forbidden) return forbidden;
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

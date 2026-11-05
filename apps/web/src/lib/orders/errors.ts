import { ApiError } from "@/lib/api/client";
import { translateFieldMessage } from "@/lib/catalog/errors";
import { readShortages, type Shortage } from "@/lib/inventory/errors";

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

const PRICE_OVERRIDE_MESSAGE = "Manuel fiyat ve iskonto girme yetkiniz yok. Fiyatları fiyat listesinden bırakın veya yetkili bir kullanıcıya başvurun.";

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

/** Tenant policy `warn`: the approve can be repeated with `confirmNegativeStock: true` as a new request. */
export function needsStockConfirmation(error: unknown): boolean {
  return isInsufficientStock(error) && (error as ApiError).problem?.confirmationRequired === true;
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

/** User-facing message for any failed orders call. */
export function orderErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && ORDER_MESSAGES[error.code]) return ORDER_MESSAGES[error.code];
    if (error.status === 403 && /orders\.price\.override/.test(error.problem?.title ?? "")) return PRICE_OVERRIDE_MESSAGE;
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

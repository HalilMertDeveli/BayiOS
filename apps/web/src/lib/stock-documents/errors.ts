import { ApiError } from "@/lib/api/client";
import { INVENTORY_MESSAGES, translateInventoryFieldMessage } from "@/lib/inventory/errors";

/** Transfer / stock count / shelf move problem codes → Turkish ("what happened + what to do"). */
export const STOCK_DOCUMENT_MESSAGES: Record<string, string> = {
  "transfer.invalid_transition": "Bu işlem transferin şu anki durumunda yapılamaz; transfer siz bakarken değişmiş olabilir. Güncel hâli yüklendi.",
  "transfer.over_receipt":
    "Yolda olandan fazlası teslim alınamaz. Miktarı kontrol edin; fazla gelen ürünü ayrıca mal kabul olarak kaydedin.",
  "transfer.unknown_code": "Bu barkod/SKU hiçbir ürünle eşleşmiyor. Etiketi kontrol edip tekrar okutun.",
  "transfer.wrong_item": "Okutulan ürün bu transferde yok. Ürünü ayırın ve transferdeki ürünleri okutun.",
  "transfer.unknown_location": "Bu raf kodu hedef depoda yok. Hedef depodaki bir rafı seçin veya okutun.",
  "transfer.warehouse_inactive": "Kaynak veya hedef depo pasif. Depoyu yeniden aktif edin ya da transferi iptal edin.",
  "transfer.busy": "Transfer şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "count.invalid_transition": "Bu işlem sayımın şu anki durumunda yapılamaz; sayım siz bakarken değişmiş olabilir. Güncel hâli yüklendi.",
  "count.already_active": "Bu depoda zaten açık bir sayım var. Önce onu tamamlayın veya iptal edin.",
  "count.below_reserved":
    "Sayılan miktar, siparişler için ayrılmış (rezerve) miktarın altında kalıyor. Önce ilgili siparişlerin rezervasyonunu azaltın, sonra tekrar onaylayın.",
  "count.lines_moved": "Bazı satırların stoğu sayımdan sonra hareket gördü. Satırları kontrol edip yine de onaylayabilirsiniz.",
  "count.nothing_counted": "Henüz hiçbir satır sayılmadı. En az bir satırı sayın.",
  "count.out_of_scope": "Bu ürün veya raf sayımın kapsamında değil.",
  "count.scope_too_large": "Kapsam çok büyük (en fazla 5.000 satır). Sayımı raflara veya ürünlere bölün.",
  "count.unknown_code": "Bu barkod/SKU hiçbir ürünle eşleşmiyor. Etiketi kontrol edip tekrar okutun.",
  "count.unknown_location": "Bu raf kodu bu depoda yok. Raf etiketini kontrol edin.",
  "count.busy": "Sayım şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "inventory.insufficient_available":
    "Kaynakta satılabilir stok yetersiz. Siparişler için ayrılmış (rezerve) stok taşınamaz; miktarı azaltın.",
  "inventory.insufficient_stock": "Kaynakta yeterli stok yok; stok asla eksiye düşürülmez. Miktarı azaltın.",
};

/** Service validation messages (English developer text) → Turkish; anything else falls back to the inventory table. */
const KNOWN: [RegExp, string][] = [
  [/^Source and destination warehouses must differ\.$/, "Kaynak ve hedef depo farklı olmalı. Aynı depoda raf değiştirmek için raf taşıma kullanın."],
  [/^Each \(variant, source location\) may appear only once\.$/, "Aynı ürün ve kaynak raf bir kez eklenebilir; miktarları tek satırda toplayın."],
  [/^Give exactly one of lineId, variantId or code\.$/, "Satırı veya ürünü belirtin."],
  [/^Line not found on this (transfer|count)\.$/, "Bu satır artık belgede yok. Sayfayı yenileyin."],
  [/^A destination location is required in this warehouse\.$/, "Hedef depo raf/göz takibi kullanıyor: teslim alınan rafı seçin veya okutun."],
  [/^Must be one of: lost, return_to_source\.$/, "Kalan miktar için kayıp veya kaynağa iade seçin."],
  [/^Required, at most (\d+) characters\.$/, "Sebep yazın (en fazla 500 karakter)."],
  [/^Must be ≥ 0 with at most 3 decimals\.$/, "0 veya daha büyük, en fazla 3 ondalıklı bir miktar girin."],
  [/^Must be set or add\.$/, "Giriş türünü seçin."],
  [/^Between 1 and (\d+) ids are required for this scope\.$/, "Bu kapsam için en az bir seçim yapın."],
  [/^Not allowed for this scope\.$/, "Bu kapsamda seçim yapılmaz."],
  [/^Every location must belong to this warehouse\.$/, "Seçili raflar bu depoya ait olmalı."],
  [/^Every line must belong to this count\.$/, "Seçili satırlar bu sayıma ait değil. Sayfayı yenileyin."],
  [/^Between 1 and (\d+) line ids are required\.$/, "Yeniden saydırmak için en az bir satır seçin."],
  [/^Between 1 and (\d+) entries are required\.$/, "En az bir sayım girişi yapın."],
  [/^The counted quantity is too large\.$/, "Sayılan miktar çok büyük."],
  [/^Source and target must differ\.$/, "Kaynak ve hedef raf farklı olmalı."],
  [/^Only (.+) of (.+) can still be received\.$/, "Yolda kalan miktardan fazlası teslim alınamaz."],
  [/^Required\.$/, "Bu alan zorunlu."],
];

export function translateDocumentFieldMessage(message: string | undefined): string {
  if (message) for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  return translateInventoryFieldMessage(message);
}

/** First message per field, translated; keys as the API sent them (e.g. `lines[0].quantity`). */
export function documentFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateDocumentFieldMessage(messages[0]);
  return result;
}

const BUSY = new Set(["transfer.busy", "count.busy", "inventory.busy"]);

/** 409 busy (Retry-After: 1) → one automatic retry with the same Idempotency-Key. */
export function isDocumentBusy(error: unknown): boolean {
  return error instanceof ApiError && error.code !== null && BUSY.has(error.code);
}

/** 412 / version mismatch, or an action the current state no longer allows: reload the document. */
export function isStaleDocument(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    (error.status === 412 ||
      error.code === "concurrency.version_mismatch" ||
      error.code === "transfer.invalid_transition" ||
      error.code === "count.invalid_transition")
  );
}

/** 409 count.lines_moved: the ids (keys of `errors`) of counted lines whose stock moved after their snapshot. */
export function movedLineIds(error: unknown): string[] {
  if (!(error instanceof ApiError) || error.code !== "count.lines_moved") return [];
  return Object.keys(error.fieldErrors);
}

export const BUSY_FINAL_MESSAGE =
  "Kayıt hâlâ başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra yeniden deneyin; aynı işlem gönderilir, çift kayıt oluşmaz.";

/** User-facing message for any failed transfer / count / shelf-move call. */
export function documentErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && STOCK_DOCUMENT_MESSAGES[error.code]) return STOCK_DOCUMENT_MESSAGES[error.code];
    if (error.code && INVENTORY_MESSAGES[error.code]) return INVENTORY_MESSAGES[error.code];
    if (error.code === "validation.failed") {
      const first = Object.values(error.fieldErrors)[0]?.[0];
      if (first) return translateDocumentFieldMessage(first);
    }
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

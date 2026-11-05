import { ApiError } from "@/lib/api/client";
import { translateInventoryFieldMessage } from "@/lib/inventory/errors";

/** Warehouse fulfilment problem codes → Turkish ("what happened + what to do"). */
export const FULFILMENT_MESSAGES: Record<string, string> = {
  "picking.already_assigned": "Bu görevi az önce başka biri aldı. Liste yenilendi; havuzdan başka bir görev seçin.",
  "picking.not_assignee": "Bu görev size atanmamış. Havuzdan üstlenin veya yöneticinizden atama isteyin.",
  "picking.invalid_transition": "Bu işlem görevin şu anki durumunda yapılamaz; görev siz bakarken değişmiş olabilir. Güncel hâli yüklendi.",
  "picking.order_not_fulfillable": "Sipariş artık toplanabilir durumda değil (iptal edilmiş veya kapanmış olabilir). Yöneticinize danışın.",
  "picking.busy": "Görev şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "order.busy": "Sipariş şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "inventory.busy": "Stok kayıtları şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "picking.unknown_code": "Bu barkod/SKU hiçbir ürünle eşleşmiyor. Etiketi kontrol edip tekrar okutun.",
  "picking.wrong_item": "Yanlış ürün: okutulan ürün bu görevde yok. Ürünü rafına bırakın ve listedeki ürünü okutun.",
  "picking.location_required": "Önce raf (lokasyon) barkodunu okutun, sonra ürünü.",
  "picking.unknown_location": "Bu raf barkodu bu depoda yok. Raf etiketini kontrol edin.",
  "picking.wrong_location": "Yanlış raf: bu ürün okutulan raftan toplanmıyor. Listede gösterilen rafa gidin.",
  "picking.over_pick": "Fazla toplama: bu satırda istenenden fazla ürün okuttunuz. Fazlasını rafa geri koyun.",
  "picking.line_closed": "Bu satırda toplanacak ürün kalmadı.",
  "picking.shortage_exceeds_open": "Eksik miktarı, satırda açık kalan miktardan fazla olamaz.",
  "picking.incomplete": "Tüm satırlar toplanmadan veya eksik bildirilmeden görev tamamlanamaz.",
  "picking.nothing_to_pick": "Bu siparişte toplanacak açık ürün yok.",
  "picking.fulfillment_in_progress": "Siparişin zaten aktif bir toplama görevi veya onaylanmamış sevkiyatı var.",
  "shipment.invalid_transition": "Bu işlem sevkiyatın şu anki durumunda yapılamaz. Güncel hâli yüklendi.",
  "shipment.already_confirmed": "Bu sevkiyat zaten onaylanmış; stok yalnızca bir kez düşüldü.",
  "shipment.empty": "Bu sevkiyatta gönderilecek ürün kalmadı.",
  "packing.unknown_code": "Bu barkod/SKU hiçbir ürünle eşleşmiyor.",
  "packing.wrong_item": "Okutulan ürün bu sevkiyatta yok.",
  "packing.over_pack": "Paketlenen miktar toplanandan fazla. Fazla ürünü koliden çıkarın.",
  "packing.quantity_mismatch": "Paketlenen miktarlar toplananla aynı değil. Eksik ürünleri önce eksik olarak bildirin.",
};

const KNOWN: [RegExp, string][] = [
  [/^Line not found in this task\.$/, "Bu satır görevde artık yok. Sayfayı yenileyin."],
  [/^The item's unit does not allow fractions\.$/, "Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin."],
  [/^Required\.$/, "Bu alan zorunlu."],
];

export function translateFulfilmentFieldMessage(message: string | undefined): string {
  if (message) for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  return translateInventoryFieldMessage(message);
}

const BUSY = new Set(["picking.busy", "order.busy", "inventory.busy"]);

/** 409 busy (task/shipment, order or stock rows locked; Retry-After: 1) → one automatic retry with the same key. */
export function isFulfilmentBusy(error: unknown): boolean {
  return error instanceof ApiError && error.code !== null && BUSY.has(error.code);
}

export function isVersionConflict(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 412 || error.code === "concurrency.version_mismatch");
}

/** The view is stale: reload the task/shipment and show the current state. */
export function isStaleFulfilment(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    (error.code === "picking.invalid_transition" || error.code === "shipment.invalid_transition" || error.code === "picking.line_closed")
  );
}

/** Scan rejections: nothing changed on the server; the worker fixes the scan and goes on. */
export const SCAN_REJECTIONS = [
  "picking.unknown_code",
  "picking.wrong_item",
  "picking.location_required",
  "picking.unknown_location",
  "picking.wrong_location",
  "picking.over_pick",
] as const;

export function isScanRejection(error: unknown): boolean {
  return error instanceof ApiError && (SCAN_REJECTIONS as readonly string[]).includes(error.code ?? "");
}

/** Shown when a busy error persists after the single automatic retry. */
export const BUSY_FINAL_MESSAGE =
  "Kayıt hâlâ başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra yeniden deneyin; aynı işlem gönderilir, çift kayıt oluşmaz.";

/** Pack-check mismatches (`errors` keyed by shipment line id, English per line) → affected line ids. */
export function mismatchedLineIds(error: unknown): string[] {
  if (!(error instanceof ApiError)) return [];
  if (error.code !== "packing.over_pack" && error.code !== "packing.quantity_mismatch") return [];
  return Object.keys(error.fieldErrors);
}

/** User-facing message for any failed fulfilment call. */
export function fulfilmentErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && FULFILMENT_MESSAGES[error.code]) return FULFILMENT_MESSAGES[error.code];
    if (error.code === "validation.failed") {
      const first = Object.values(error.fieldErrors)[0]?.[0];
      if (first) return translateFulfilmentFieldMessage(first);
    }
    if (error.status === 404) return "Kayıt bulunamadı veya size atanmamış. Görev listesine dönün.";
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

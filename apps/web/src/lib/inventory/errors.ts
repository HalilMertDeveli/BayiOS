import { ApiError } from "@/lib/api/client";
import { translateFieldMessage } from "@/lib/catalog/errors";

/** Inventory/Warehouse validation messages (English developer text from the API) → Turkish. */
const KNOWN: [RegExp, string][] = [
  [/^Warehouse not found\.$/, "Depo bulunamadı. Sayfayı yenileyin."],
  [/^Warehouse is inactive\.$/, "Bu depo pasif. Aktif bir depo seçin."],
  [/^This warehouse does not use locations\.$/, "Bu depo raf/göz takibi kullanmıyor; lokasyon seçmeyin."],
  [/^A location is required in this warehouse\.$/, "Bu depoda raf/göz (lokasyon) seçmek zorunlu."],
  [/^Location not found in this warehouse\.$/, "Lokasyon bu depoda bulunamadı."],
  [/^Location is inactive\.$/, "Bu lokasyon pasif. Başka bir lokasyon seçin."],
  [/^Variant not found\.$/, "Ürün bulunamadı. Satırı silip ürünü yeniden okutun."],
  [/^Variant is not active\.$/, "Bu ürün aktif değil; stok girişi yapılamaz."],
  [/^The variant's unit does not allow fractions\.$/, "Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin."],
  [/^Between 1 and (\d+) lines are required\.$/, "En az bir satır ekleyin (en fazla 500)."],
  [/^Must be greater than 0 with at most 3 decimals\.$/, "0'dan büyük, en fazla 3 ondalıklı bir miktar girin."],
  [/^Must be ≥ 0 with at most 4 decimals\.$/, "0 veya daha büyük, en fazla 4 ondalıklı bir değer girin."],
  [/^A reason of 1–(\d+) characters is required\.$/, "Düzeltme sebebini yazın (en fazla 500 karakter)."],
  [/^Code must be 1–(\d+) characters without whitespace\.$/, "Boşluk içermeyen bir kod girin."],
  [/^Must be between 0 and 1000000\.$/, "0 ile 1.000.000 arasında bir sayı girin."],
  [/^Only true is accepted/, "Varsayılan depo kaldırılamaz; bunun yerine başka bir depoyu varsayılan yapın."],
];

export function translateInventoryFieldMessage(message: string | undefined): string {
  if (message) {
    for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  }
  return translateFieldMessage(message);
}

/** First message per field, translated; keys as the API sent them (e.g. `lines[0].quantity`). */
export function inventoryFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateInventoryFieldMessage(messages[0]);
  return result;
}

/** Inventory/Warehouse problem codes → Turkish ("what happened + what to do"). */
export const INVENTORY_MESSAGES: Record<string, string> = {
  "warehouse.default_inactive": "Varsayılan depo pasif yapılamaz. Önce başka bir depoyu varsayılan yapın.",
  "warehouse.has_stock":
    "Depoda stok varken raf/göz takibi açılıp kapatılamaz. Bu ayar yalnızca depo boşken değiştirilebilir.",
  "warehouse.code_taken": "Bu depo kodu zaten kullanılıyor. Farklı bir kod girin.",
  "warehouse.default_conflict": "Varsayılan depo aynı anda başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.",
  "warehouse.location_code_taken": "Bu lokasyon kodu bu depoda zaten var. Farklı bir kod girin.",
  "warehouse.location_barcode_taken": "Bu lokasyon barkodu başka bir lokasyonda kullanılıyor.",
  "warehouse.locations_disabled": "Bu depo raf/göz takibi kullanmıyor. Önce depoda raf/göz takibini açın.",
  "inventory.insufficient_available":
    "Satılabilir stok yetersiz. Siparişler için ayrılmış (rezerve) stok düzeltmeyle düşülemez.",
  "inventory.insufficient_stock": "Stok yetersiz: işlem negatif stok kuralına takıldı.",
  "inventory.search_too_broad": "Arama çok fazla ürünle eşleşiyor. Aramayı daraltın: daha uzun bir SKU, barkod veya ürün adı yazın.",
  "inventory.busy": "Stok kayıtları şu an başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin.",
  "inventory.duplicate_source": "Bu belge satırı stoğa zaten işlenmiş.",
};

/** Shown when `inventory.busy` persists after the single automatic retry. */
export const BUSY_FINAL_MESSAGE =
  "Stok kayıtları hâlâ başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra yeniden kaydedin; aynı işlem gönderilir, çift kayıt oluşmaz.";

export type Shortage = {
  variantId: string;
  sku: string | null;
  warehouseId: string;
  locationId: string | null;
  requested: number;
  available: number;
};

/** The `shortages` extension of an `inventory.insufficient_*` problem; malformed entries are skipped. */
export function readShortages(error: unknown): Shortage[] {
  if (!(error instanceof ApiError)) return [];
  const raw = error.problem?.shortages;
  if (!Array.isArray(raw)) return [];
  const result: Shortage[] = [];
  for (const item of raw) {
    if (!item || typeof item !== "object") continue;
    const s = item as Record<string, unknown>;
    if (typeof s.variantId !== "string" || typeof s.warehouseId !== "string") continue;
    result.push({
      variantId: s.variantId,
      sku: typeof s.sku === "string" ? s.sku : null,
      warehouseId: s.warehouseId,
      locationId: typeof s.locationId === "string" ? s.locationId : null,
      requested: Number(s.requested),
      available: Number(s.available),
    });
  }
  return result;
}

/**
 * Tenant policy `warn`: the API answers 409 `inventory.insufficient_stock` with `confirmationRequired: true`;
 * the same request repeated with `confirmNegativeStock: true` goes through.
 */
export function needsNegativeStockConfirmation(error: unknown): boolean {
  return error instanceof ApiError && error.code === "inventory.insufficient_stock" && error.problem?.confirmationRequired === true;
}

export function isBusy(error: unknown): boolean {
  return error instanceof ApiError && error.code === "inventory.busy";
}

/** User-facing message for any failed inventory/warehouse call. */
export function inventoryErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && INVENTORY_MESSAGES[error.code]) return INVENTORY_MESSAGES[error.code];
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

/** 422 `inventory.search_too_broad`: the balance search matches too many products. */
export function isSearchTooBroad(error: unknown): boolean {
  return error instanceof ApiError && error.code === "inventory.search_too_broad";
}

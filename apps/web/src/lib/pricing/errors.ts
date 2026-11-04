import { ApiError } from "@/lib/api/client";
import { translateFieldMessage } from "@/lib/catalog/errors";

/** Pricing validation messages (English developer text from the API) → Turkish. */
const KNOWN: [RegExp, string][] = [
  [/^Must be a 3-letter ISO 4217 code\.$/, "Geçerli bir para birimi seçin (ör. TRY)."],
  [/^Only sales lists can be the default\.$/, "Yalnızca satış listeleri varsayılan olabilir."],
  [/^Only sales lists can derive from a base list\.$/, "Yalnızca satış listeleri bir taban listeden türetilebilir."],
  [/^Only true is accepted/, "Varsayılan liste kaldırılamaz; bunun yerine başka bir listeyi varsayılan yapın."],
  [/^Must not be before validFrom\.$/, "Bitiş tarihi başlangıç tarihinden önce olamaz."],
  [/^basePriceListId and discountPercent are set together\.$/, "Taban liste ve iskonto oranı birlikte girilmeli."],
  [/^Must be between 0 and 100 with at most 2 decimals\.$/, "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin."],
  [/^Must be another sales list that is not archived\.$/, "Taban liste, arşivlenmemiş başka bir satış listesi olmalı."],
  [/^Must contain 1–(\d+) items\.$/, "En az bir satır girin (tek seferde en fazla 500)."],
  [/^Must contain 1–(\d+) lines\.$/, "En az bir ürün satırı ekleyin (en fazla 500)."],
  [/^Must be greater than 0 with at most 3 decimals\.$/, "0'dan büyük, en fazla 3 ondalıklı bir miktar girin."],
  [/^Required: 0 or more with at most 4 decimals\.$/, "0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin."],
  [/^Duplicate variantId \+ minQuantity in this request\.$/, "Aynı ürün ve en az miktar bu listede iki kez var."],
  [/^Variant not found\.$/, "Ürün bulunamadı. Satırı silip ürünü yeniden seçin."],
  [/^Customer not found\.$/, "Müşteri bulunamadı."],
];

export function translatePricingFieldMessage(message: string | undefined): string {
  if (message) {
    for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  }
  return translateFieldMessage(message);
}

/** First message per field, translated; keys as the API sent them (e.g. `items[3].unitPrice`). */
export function pricingFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translatePricingFieldMessage(messages[0]);
  return result;
}

/** Pricing problem codes (409) → Turkish ("what happened + what to do"). */
export const PRICING_MESSAGES: Record<string, string> = {
  "pricing.name_taken": "Bu adla bir fiyat listesi zaten var. Farklı bir ad girin.",
  "pricing.default_conflict": "Varsayılan liste aynı anda başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.",
  "pricing.default_inactive": "Varsayılan liste pasif yapılamaz veya arşivlenemez. Önce başka bir listeyi varsayılan yapın.",
  "pricing.base_chain": "Taban liste tek seviyelidir: türetilmiş bir liste taban olamaz, taban olarak kullanılan bir liste de türetilemez.",
  "pricing.list_in_use": "Bu listeden türetilmiş listeler var. Önce onları arşivleyin.",
  "pricing.tax_mode_mismatch": "Türetilmiş liste, taban listeyle aynı KDV modunu kullanmalı.",
  "pricing.currency_mismatch": "Taban liste başka bir para biriminde. Aynı para birimindeki bir liste seçin.",
  "pricing.list_archived": "Arşivlenmiş fiyat listesi değiştirilemez.",
  "pricing.item_conflict": "Aynı kalem aynı anda başka biri tarafından yazıldı. Sayfayı yenileyip tekrar deneyin.",
};

/** User-facing message for any failed pricing call. */
export function pricingErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && PRICING_MESSAGES[error.code]) return PRICING_MESSAGES[error.code];
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

export type RowErrors = Record<number, { variantId?: string; minQuantity?: string; unitPrice?: string; row?: string }>;

/**
 * Splits `items[i].field` / `items[i]` errors of a bulk upsert onto the submitted rows; anything else (e.g. `items`)
 * is returned as a general message. `rowIndex` maps the request index back to the editor's row.
 */
export function itemRowErrors(errors: Record<string, string>, rowIndex: (requestIndex: number) => number): { rows: RowErrors; general: string[] } {
  const rows: RowErrors = {};
  const general: string[] = [];
  for (const [key, message] of Object.entries(errors)) {
    const match = /^items\[(\d+)\](?:\.(variantId|minQuantity|unitPrice))?$/.exec(key);
    if (!match) {
      general.push(message);
      continue;
    }
    const row = rowIndex(Number(match[1]));
    const field = (match[2] ?? "row") as "variantId" | "minQuantity" | "unitPrice" | "row";
    rows[row] = { ...rows[row], [field]: message };
  }
  return { rows, general };
}

import { ApiError } from "@/lib/api/client";

/**
 * The API's per-field validation messages are English developer text (api-strategy §6). They are never shown
 * as-is: known shapes are translated, anything else falls back to a generic Turkish message per field.
 */
const KNOWN: [RegExp, string | ((m: RegExpMatchArray) => string)][] = [
  [/^Required\.$/, "Bu alan zorunlu."],
  [/^Cannot be null\.$/, "Bu alan boş bırakılamaz."],
  [/^Must be 1–(\d+) characters\.$/, (m) => `1–${m[1]} karakter girin.`],
  [/^Must be at most (\d+) characters\.$/, (m) => `En fazla ${m[1]} karakter girin.`],
  [/^Must be 1–(\d+) characters without whitespace\.$/, (m) => `Boşluk içermeyen 1–${m[1]} karakter girin.`],
  [/^Must be a percentage between 0 and 100/, "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin."],
  [/^Must be active or inactive/, "Durum aktif veya pasif olmalı."],
  [/^Must be a string of 1–(\d+) characters\.$/, (m) => `1–${m[1]} karakter girin.`],
  [/^Must be a date/, "Geçerli bir tarih girin."],
  [/^Must be true or false\.$/, "Evet veya Hayır seçin."],
  [/^Must be a number\.$/, "Geçerli bir sayı girin."],
  [/^Must be an integer\.$/, "Tam sayı girin."],
  [/^Must be one of: (.*)\.$/, (m) => `Şunlardan biri olmalı: ${m[1]}.`],
  [/^Must be a non-empty array of distinct values/, "Listeden en az bir seçenek seçin."],
  [/^Must be an object \{"min"/, "En az ve en çok değerlerini girin; en az, en çoktan büyük olamaz."],
  [/^Unknown custom field/, "Bu özel alan tanımlı değil. Sayfayı yenileyin."],
  [/^Custom field is archived\.$/, "Bu özel alan arşivlenmiş. Sayfayı yenileyin."],
  [/^Category not found\.$/, "Kategori bulunamadı veya arşivlenmiş."],
  [/^Brand not found\.$/, "Marka bulunamadı veya arşivlenmiş."],
  [/^Unit not found\.$/, "Birim bulunamadı veya arşivlenmiş."],
  [/^SKUs must be unique within the request\.$/, "Varyantların SKU'ları birbirinden farklı olmalı."],
  [/^Barcodes must be unique within the request\.$/, "Varyantların barkodları birbirinden farklı olmalı."],
  [/^Must contain 1–(\d+) variants\.$/, (m) => `1–${m[1]} varyant ekleyin.`],
  [/^Must match \^\[a-z\]/, "Küçük harf ile başlayın; yalnızca küçük harf (a–z), rakam ve alt çizgi kullanın (en fazla 50)."],
  [/^Only number, integer and range fields have a unit/, "Birim yalnızca sayı, tam sayı ve aralık alanlarında girilir (1–20 karakter)."],
  [/^enum and multi_enum need/, "Seçim alanları için birbirinden farklı, 1–100 karakterlik seçenekler girin."],
  [/^Only enum and multi_enum fields have options\.$/, "Seçenekler yalnızca seçim alanlarında girilir."],
  [/^Must be between 0 and 10000\.$/, "0 ile 10000 arasında bir sayı girin."],
];

export function translateFieldMessage(message: string | undefined): string {
  if (!message) return "Bu alanı kontrol edin.";
  for (const [pattern, text] of KNOWN) {
    const match = message.match(pattern);
    if (match) return typeof text === "string" ? text : text(match);
  }
  return "Bu alanı kontrol edin.";
}

/** First message per field, translated. Keys stay exactly as the API sent them (e.g. `variants[0].attributes.guc`). */
export function serverFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateFieldMessage(messages[0]);
  return result;
}

/**
 * Splits errors into those shown on a rendered input and the rest (shown in the form summary).
 * `knownKeys` are the error keys of the inputs currently on screen.
 */
export function partitionErrors(
  errors: Record<string, string>,
  knownKeys: ReadonlySet<string>,
): { onFields: Record<string, string>; unmatched: string[] } {
  const onFields: Record<string, string> = {};
  const unmatched: string[] = [];
  for (const [key, message] of Object.entries(errors)) {
    if (knownKeys.has(key)) onFields[key] = message;
    else unmatched.push(message);
  }
  return { onFields, unmatched };
}

/** Catalog conflict codes (409) → Turkish. */
export const CATALOG_CONFLICTS: Record<string, string> = {
  "catalog.sku_taken": "Bu SKU başka bir üründe kullanılıyor. Farklı bir SKU girin.",
  "catalog.barcode_taken": "Bu barkod başka bir üründe kullanılıyor. Farklı bir barkod girin.",
  "catalog.last_variant": "Ürünün en az bir aktif varyantı olmalı. Bunun yerine ürünü arşivleyin.",
  "catalog.product_archived": "Arşivlenmiş ürüne varyant eklenemez. Önce ürünü geri alın.",
  "catalog.field_key_taken": "Bu anahtar başka bir özel alanda kullanılıyor. Farklı bir anahtar girin.",
  "catalog.field_limit_reached": "Aktif özel alan sınırına ulaşıldı. Kullanmadığınız bir alanı arşivleyin.",
  "catalog.brand_name_taken": "Bu marka adı zaten kayıtlı.",
};

/** True for the optimistic-concurrency failure (412 / concurrency.version_mismatch). */
export function isVersionConflict(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 412 || error.code === "concurrency.version_mismatch");
}

/** User-facing message for any failed catalog call. */
export function catalogErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && CATALOG_CONFLICTS[error.code]) return CATALOG_CONFLICTS[error.code];
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

/** Turns a form error key into a DOM id (`variants[0].attributes.guc` → `f-variants-0-attributes-guc`). */
export function fieldId(key: string): string {
  return `f-${key.replace(/[^a-zA-Z0-9_-]+/g, "-").replace(/-+$/, "")}`;
}

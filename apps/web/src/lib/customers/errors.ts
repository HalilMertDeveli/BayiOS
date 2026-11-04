import { ApiError } from "@/lib/api/client";
import { translateFieldMessage } from "@/lib/catalog/errors";

/** Customers validation messages (English developer text from the API) → Turkish. */
const KNOWN: [RegExp, string][] = [
  [/^Required: 1–30 characters without whitespace\.$/, "Boşluk içermeyen, en fazla 30 karakterlik bir müşteri kodu girin."],
  [/^Required: 1–200 characters\.$/, "Bu alan zorunlu (en fazla 200 karakter)."],
  [/^Required: 1–100 characters\.$/, "Bu alan zorunlu (en fazla 100 karakter)."],
  [/^Must be 10 \(VKN\) or 11 \(TCKN\) digits\.$/, "Vergi numarası 10 (VKN) veya 11 (TCKN) haneli olmalı."],
  [/^Must be 7–20 characters: digits, spaces, dashes/, "Geçerli bir telefon girin (7–20 karakter; rakam, boşluk, tire, parantez, başta +)."],
  [/^Must be a valid e-mail address\.$/, "Geçerli bir e-posta adresi girin."],
  [/^Each number must be E\.164/, "WhatsApp numaralarını ülke koduyla girin, ör. +905321234567."],
  [/^At most (\d+) numbers\.$/, "En fazla 5 WhatsApp numarası girilebilir."],
  [/^Must be between 0 and 3650\.$/, "Vade 0 ile 3650 gün arasında olmalı."],
  [/^Must be 0 or more with at most 4 decimals\.$/, "0 veya daha büyük, en fazla 4 ondalıklı bir tutar girin."],
  [/^Must be a 3-letter ISO 4217 code\.$/, "Geçerli bir para birimi seçin (ör. TRY)."],
  [/^Dealer group not found or archived\.$/, "Bayi grubu bulunamadı veya arşivlenmiş."],
  [/^Must be a sales price list that is not archived\.$/, "Arşivlenmemiş bir satış fiyat listesi seçin."],
  [/^Must be an ISO 3166-1 alpha-2 code/, "Ülkeyi iki harfli kodla girin (ör. TR)."],
  [/^Only true is accepted/, "Varsayılan adres kaldırılamaz; bunun yerine başka bir adresi varsayılan yapın."],
  [/^Must be between 0 and 100 with at most 2 decimals\.$/, "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin."],
];

export function translateCustomerFieldMessage(message: string | undefined): string {
  if (message) {
    for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  }
  return translateFieldMessage(message);
}

/** First message per field, translated; keys as the API sent them. */
export function customerFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateCustomerFieldMessage(messages[0]);
  return result;
}

/** Customers problem codes (409, 403 on assignment) → Turkish. */
export const CUSTOMER_MESSAGES: Record<string, string> = {
  "customers.code_taken": "Bu müşteri kodu zaten kullanılıyor. Farklı bir kod girin.",
  "customers.archived": "Arşivlenmiş müşteri değiştirilemez. Önce müşteriyi arşivden çıkarın.",
  "customers.anonymized": "Bu müşteri anonimleştirilmiş; kayıt artık değiştirilemez.",
  "customers.address_limit": "Bir müşteriye en fazla 50 adres eklenebilir. Kullanılmayan bir adresi silin.",
  "customers.default_address": "Varsayılan adresin türü değiştirilemez. Önce bu türde başka bir adresi varsayılan yapın.",
  "customers.default_address_conflict": "Varsayılan adres aynı anda başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.",
  "customers.group_name_taken": "Bu adla bir bayi grubu zaten var. Farklı bir ad girin.",
};

/** User-facing message for any failed customers call. */
export function customerErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && CUSTOMER_MESSAGES[error.code]) return CUSTOMER_MESSAGES[error.code];
    return error.userMessage;
  }
  return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
}

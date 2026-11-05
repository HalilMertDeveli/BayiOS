import { ApiError } from "@/lib/api/client";
import { problemString } from "@/lib/api/problems";
import { orderErrorMessage, translateOrderFieldMessage } from "@/lib/orders/errors";

/** Default wait when a 429 comes without a readable Retry-After. */
export const DEFAULT_RETRY_AFTER_SECONDS = 60;

/** "3 dakika" / "45 saniye" for a Retry-After value. */
export function waitText(seconds: number): string {
  if (seconds < 60) return `${Math.max(1, Math.round(seconds))} saniye`;
  return `${Math.ceil(seconds / 60)} dakika`;
}

/** 429 `ai.rate_limited`: the tenant's hourly parse limit is reached. */
export function isRateLimited(error: unknown): error is ApiError {
  return error instanceof ApiError && (error.code === "ai.rate_limited" || error.status === 429);
}

export function rateLimitMessage(error: ApiError): string {
  const seconds = error.retryAfterSeconds ?? DEFAULT_RETRY_AFTER_SECONDS;
  return `İşletmeniz bu saat için WhatsApp mesajı ayrıştırma sınırına ulaştı. ${waitText(seconds)} sonra tekrar deneyin; mesajınız bu sayfada duruyor.`;
}

/** Submit `text` validation (422) → Turkish. The limits come from the API's message when present. */
export function translateTextError(message: string | undefined): string {
  if (!message) return "Mesaj geçersiz.";
  if (/^Required\.?$/.test(message)) return "WhatsApp mesajını yapıştırın.";
  const chars = /^At most (\d+) characters\.$/.exec(message);
  if (chars) return `Mesaj en fazla ${Number(chars[1]).toLocaleString("tr-TR")} karakter olabilir; mesajı bölerek gönderin.`;
  const lines = /^At most (\d+) lines\.$/.exec(message);
  if (lines) return `Mesaj en fazla ${lines[1]} satır olabilir; mesajı bölerek gönderin.`;
  return translateOrderFieldMessage(message);
}

/** Approve 422 messages (English developer text) → Turkish; falls back to the Orders translations (passed through). */
const APPROVE_KNOWN: [RegExp, string][] = [
  [/^Choose the product variant for this line\.$/, "Bu satır için ürünü seçin."],
  [/^Required: a positive quantity with at most 3 decimals\.$/, "0'dan büyük, en fazla 3 ondalıklı bir miktar girin."],
  [/^Unknown review line\.$/, "Satır bu ayrıştırmada yok; sayfayı yenileyin."],
  [/^Listed more than once\.$/, "Satır birden fazla gönderildi; sayfayı yenileyin."],
  [/^Required \(true or false\)\.$/, "Satırın siparişe eklenip eklenmeyeceği belirtilmedi; sayfayı yenileyin."],
  [/^Every review line must be listed/, "Tüm satırlar gönderilmedi; sayfayı yenileyin."],
  [/^Include at least one line/, "En az bir satırı siparişe ekleyin ya da ayrıştırmayı reddedin."],
  [/^Required \(not given on submit either\)\.$/, "Müşteri seçin."],
];

export function translateApproveFieldMessage(message: string | undefined): string {
  if (message) for (const [pattern, text] of APPROVE_KNOWN) if (pattern.test(message)) return text;
  return translateOrderFieldMessage(message);
}

export function approveFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateApproveFieldMessage(messages[0]);
  return result;
}

/** The parse was approved/rejected meanwhile, or its text was purged: the screen reloads it. */
export function isNotReviewable(error: unknown): boolean {
  return error instanceof ApiError && (error.code === "ai.parse_not_reviewable" || error.code === "ai.parse_text_purged");
}

export function isParseVersionConflict(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 412 || error.code === "concurrency.version_mismatch");
}

/** User-facing message for any failed assistant call (by problem `code`, never by title). */
export function assistantErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
  switch (error.code) {
    case "ai.rate_limited":
      return rateLimitMessage(error);
    case "ai.parse_not_reviewable":
      return "Bu ayrıştırma siz incelerken başka biri tarafından tamamlandı veya reddedildi. Güncel hâli yüklendi.";
    case "ai.parse_text_purged":
      return "Mesaj metni saklama süresi dolduğu için silindi; bu ayrıştırmadan taslak oluşturulamaz. Mesajı yeniden yapıştırıp ayrıştırın.";
    case "concurrency.version_mismatch":
      return "Ayrıştırma siz incelerken değişti. Güncel hâli yüklendi; seçimlerinizi kontrol edip tekrar deneyin.";
    case "auth.forbidden": {
      const permission = problemString(error.problem, "permission");
      if (permission === "orders.create") return "Taslak sipariş oluşturma yetkiniz yok. Ayrıştırmayı yetkili bir kullanıcıya iletin.";
      if (permission === "whatsapp.parse") return "WhatsApp siparişi ayrıştırma yetkiniz yok. Gerekirse işletme yöneticinizle görüşün.";
      return error.userMessage;
    }
  }
  if (error.status === 429) return rateLimitMessage(error);
  return orderErrorMessage(error);
}

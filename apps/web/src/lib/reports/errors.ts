import { ApiError } from "@/lib/api/client";
import { waitText } from "@/lib/assistant/errors";
import { translateFieldMessage } from "@/lib/catalog/errors";
import { formatMoney, type Decimal } from "@/lib/pricing/money";

/** Exports per hour per tenant (threat-model §6.5) — for the wording only; the API enforces it. */
export const EXPORTS_PER_HOUR = 10;
export const MAX_EXPORT_ROWS = 20_000;

/** Wait shown when a 429 arrives without a readable Retry-After. */
const DEFAULT_EXPORT_WAIT_SECONDS = 15 * 60;

/** Report validation messages (English developer text from the API) → Turkish. */
const KNOWN: [RegExp, string][] = [
  [/^from must not be after to$/, "Başlangıç tarihi bitiş tarihinden sonra olamaz."],
  [/^The range may cover at most (\d+) days$/, "Tarih aralığı en fazla 366 gün olabilir."],
  [/^format must be csv or xlsx$/, "Dosya biçimi CSV veya XLSX olmalı."],
  [/is not a filter of this report$/, "Bu rapor seçilen filtreyi desteklemiyor. Sayfayı yenileyip tekrar deneyin."],
  [/^Unknown status/i, "Geçersiz sipariş durumu seçildi."],
  [/threshold/i, "Eşik 0 veya daha büyük bir sayı olmalı."],
];

function firstFieldMessage(error: ApiError): string | null {
  for (const messages of Object.values(error.fieldErrors)) {
    const message = messages[0];
    if (!message) continue;
    for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
    return translateFieldMessage(message);
  }
  return null;
}

/** User-facing message for a failed report read. */
export function reportErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.";
  if (error.status === 422 && error.code !== "reports.export_too_large") return firstFieldMessage(error) ?? error.userMessage;
  if (error.code === "warehouse.not_found" || (error.status === 404 && error.code === "resource.not_found")) {
    return "Seçilen depo bulunamadı. Depo filtresini temizleyip tekrar deneyin.";
  }
  return error.userMessage;
}

/** User-facing message for a failed export, incl. the hourly limit (429 + Retry-After) and the row limit (422). */
export function exportErrorMessage(error: unknown): string {
  if (error instanceof ApiError && error.status === 429) {
    const wait = waitText(error.retryAfterSeconds ?? DEFAULT_EXPORT_WAIT_SECONDS);
    return `Saatlik dışa aktarma sınırına ulaşıldı (işletme başına saatte ${EXPORTS_PER_HOUR} dosya). ${wait} sonra tekrar deneyin.`;
  }
  if (error instanceof ApiError && error.code === "reports.export_too_large") {
    return `Dosya ${MAX_EXPORT_ROWS.toLocaleString("tr-TR")} satır sınırını aşıyor. Tarih aralığını veya filtreleri daraltıp tekrar deneyin.`;
  }
  return reportErrorMessage(error);
}

/**
 * Money is shown only when the API says the caller may see sell-side amounts (`amountsVisible`, unconditional
 * prices.read.sell); the API then sends null amounts anyway. Returns the formatted amount, "—" for a missing amount, or
 * null when amounts are hidden (the column is not rendered at all).
 */
export function visibleMoney(amountsVisible: boolean, value: Decimal | null | undefined, currency: string): string | null {
  if (!amountsVisible) return null;
  return formatMoney(value, currency) ?? "—";
}

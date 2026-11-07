import { ApiError } from "@/lib/api/client";
import { SERVER_ERROR_MESSAGE } from "@/lib/api/problems";

/** Imports problem codes (409 / 413 / 415 / 422 / 429) and job failure codes → Turkish ("what happened + what to do"). */
export const IMPORT_MESSAGES: Record<string, string> = {
  "imports.file_too_large": "Dosya 10 MB sınırını aşıyor. Dosyayı daha küçük parçalara bölüp ayrı ayrı yükleyin.",
  "imports.file_type_not_allowed": "Bu dosya türü kabul edilmiyor. Yalnızca .xlsx veya .csv yükleyin; eski .xls dosyasını Excel'de .xlsx olarak kaydedin.",
  "imports.file_macro_not_allowed": "Makro içeren Excel dosyaları (.xlsm) kabul edilmez. Dosyayı Excel'de .xlsx olarak kaydedip tekrar yükleyin.",
  "imports.file_content_mismatch": "Dosyanın içeriği uzantısıyla uyuşmuyor. Dosyayı Excel'de .xlsx veya .csv olarak yeniden kaydedip tekrar yükleyin.",
  "imports.file_archive_limits": "Dosya güvenlik sınırlarını aşıyor (çok büyük veya olağan dışı sıkıştırılmış). Dosyayı Excel'de yeniden kaydedip tekrar deneyin.",
  "imports.file_xml_dtd_not_allowed": "Dosya güvenlik nedeniyle reddedildi. Excel'de açıp .xlsx olarak yeniden kaydedin.",
  "imports.file_empty": "Dosyada başlık satırı veya veri satırı bulunamadı. İlk satırda kolon başlıkları, altında ürünler olmalı.",
  "imports.file_too_many_rows": "Dosyada 10.000'den fazla satır var. Dosyayı parçalara bölüp ayrı ayrı yükleyin.",
  "imports.file_too_many_columns": "Dosyada 100'den fazla kolon var. Kullanılmayan kolonları silip tekrar yükleyin.",
  "imports.file_cell_too_long": "Bir hücre 4.000 karakter sınırını aşıyor. Uzun metni kısaltıp tekrar yükleyin.",
  "imports.file_unreadable": "Dosya okunamadı; bozuk olabilir. Excel'de açıp yeniden kaydedin ve tekrar yükleyin.",
  "imports.file_parse_timeout": "Dosya zamanında okunamadı. Dosyayı daha küçük parçalara bölüp tekrar deneyin.",
  "imports.file_missing": "Yüklenen dosyaya artık ulaşılamıyor. Dosyayı yeniden yükleyerek yeni bir aktarma başlatın.",
  "imports.file_changed": "Kayıtlı dosya başlıklarla artık uyuşmuyor. Dosyayı yeniden yükleyerek yeni bir aktarma başlatın.",
  "imports.file_purged":
    "Yüklenen dosya saklama süresi (30 gün) dolduğu için silindi. Dosyayı yeniden yükleyerek yeni bir aktarma başlatın.",
  "imports.invalid_state": "Aktarmanın durumu siz bakarken değişti; bu işlem şu an yapılamıyor. Güncel durum gösteriliyor.",
  "imports.already_rolled_back": "Bu aktarma zaten geri alındı veya şu an geri alınıyor. Geri alma yalnızca bir kez yapılabilir.",
  "imports.rollback_window_expired": "Geri alma süresi (tamamlandıktan sonra 7 gün) doldu; bu aktarma artık geri alınamaz.",
  "imports.template_name_taken": "Bu adla bir şablon zaten var. Farklı bir ad girin.",
  "imports.actor_not_authorized": "İşlemi başlatan kullanıcının yetkisi değiştiği için işlem durduruldu. Yetkili bir kullanıcı devam ettirebilir.",
  "imports.stalled": "Aktarma yanıt vermediği için durduruldu. “Kaldığı yerden devam et” ile sürdürebilirsiniz.",
  "imports.rollback_stalled": "Geri alma yanıt vermediği için durdu. “Geri almayı sürdür” ile kaldığı yerden devam ettirebilirsiniz.",
  "pricing.list_archived": "Seçilen fiyat listesi arşivlenmiş; arşivlenmiş listeye fiyat yazılamaz. Başka bir liste seçin.",
};

/** "3 dakika", "45 saniye", "1 saat 5 dakika". */
export function formatWait(seconds: number): string {
  const s = Math.max(1, Math.ceil(seconds));
  if (s < 60) return `${s} saniye`;
  const minutes = Math.ceil(s / 60);
  if (minutes < 60) return `${minutes} dakika`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `${hours} saat ${rest} dakika` : `${hours} saat`;
}

/** 429 on upload (10 files per hour per business): wait time from Retry-After. */
export function rateLimitMessage(retryAfterSeconds: number | null): string {
  const base = "Bu işletme için saatlik dosya yükleme sınırına ulaşıldı.";
  return retryAfterSeconds && retryAfterSeconds > 0
    ? `${base} Yaklaşık ${formatWait(retryAfterSeconds)} sonra tekrar deneyin.`
    : `${base} Biraz bekleyip tekrar deneyin.`;
}

/** User-facing message for any failed imports call. */
export function importErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 429) return rateLimitMessage(error.retryAfterSeconds);
    if (error.code && IMPORT_MESSAGES[error.code]) return IMPORT_MESSAGES[error.code];
    if (error.status === 413) return IMPORT_MESSAGES["imports.file_too_large"];
    if (error.status === 415) return IMPORT_MESSAGES["imports.file_type_not_allowed"];
    return error.userMessage;
  }
  return SERVER_ERROR_MESSAGE;
}

/** Message for a job's `failure` (validation or run stopped). */
export function failureMessage(failure: { code: string; message: string } | null): string | null {
  if (!failure) return null;
  return IMPORT_MESSAGES[failure.code] ?? "İşlem tamamlanamadı. Kaldığı yerden devam ettirmeyi deneyin; sorun sürerse destek ile iletişime geçin.";
}

/** Mapping 422 field messages (English developer text) → Turkish; keys stay as the API sent them (`columns.sku`, `priceListId`). */
const FIELD_MESSAGES: [RegExp, string][] = [
  [/^Required for this import type and mode\.$/, "Bu alan bu tür ve modda zorunlu; bir kolonla eşleyin."],
  [/^Must be one of the file's headers\.$/, "Dosyada olmayan bir kolon seçildi."],
  [/^A header can be mapped to one field only\.$/, "Bir kolon yalnızca bir alana eşlenebilir."],
  [/^Unknown or archived custom field\.$/, "Özel alan bulunamadı veya arşivlenmiş."],
  [/^Custom field columns are only allowed in product imports\.$/, "Özel alanlar yalnızca ürün aktarmada eşlenebilir."],
  [/^Unknown field\./, "Bilinmeyen alan."],
  [/^Map at least the match key column\.$/, "En az eşleşme anahtarı kolonunu (SKU veya barkod) eşleyin."],
  [/^Required for price imports\.$/, "Fiyat listesi seçin."],
  [/^Required when the price column is mapped\.$/, "Fiyat kolonu eşlendiğinde fiyat listesi seçilmeli."],
  [/^Only allowed when the price column is mapped\.$/, "Fiyat listesi yalnızca fiyat kolonu eşlendiğinde seçilebilir."],
  [/^Price list not found\.$/, "Fiyat listesi bulunamadı."],
  [/^Must be 1–100 characters\.$/, "1–100 karakter olmalı."],
];

export function translateImportField(message: string | undefined): string {
  if (message) for (const [pattern, text] of FIELD_MESSAGES) if (pattern.test(message)) return text;
  return "Geçersiz değer.";
}

export function mappingFieldErrors(error: ApiError): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = translateImportField(messages[0]);
  return result;
}

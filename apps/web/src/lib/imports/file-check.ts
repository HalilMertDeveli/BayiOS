/**
 * Client-side pre-check before an upload (fast feedback only — the server is authoritative: it checks content and
 * magic bytes, macros, archive limits and the 10,000-row limit, ImportLimits / TabularFileReader).
 */
export const MAX_FILE_BYTES = 20 * 1024 * 1024;
export const MAX_ROWS = 10_000;
export const ACCEPTED_EXTENSIONS = [".xlsx", ".csv"] as const;
/** `accept` for the file input: extensions plus the usual MIME types. */
export const FILE_ACCEPT = ".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv";

export function extensionOf(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot < 0 ? "" : name.slice(dot).toLowerCase();
}

/** Null when the file may be sent; otherwise a Turkish "what happened + what to do" message. */
export function preCheckFile(file: Pick<File, "name" | "size">): string | null {
  const ext = extensionOf(file.name);
  if (ext === ".xlsm") return "Makro içeren Excel dosyaları (.xlsm) kabul edilmez. Dosyayı Excel'de “.xlsx” olarak kaydedip tekrar seçin.";
  if (ext === ".xls") return "Eski Excel biçimi (.xls) desteklenmiyor. Dosyayı Excel'de “Excel Çalışma Kitabı (.xlsx)” olarak kaydedip tekrar seçin.";
  if (!(ACCEPTED_EXTENSIONS as readonly string[]).includes(ext)) return "Yalnızca .xlsx veya .csv dosyası yükleyebilirsiniz.";
  if (file.size === 0) return "Dosya boş. İçinde başlık satırı ve veri olan bir dosya seçin.";
  if (file.size > MAX_FILE_BYTES) return "Dosya 20 MB sınırını aşıyor. Dosyayı daha küçük parçalara bölüp ayrı ayrı yükleyin.";
  return null;
}

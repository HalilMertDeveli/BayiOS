import type { BadgeTone } from "@/components/ui/badge";

export const importTypeLabels: Record<string, string> = {
  products: "Ürünler",
  prices: "Fiyat listesi kalemleri",
};

export const importTypeDescriptions: Record<string, string> = {
  products: "Yeni ürün ekler veya mevcut ürünleri SKU/barkod ile günceller. İsterseniz fiyatı da bir fiyat listesine yazar.",
  prices: "Seçtiğiniz fiyat listesindeki fiyatları SKU/barkod ile ekler veya günceller. Yeni ürün oluşturmaz.",
};

/** Job lifecycle (ImportStatuses): label + tone; the text carries the meaning, never the color alone. */
export const importStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  uploaded: { label: "Yüklendi", tone: "neutral" },
  mapped: { label: "Eşlendi", tone: "neutral" },
  validating: { label: "Önizleme hazırlanıyor", tone: "info" },
  validated: { label: "Önizleme hazır", tone: "info" },
  running: { label: "Aktarılıyor", tone: "purple" },
  completed: { label: "Tamamlandı", tone: "success" },
  failed: { label: "Hata", tone: "danger" },
  cancelled: { label: "İptal edildi", tone: "warning" },
  rolling_back: { label: "Geri alınıyor", tone: "purple" },
  rolled_back: { label: "Geri alındı", tone: "neutral" },
  partially_rolled_back: { label: "Kısmen geri alındı", tone: "warning" },
  rollback_failed: { label: "Geri alma durdu", tone: "danger" },
};

export const statusLabel = (status: string) => importStatusMeta[status]?.label ?? status;
export const statusTone = (status: string): BadgeTone => importStatusMeta[status]?.tone ?? "neutral";

export const modeOptions = [
  { value: "upsert", label: "Yoksa ekle, varsa güncelle" },
  { value: "create_only", label: "Yalnızca yeni kayıtları ekle" },
  { value: "update_only", label: "Yalnızca mevcut kayıtları güncelle" },
] as const;

export const matchKeyOptions = [
  { value: "sku", label: "Stok kodu (SKU)" },
  { value: "barcode", label: "Barkod" },
] as const;

export const modeLabel = (mode: string) => modeOptions.find((o) => o.value === mode)?.label ?? mode;
export const matchKeyLabel = (key: string) => matchKeyOptions.find((o) => o.value === key)?.label ?? key;

/** Planned action of a row (preview). */
export const rowActionMeta: Record<string, { label: string; tone: BadgeTone }> = {
  create: { label: "Eklenecek", tone: "success" },
  update: { label: "Güncellenecek", tone: "info" },
  skip: { label: "Atlanacak", tone: "neutral" },
  error: { label: "Hatalı", tone: "danger" },
};

/** What execution did (ImportRowResponse.outcome). */
export const rowOutcomeLabels: Record<string, string> = {
  created: "Eklendi",
  updated: "Güncellendi",
  unchanged: "Değişiklik yok",
  skipped: "Atlandı",
  failed: "Başarısız",
};

export const rollbackStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  rolled_back: { label: "Geri alındı", tone: "neutral" },
  conflict: { label: "Çakışma — dokunulmadı", tone: "warning" },
  failed: { label: "Geri alınamadı", tone: "danger" },
  skipped: { label: "Geri alınacak bir şey yok", tone: "neutral" },
};

const dateTime = new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "Europe/Istanbul" });

export const formatDateTime = (iso: string | null | undefined) => (iso ? dateTime.format(new Date(iso)) : "—");

export const formatCount = (n: number) => n.toLocaleString("tr-TR");

export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toLocaleString("tr-TR", { maximumFractionDigits: 1 })} KB`;
  return `${(bytes / (1024 * 1024)).toLocaleString("tr-TR", { maximumFractionDigits: 1 })} MB`;
}

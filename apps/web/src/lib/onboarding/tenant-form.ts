import type { NegativeStockPolicy, Sector } from "@/lib/api/identity";

/** Same rule as the API (IdentityService.SlugPattern): 3–63 chars of a-z, 0-9, '-', alphanumeric at both ends. */
export const SLUG_PATTERN = /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$/;

const TURKISH_MAP: Record<string, string> = { ç: "c", ğ: "g", ı: "i", İ: "i", ö: "o", ş: "s", ü: "u", â: "a", î: "i", û: "u" };

/** "Yılmaz Motor Yedek Parça Ltd. Şti." → "yilmaz-motor-yedek-parca-ltd-sti" */
export function slugify(value: string): string {
  return value
    .replace(/[çğıİöşüâîû]/gi, (ch) => TURKISH_MAP[ch] ?? TURKISH_MAP[ch.toLowerCase()] ?? ch)
    .toLowerCase()
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 63)
    .replace(/-+$/g, "");
}

export function validateTenantName(value: string): string | undefined {
  const name = value.trim();
  if (!name) return "İşletme adını girin.";
  if (name.length < 2) return "İşletme adı en az 2 karakter olmalı.";
  if (name.length > 200) return "İşletme adı en fazla 200 karakter olabilir.";
  return undefined;
}

export function validateSlug(value: string): string | undefined {
  const slug = value.trim();
  if (!slug) return "İşletme adresini girin.";
  if (slug.length < 3 || slug.length > 63) return "İşletme adresi 3–63 karakter olmalı.";
  if (!SLUG_PATTERN.test(slug)) return "Yalnızca küçük harf (a-z), rakam ve tire kullanın; tire ile başlayamaz veya bitemez.";
  return undefined;
}

export const sectorOptions: readonly { value: Sector; label: string; description: string }[] = [
  {
    value: "motorcycle_parts",
    label: "Motosiklet yedek parça",
    description: "Model/yıl uyumluluğu, OEM ve muadil parça kodları, adet bazlı satış.",
  },
  {
    value: "led_electronics",
    label: "LED ve elektronik",
    description: "Watt, renk sıcaklığı, voltaj gibi teknik özellikler; metre ve adet birimleri.",
  },
  {
    value: "general",
    label: "Genel toptan / dağıtım",
    description: "Sektöre özel alan olmadan sade ürün kataloğu ve stok yönetimi.",
  },
];

export const negativeStockOptions: readonly { value: NegativeStockPolicy; label: string }[] = [
  { value: "block", label: "İzin verme (önerilen)" },
  { value: "warn", label: "Uyar, devam etmeye izin ver" },
  { value: "allow", label: "İzin ver" },
];

/** ISO 4217 codes offered in the wizard (the API accepts any 3-letter code). TRY is the tenant default. */
export const currencyOptions: readonly { value: string; label: string }[] = [
  { value: "TRY", label: "Türk lirası (TRY)" },
  { value: "USD", label: "ABD doları (USD)" },
  { value: "EUR", label: "Euro (EUR)" },
];

/** Turkish VAT (KDV) rates in use; 20 is the API default. */
export const taxRateOptions: readonly { value: string; label: string }[] = [
  { value: "20", label: "%20" },
  { value: "10", label: "%10" },
  { value: "1", label: "%1" },
  { value: "0", label: "%0 (KDV'siz)" },
];

export const priceDisplayOptions: readonly { value: "excluded" | "included"; label: string }[] = [
  { value: "excluded", label: "KDV hariç" },
  { value: "included", label: "KDV dahil" },
];

/** API field names (lower camel) → wizard step that owns the field. */
export const FIELD_STEP: Record<string, number> = { name: 0, slug: 0, sector: 1 };

/** Turkish text for API validation errors on tenant fields (the API's own messages are English). */
export function tenantFieldMessage(field: string): string {
  switch (field) {
    case "name":
      return "İşletme adı 2–200 karakter olmalı.";
    case "slug":
      return "İşletme adresi geçersiz. Yalnızca küçük harf, rakam ve tire kullanın (3–63 karakter).";
    case "sector":
      return "Lütfen listeden bir sektör seçin.";
    default:
      return "Bu alan geçersiz.";
  }
}

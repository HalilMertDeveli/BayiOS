import type { Sector } from "@/lib/api/identity";

/** Same rule as the API (IdentityService.SlugPattern): 3–63 chars of a-z, 0-9, '-', alphanumeric at both ends. */
export const SLUG_PATTERN = /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$/;

const TURKISH_MAP: Record<string, string> = { ç: "c", ğ: "g", ı: "i", İ: "i", ö: "o", ş: "s", ü: "u", â: "a", î: "i", û: "u" };

/** "Yıldız Toptan Gıda Ltd. Şti." → "yildiz-toptan-gida-ltd-sti" */
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

/**
 * Starter templates (API sector codes). BayiOS is sector-agnostic (owner direction 2026-11-09): the plain start is the
 * default and fits any business; the other two are optional *examples* of what custom fields can do, described by what
 * they add rather than by an industry. Everything they add can be archived later.
 */
export const sectorOptions: readonly { value: Sector; label: string; description: string }[] = [
  {
    value: "general",
    label: "Sade başlangıç (önerilen)",
    description: "Her işletmeye uygun: adet, koli ve kilogram birimleri. Kategorileri ve özel alanları sonra kendinize göre eklersiniz.",
  },
  {
    value: "motorcycle_parts",
    label: "Örnek şablon: Uyumluluk takipli parçalar",
    description: "Uyumlu marka/model, model yılı, orijinal parça no gibi alanlar ve örnek kategoriler; takım, kutu, litre, metre birimleri.",
  },
  {
    value: "led_electronics",
    label: "Örnek şablon: Teknik özellikli ürünler",
    description: "Güç, voltaj, IP sınıfı gibi teknik alanlar ve örnek kategoriler; kutu, koli, metre birimleri.",
  },
];

/** Pre-selected starter template: the plain one, so a new business never has to pick an industry. */
export const DEFAULT_SECTOR: Sector = "general";

/**
 * Owner decision OD-09 (2026-11-09): stock never goes below zero. Not a setting any more — shown as a fixed rule in
 * onboarding and settings. The API accepts only `negativeStockPolicy: "block"`.
 */
export const NEGATIVE_STOCK_RULE = {
  title: "Stok eksiye düşmez",
  text: "Satılabilir stoktan fazlası onaylanamaz, düzeltmeyle düşülemez ve sevk edilemez. Bu sabit bir kuraldır; değiştirilemez.",
} as const;

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
      return "Lütfen listeden bir şablon seçin.";
    default:
      return "Bu alan geçersiz.";
  }
}

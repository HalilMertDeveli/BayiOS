import type { Access } from "@/lib/session/access";

/**
 * Guided setup after tenant creation (#83, owner direction 2026-11-09: sector-agnostic self-serve). Every step is
 * skippable; progress is never stored as a flag — it is computed from the tenant's real data (has a warehouse, has
 * products, has stock, invited someone, first order), so the wizard and the dashboard checklist can be resumed from
 * any device. Only "the checklist was dismissed" is a local UI preference.
 */
export const SETUP_HREF = "/kurulum";

export const SETUP_STEPS = ["isletme", "depo", "urunler", "stok", "ekip", "bitti"] as const;
export type SetupStep = (typeof SETUP_STEPS)[number];

export const SETUP_STEP_LABELS: Record<SetupStep, string> = {
  isletme: "İşletme bilgileri",
  depo: "Depo",
  urunler: "Ürünler",
  stok: "Açılış stoğu",
  ekip: "Ekip",
  bitti: "Bitti",
};

/** Query parameter carrying the current step, so a reload or a link from the dashboard opens the same step. */
export const STEP_PARAM = "adim";

export function parseStep(value: string | null | undefined): SetupStep {
  return (SETUP_STEPS as readonly string[]).includes(value ?? "") ? (value as SetupStep) : "isletme";
}

export function setupStepHref(step: SetupStep): string {
  return step === "isletme" ? SETUP_HREF : `${SETUP_HREF}?${STEP_PARAM}=${step}`;
}

export function nextStep(step: SetupStep): SetupStep {
  const i = SETUP_STEPS.indexOf(step);
  return SETUP_STEPS[Math.min(i + 1, SETUP_STEPS.length - 1)];
}

export function previousStep(step: SetupStep): SetupStep {
  const i = SETUP_STEPS.indexOf(step);
  return SETUP_STEPS[Math.max(i - 1, 0)];
}

// ---- Business profile (PATCH /tenant: phone, taxOffice, taxNumber — the fields the API has) ----

export type ProfileValues = { phone: string; taxOffice: string; taxNumber: string };
export type ProfileErrors = Partial<Record<keyof ProfileValues, string>>;

/** Same rules as the API (IdentityService): VKN 10 / TCKN 11 digits, phone 7–20 of digits, space, + - ( ), office ≤ 100. */
export function validateProfile(values: ProfileValues): ProfileErrors {
  const errors: ProfileErrors = {};
  const tax = values.taxNumber.trim();
  if (tax && !/^(\d{10}|\d{11})$/.test(tax)) errors.taxNumber = "Vergi numarası 10 haneli (VKN) veya T.C. kimlik numarası 11 haneli olmalı.";
  const phone = values.phone.trim();
  if (phone && !/^[0-9 +\-()]{7,20}$/.test(phone)) errors.phone = "Telefon 7–20 karakter olmalı; yalnızca rakam, boşluk, +, -, ( ve ) kullanın.";
  if (values.taxOffice.trim().length > 100) errors.taxOffice = "Vergi dairesi en fazla 100 karakter olabilir.";
  return errors;
}

/** Merge patch with only the changed members; a cleared field is sent as null. */
export function profilePatch(current: { phone: string | null; taxOffice: string | null; taxNumber: string | null }, values: ProfileValues) {
  const patch: Partial<Record<keyof ProfileValues, string | null>> = {};
  for (const key of ["phone", "taxOffice", "taxNumber"] as const) {
    const next = values[key].trim() || null;
    if (next !== (current[key] ?? null)) patch[key] = next;
  }
  return patch;
}

/** Maps the API's English field errors on PATCH /tenant to Turkish. */
export function profileFieldMessage(field: string): string {
  switch (field) {
    case "taxNumber":
      return "Vergi numarası 10 haneli (VKN) veya T.C. kimlik numarası 11 haneli olmalı.";
    case "phone":
      return "Telefon 7–20 karakter olmalı; yalnızca rakam, boşluk, +, -, ( ve ) kullanın.";
    case "taxOffice":
      return "Vergi dairesi en fazla 100 karakter olabilir.";
    default:
      return "Bu alan geçersiz.";
  }
}

// ---- First warehouse with an optional simple shelf layout ----

export const DEFAULT_WAREHOUSE = { code: "MERKEZ", name: "Merkez Depo" } as const;
export const SHELF_COUNT_MIN = 1;
export const SHELF_COUNT_MAX = 50;
export const SHELF_DEFAULT_COUNT = 10;

export type ShelfLayout = { kind: "none" } | { kind: "simple"; prefix: string; count: number };

/** "A", 10 → A-01 … A-10 (two digits, three from 100 on). Pick sequence follows the code order in steps of 10. */
export function shelfCodes(prefix: string, count: number): { code: string; pickSequence: number }[] {
  const clean = prefix.trim().toUpperCase();
  const width = count >= 100 ? 3 : 2;
  return Array.from({ length: count }, (_, i) => ({ code: `${clean}-${String(i + 1).padStart(width, "0")}`, pickSequence: (i + 1) * 10 }));
}

export function validateShelfLayout(prefix: string, countText: string): { prefix?: string; count?: string } {
  const errors: { prefix?: string; count?: string } = {};
  if (!/^[A-Za-z0-9]{1,10}$/.test(prefix.trim())) errors.prefix = "1–10 harf veya rakam girin (ör. A).";
  const count = Number(countText.trim());
  if (!/^\d+$/.test(countText.trim()) || count < SHELF_COUNT_MIN || count > SHELF_COUNT_MAX) {
    errors.count = `${SHELF_COUNT_MIN} ile ${SHELF_COUNT_MAX} arasında bir sayı girin.`;
  }
  return errors;
}

/** Shelves of the layout that do not exist yet (a resumed or retried setup skips the ones already created). */
export function missingShelves(wanted: readonly { code: string; pickSequence: number }[], existingCodes: readonly string[]) {
  const existing = new Set(existingCodes.map((c) => c.toUpperCase()));
  return wanted.filter((s) => !existing.has(s.code.toUpperCase()));
}

// ---- Products: downloadable import template (generic, synthetic rows) ----

/**
 * Header names are the Turkish synonyms the API's column suggester recognises (ImportFields.cs), so the mapping step
 * pre-fills itself. Two clearly synthetic sample rows show the format; the unit is the "adet" code every sector
 * template creates. `;` + UTF-8 BOM: Turkish Excel opens it directly; the API detects the delimiter.
 */
export const IMPORT_TEMPLATE_HEADERS = ["Stok Kodu", "Barkod", "Ürün Adı", "Açıklama", "Birim", "KDV Oranı", "Marka", "Kategori", "Durum"] as const;
export const IMPORT_TEMPLATE_ROWS: readonly (readonly string[])[] = [
  ["ORNEK-001", "", "Örnek ürün 1", "Bu satırı silip kendi ürününüzü yazın", "adet", "20", "", "", "Aktif"],
  ["ORNEK-002", "", "Örnek ürün 2", "", "adet", "20", "", "", "Aktif"],
];
export const IMPORT_TEMPLATE_FILENAME = "bayios-urun-sablonu.csv";

function csvCell(value: string): string {
  return /[";\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

export function importTemplateCsv(): string {
  const lines = [IMPORT_TEMPLATE_HEADERS, ...IMPORT_TEMPLATE_ROWS].map((row) => row.map(csvCell).join(";"));
  return `﻿${lines.join("\r\n")}\r\n`;
}

// ---- Quick product (POST /products, simple product: sku at product level) ----

export type QuickProductValues = { name: string; sku: string; barcode: string; unitId: string };
export type QuickProductErrors = Partial<Record<keyof QuickProductValues, string>>;

export function validateQuickProduct(values: QuickProductValues): QuickProductErrors {
  const errors: QuickProductErrors = {};
  const name = values.name.trim();
  if (!name) errors.name = "Ürün adını girin.";
  else if (name.length > 200) errors.name = "Ürün adı en fazla 200 karakter olabilir.";
  const sku = values.sku.trim();
  if (!sku) errors.sku = "Stok kodunu (SKU) girin.";
  else if (sku.length > 64 || /\s/.test(sku)) errors.sku = "Boşluk içermeyen, en fazla 64 karakterlik bir kod girin.";
  const barcode = values.barcode.trim();
  if (barcode && (barcode.length > 64 || /\s/.test(barcode))) errors.barcode = "Boşluk içermeyen, en fazla 64 karakterlik bir barkod girin.";
  if (!values.unitId) errors.unitId = "Bir birim seçin.";
  return errors;
}

/** "Kırmızı Kalem 0.7" → "KIRMIZI-KALEM-0-7" (max 32): a suggestion only; the user can type their own code. */
export function suggestSku(name: string): string {
  const map: Record<string, string> = { ç: "C", ğ: "G", ı: "I", i: "I", ö: "O", ş: "S", ü: "U" };
  return name
    .toLocaleLowerCase("tr")
    .replace(/[çğıiöşü]/g, (ch) => map[ch] ?? ch)
    .toUpperCase()
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .replace(/[^A-Z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 32)
    .replace(/-+$/g, "");
}

// ---- Progress facts and the dashboard "İlk adımlar" checklist ----

/** true/false from data; null = not known (no permission to read it, or the read failed). */
export type SetupFacts = {
  warehouse: boolean | null;
  products: boolean | null;
  stock: boolean | null;
  team: boolean | null;
  order: boolean | null;
};

export type ChecklistKey = keyof SetupFacts;

export type ChecklistItem = { key: ChecklistKey; title: string; doneText: string; todoText: string; href: string; actionLabel: string };

const CHECKLIST: readonly ChecklistItem[] = [
  {
    key: "warehouse",
    title: "Deponuzu tanımlayın",
    doneText: "En az bir depo hazır.",
    todoText: "Stoğun tutulacağı ilk depoyu ve isterseniz raflarını oluşturun.",
    href: setupStepHref("depo"),
    actionLabel: "Depoyu kur",
  },
  {
    key: "products",
    title: "Ürünlerinizi ekleyin",
    doneText: "Katalogda ürün var.",
    todoText: "Excel/CSV şablonuyla toplu aktarın veya birkaç ürünü elle ekleyin.",
    href: setupStepHref("urunler"),
    actionLabel: "Ürünleri ekle",
  },
  {
    key: "stock",
    title: "Açılış stoğunu girin",
    doneText: "Stok girişi yapıldı.",
    todoText: "Elinizdeki miktarları mal kabul olarak girin; stok hiçbir zaman eksiye düşmez.",
    href: setupStepHref("stok"),
    actionLabel: "Açılış stoğunu gir",
  },
  {
    key: "team",
    title: "Ekibinizi davet edin",
    doneText: "Ekibe en az bir kişi davet edildi.",
    todoText: "Satış, depo veya yönetici olarak çalışma arkadaşlarınızı davet edin.",
    href: setupStepHref("ekip"),
    actionLabel: "Ekibi davet et",
  },
  {
    key: "order",
    title: "İlk siparişinizi oluşturun",
    doneText: "İlk sipariş oluşturuldu.",
    todoText: "Bir müşteri için sipariş açın; onayda stok ayrılır, sevkiyatta düşülür.",
    href: "/siparisler/yeni",
    actionLabel: "İlk siparişi aç",
  },
];

/** Which facts the user may read (each read needs the endpoint's permission; visibility only, the API checks again). */
export function readableFacts(access: Access): Record<ChecklistKey, boolean> {
  return {
    warehouse: access.canSome("inventory.read"),
    products: access.canSome("catalog.read"),
    stock: access.canSome("inventory.read"),
    team: access.canSome("members.read"),
    order: access.can("orders.read"),
  };
}

/** The checklist and the setup wizard are for the people who set the business up (tenant.settings.manage: owner, admin). */
export function canRunSetup(access: Access): boolean {
  return access.can("tenant.settings.manage");
}

/** Checklist rows the user can see: facts that are unknown (no permission / failed) are left out, never shown as "done". */
export function checklistItems(facts: SetupFacts): { item: ChecklistItem; done: boolean }[] {
  return CHECKLIST.filter((item) => facts[item.key] !== null).map((item) => ({ item, done: facts[item.key] === true }));
}

export function checklistProgress(facts: SetupFacts): { done: number; total: number } {
  const rows = checklistItems(facts);
  return { done: rows.filter((r) => r.done).length, total: rows.length };
}

/** localStorage key of "the checklist was dismissed" — per user and tenant (a UI preference, not data). */
export function checklistDismissKey(userId: string, tenantId: string): string {
  return `bayios.setup-checklist.dismissed.${userId}.${tenantId}`;
}

/** Plain-Turkish role explanations for the invite step (roles-permissions.md §2). */
export const ROLE_PLAIN_TEXT: Record<string, string> = {
  admin: "Sizin gibi her şeyi yönetir: ayarlar, ekip, fiyatlar ve raporlar. Paketi ve sahipliği değiştiremez.",
  manager: "Günlük işi yürütür: siparişleri onaylar, stok ve depo işlerini yönetir, görev dağıtır.",
  sales: "Müşterileri ve siparişleri girer. Onay ve iskonto, işletme kurallarınıza göre.",
  warehouse_staff: "Depoda çalışır: mal kabul, toplama, paketleme ve sevkiyat. Fiyatları görmez.",
  viewer: "Yalnızca bakar: raporları ve listeleri görür, hiçbir şeyi değiştiremez.",
};

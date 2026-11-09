import type { BadgeTone } from "@/components/ui/badge";

type Meta = { label: string; tone: BadgeTone };

export const statusMeta = (map: Record<string, Meta>, status: string): Meta => map[status] ?? { label: status, tone: "neutral" };

// ---- Transfers (OD-01: draft → in_transit "Yolda" → partially_received → received | closed; cancel before dispatch) ----

export const TRANSFER_STATUSES = ["draft", "in_transit", "partially_received", "received", "closed", "cancelled"] as const;
/** Goods are (partly) on the road. */
export const TRANSFER_OPEN_STATUSES = ["in_transit", "partially_received"] as const;

export const transferStatusMeta: Record<string, Meta> = {
  draft: { label: "Taslak", tone: "info" },
  in_transit: { label: "Yolda", tone: "warning" },
  partially_received: { label: "Kısmen teslim alındı", tone: "purple" },
  received: { label: "Teslim alındı", tone: "success" },
  closed: { label: "Farkla kapatıldı", tone: "teal" },
  cancelled: { label: "İptal", tone: "neutral" },
};

/** List filter → statuses (empty = all). */
export const TRANSFER_FILTERS = {
  active: { label: "Açık (taslak + yolda)", statuses: ["draft", "in_transit", "partially_received"] },
  draft: { label: "Taslak", statuses: ["draft"] },
  transit: { label: "Yolda (kısmen teslim alınan dahil)", statuses: [...TRANSFER_OPEN_STATUSES] },
  done: { label: "Tamamlanan (teslim alındı + kapatıldı)", statuses: ["received", "closed"] },
  cancelled: { label: "İptal", statuses: ["cancelled"] },
  any: { label: "Tümü", statuses: [] },
} as const satisfies Record<string, { label: string; statuses: readonly string[] }>;
export type TransferFilter = keyof typeof TRANSFER_FILTERS;

export const transferResolutionLabels: Record<string, string> = {
  lost: "Kayıp olarak düşüldü",
  return_to_source: "Kaynak depoya iade edildi",
};

// ---- Stock counts (open → submitted → applied; recount → open; cancel while not applied) ----

export const COUNT_STATUSES = ["open", "submitted", "applied", "cancelled"] as const;

export const countStatusMeta: Record<string, Meta> = {
  open: { label: "Sayılıyor", tone: "info" },
  submitted: { label: "Onay bekliyor", tone: "warning" },
  applied: { label: "Uygulandı", tone: "success" },
  cancelled: { label: "İptal", tone: "neutral" },
};

export const COUNT_FILTERS = {
  active: { label: "Açık (sayılıyor + onay bekliyor)", statuses: ["open", "submitted"] },
  applied: { label: "Uygulandı", statuses: ["applied"] },
  cancelled: { label: "İptal", statuses: ["cancelled"] },
  any: { label: "Tümü", statuses: [] },
} as const satisfies Record<string, { label: string; statuses: readonly string[] }>;
export type CountFilter = keyof typeof COUNT_FILTERS;

export const countLineStatusMeta: Record<string, Meta> = {
  pending: { label: "Sayılmadı", tone: "neutral" },
  counted: { label: "Sayıldı", tone: "info" },
  applied: { label: "Uygulandı", tone: "success" },
  uncounted: { label: "Sayılmadan kapandı", tone: "neutral" },
};

export const COUNT_SCOPES = ["full", "locations", "variants"] as const;
export type CountScope = (typeof COUNT_SCOPES)[number];

export const countScopeLabels: Record<string, string> = {
  full: "Tüm depo",
  locations: "Seçili raflar",
  variants: "Seçili ürünler",
};

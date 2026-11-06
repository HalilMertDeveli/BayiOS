import type { BadgeTone, OrderStatus } from "@/components/ui/badge";

/** Order statuses as the API sends them (OrderStatuses), in lifecycle order. */
export const ORDER_STATUSES = ["draft", "pending_approval", "approved", "picking", "packed", "shipped", "completed", "cancelled"] as const;
export type ApiOrderStatus = (typeof ORDER_STATUSES)[number];

/** API status → design-system badge key (design-system-plan §2.2). Unknown values fall back to null (shown as text). */
const BADGE_KEYS: Record<ApiOrderStatus, OrderStatus> = {
  draft: "Draft",
  pending_approval: "PendingApproval",
  approved: "Approved",
  picking: "Picking",
  packed: "Packed",
  shipped: "Shipped",
  completed: "Completed",
  cancelled: "Cancelled",
};

export function orderBadgeKey(status: string): OrderStatus | null {
  return (BADGE_KEYS as Record<string, OrderStatus>)[status] ?? null;
}

export const orderStatusLabels: Record<ApiOrderStatus, string> = {
  draft: "Taslak",
  pending_approval: "Onay Bekliyor",
  approved: "Onaylandı",
  picking: "Toplanıyor",
  packed: "Paketlendi",
  shipped: "Sevk Edildi",
  completed: "Tamamlandı",
  cancelled: "İptal",
};

export function orderStatusLabel(status: string | null | undefined): string {
  if (!status) return "—";
  return (orderStatusLabels as Record<string, string>)[status] ?? status;
}

/** fulfillment_status: derived from shipped vs ordered quantities; not a state of its own (inventory-and-orders §3.1). */
export const fulfillmentMeta: Record<string, { label: string; tone: BadgeTone }> = {
  none: { label: "Sevk yok", tone: "neutral" },
  partial: { label: "Kısmi sevk", tone: "info" },
  full: { label: "Tamamı sevk edildi", tone: "success" },
};

export const ORDER_SOURCES = ["manual", "whatsapp_ai", "import", "mobile"] as const;

export const orderSourceLabels: Record<string, string> = {
  manual: "Manuel",
  whatsapp_ai: "WhatsApp (AI)",
  import: "İçe aktarma",
  mobile: "Mobil",
};

/** order_status_history.action → timeline text. */
export const historyActionLabels: Record<string, string> = {
  create: "Taslak oluşturuldu",
  submit: "Onaya gönderildi",
  withdraw: "Onaydan geri çekildi",
  reject: "Reddedildi (taslağa döndü)",
  approve: "Onaylandı, stok rezerve edildi",
  start_picking: "Toplama başladı",
  pack: "Paketlendi",
  ship: "Sevkiyat kaydedildi",
  close_remaining: "Kalan miktar kapatıldı",
  report_shortage: "Depoda eksik bildirildi",
  complete: "Tamamlandı",
  cancel: "İptal edildi",
};

/** OrderLineResponse.priceSource: a Pricing source or a manual override. */
export const linePriceSourceLabels: Record<string, string> = {
  customer_price_list: "Müşteriye özel liste",
  dealer_group_price_list: "Bayi grubu listesi",
  default_price_list: "Varsayılan liste",
  manual: "Manuel fiyat",
};

/** Preset cancel reasons (user-flows AF-04); the text sent is "<preset>: <note>". */
export const CANCEL_REASONS = ["Müşteri vazgeçti", "Stok yok", "Fiyat anlaşmazlığı", "Mükerrer sipariş", "Diğer"] as const;

/** Preset reasons for closing the rest of a partially shipped order (short-close); sent the same way. */
export const CLOSE_REMAINING_REASONS = ["Stok tedarik edilemiyor", "Müşteri kalanı istemiyor", "Ürün artık satılmıyor", "Diğer"] as const;

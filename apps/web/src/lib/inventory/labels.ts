import type { BadgeTone } from "@/components/ui/badge";

/** Movement types (inventory_movements.type) → Turkish. */
export const movementTypeLabels: Record<string, string> = {
  receipt: "Mal kabul",
  adjustment_in: "Düzeltme (giriş)",
  adjustment_out: "Düzeltme (çıkış)",
  count_correction: "Sayım düzeltmesi",
  transfer_out: "Transfer (çıkış)",
  transfer_in: "Transfer (giriş)",
  shipment_out: "Sevkiyat",
  return_in: "İade girişi",
  damage_out: "Hasar / fire",
};

export function movementTypeLabel(type: string): string {
  return movementTypeLabels[type] ?? type;
}

export function movementTypeTone(type: string): BadgeTone {
  if (type === "receipt" || type === "return_in" || type === "transfer_in" || type === "adjustment_in") return "success";
  if (type === "damage_out") return "danger";
  if (type === "shipment_out") return "teal";
  if (type === "count_correction") return "purple";
  return "neutral";
}

/** Adjustment types the API accepts (§4.2), in the order the form offers them. */
export const ADJUSTMENT_TYPES = ["adjustment_in", "adjustment_out", "damage_out", "return_in"] as const;
export type AdjustmentType = (typeof ADJUSTMENT_TYPES)[number];

export const adjustmentTypeLabels: Record<AdjustmentType, string> = {
  adjustment_in: "Stok ekle (düzeltme girişi)",
  adjustment_out: "Stok düş (düzeltme çıkışı)",
  damage_out: "Hasar / fire çıkışı",
  return_in: "İade girişi",
};

export function isOutbound(type: string): boolean {
  return type === "adjustment_out" || type === "damage_out" || type === "transfer_out" || type === "shipment_out";
}

const dateFormatter = new Intl.DateTimeFormat("tr-TR", { dateStyle: "short", timeStyle: "short", timeZone: "Europe/Istanbul" });

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : dateFormatter.format(date);
}

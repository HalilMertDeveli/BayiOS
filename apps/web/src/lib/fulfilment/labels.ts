import type { BadgeTone } from "@/components/ui/badge";

export const TASK_ACTIVE_STATUSES = ["open", "in_progress"] as const;
export const TASK_DONE_STATUSES = ["completed", "completed_with_shortage", "cancelled"] as const;

export const taskStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  open: { label: "Bekliyor", tone: "info" },
  in_progress: { label: "Toplanıyor", tone: "warning" },
  completed: { label: "Tamamlandı", tone: "success" },
  completed_with_shortage: { label: "Eksikle tamamlandı", tone: "danger" },
  cancelled: { label: "İptal", tone: "neutral" },
};

export const taskTypeLabels: Record<string, string> = { pick: "Toplama", putaway: "Rafa geri koyma" };

export const SHIPMENT_OPEN_STATUSES = ["draft", "packed"] as const;

export const shipmentStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  draft: { label: "Taslak", tone: "info" },
  packed: { label: "Paketlendi", tone: "purple" },
  confirmed: { label: "Sevk edildi", tone: "success" },
  cancelled: { label: "İptal", tone: "neutral" },
};

export const SHORTAGE_REASONS = ["not_found", "damaged", "insufficient"] as const;
export type ShortageReason = (typeof SHORTAGE_REASONS)[number];

export const shortageReasonLabels: Record<string, string> = {
  not_found: "Rafta bulunamadı",
  damaged: "Hasarlı",
  insufficient: "Raftaki miktar yetersiz",
};

export const statusMeta = (map: Record<string, { label: string; tone: BadgeTone }>, status: string) =>
  map[status] ?? { label: status, tone: "neutral" as const };

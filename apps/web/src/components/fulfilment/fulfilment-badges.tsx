import { Badge } from "@/components/ui/badge";
import { shipmentStatusMeta, statusMeta, taskStatusMeta } from "@/lib/fulfilment/labels";

export function TaskStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(taskStatusMeta, status);
  return <Badge tone={meta.tone}>{meta.label}</Badge>;
}

export function ShipmentStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(shipmentStatusMeta, status);
  return <Badge tone={meta.tone}>{meta.label}</Badge>;
}

/** Picked / to-pick as a thin bar + text (the text carries the meaning; the bar is decorative). */
export function ProgressBar({ value, max, label }: { value: number; max: number; label: string }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0;
  return (
    <div className="flex flex-col gap-1">
      <span className="text-caption text-fg-secondary">{label}</span>
      <div className="h-2 w-full overflow-hidden rounded-full bg-sunken" aria-hidden="true">
        <div className="h-full rounded-full bg-brand" style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

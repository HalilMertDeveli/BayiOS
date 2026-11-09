import { ArrowRightLeft, Check, CheckCheck, CircleDashed, CircleX, ClipboardCheck, Clock, Hourglass, PackageCheck, Pencil, Truck, TriangleAlert, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { countLineStatusMeta, countStatusMeta, statusMeta, transferStatusMeta } from "@/lib/stock-documents/labels";

/** Status icons (design-system-plan §1.3: colour + text + icon); unknown statuses get none. */
const TRANSFER_ICONS: Record<string, LucideIcon> = {
  draft: Pencil,
  in_transit: Truck,
  partially_received: ArrowRightLeft,
  received: PackageCheck,
  closed: CheckCheck,
  cancelled: CircleX,
};
const COUNT_ICONS: Record<string, LucideIcon> = { open: ClipboardCheck, submitted: Hourglass, applied: CheckCheck, cancelled: CircleX };
const COUNT_LINE_ICONS: Record<string, LucideIcon> = { pending: CircleDashed, counted: Check, applied: CheckCheck, uncounted: CircleDashed };

function iconFor(map: Record<string, LucideIcon>, status: string) {
  const Icon = map[status];
  return Icon ? <Icon className="size-3.5" aria-hidden="true" /> : undefined;
}

export function TransferStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(transferStatusMeta, status);
  return (
    <Badge tone={meta.tone} icon={iconFor(TRANSFER_ICONS, status)}>
      {meta.label}
    </Badge>
  );
}

/** In transit for more than 7 days (AF-07). */
export function OverdueBadge() {
  return (
    <Badge tone="danger" icon={<Clock className="size-3.5" />}>
      Gecikmiş (7 günden uzun yolda)
    </Badge>
  );
}

/** Received or closed with less than was sent. */
export function DiscrepancyBadge() {
  return (
    <Badge tone="warning" icon={<TriangleAlert className="size-3.5" />}>
      Fark var
    </Badge>
  );
}

export function CountStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(countStatusMeta, status);
  return (
    <Badge tone={meta.tone} icon={iconFor(COUNT_ICONS, status)}>
      {meta.label}
    </Badge>
  );
}

export function CountLineStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(countLineStatusMeta, status);
  return (
    <Badge tone={meta.tone} icon={iconFor(COUNT_LINE_ICONS, status)}>
      {meta.label}
    </Badge>
  );
}

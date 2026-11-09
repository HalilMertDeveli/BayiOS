import { Clock, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { countLineStatusMeta, countStatusMeta, statusMeta, transferStatusMeta } from "@/lib/stock-documents/labels";

export function TransferStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(transferStatusMeta, status);
  return <Badge tone={meta.tone}>{meta.label}</Badge>;
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
  return <Badge tone={meta.tone}>{meta.label}</Badge>;
}

export function CountLineStatusBadge({ status }: { status: string }) {
  const meta = statusMeta(countLineStatusMeta, status);
  return <Badge tone={meta.tone}>{meta.label}</Badge>;
}

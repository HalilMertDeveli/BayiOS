import { Badge, type BadgeTone } from "@/components/ui/badge";

const meta: Record<string, { label: string; tone: BadgeTone }> = {
  active: { label: "Aktif", tone: "success" },
  inactive: { label: "Pasif", tone: "neutral" },
  archived: { label: "Arşivlendi", tone: "warning" },
};

export function CustomerStatusBadge({ status, anonymized = false }: { status: string; anonymized?: boolean }) {
  if (anonymized) return <Badge tone="danger">Anonimleştirildi</Badge>;
  const m = meta[status] ?? { label: status, tone: "neutral" as const };
  return <Badge tone={m.tone}>{m.label}</Badge>;
}

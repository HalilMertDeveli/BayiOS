import { TriangleAlert } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/cn";
import { formatQuantity, toNumber } from "@/lib/inventory/quantity";

/**
 * Available (satılabilir) quantity. Zero and negative values carry a text label, not only a colour.
 */
export function AvailableQuantity({ value, className }: { value: number | string; className?: string }) {
  const n = toNumber(value);
  return (
    <span className={cn("tabular-nums", n < 0 ? "font-semibold text-danger-fg" : n === 0 ? "text-fg-muted" : "font-semibold text-fg", className)}>
      {formatQuantity(value)}
      {n < 0 ? <span className="ml-1 text-caption font-medium">(eksi)</span> : n === 0 ? <span className="ml-1 text-caption">(yok)</span> : null}
    </span>
  );
}

/** "Kritik" — the variant's available stock in the warehouse is at or below its minimum (#137). Text, not only colour. */
export function CriticalBadge() {
  return (
    <Badge tone="danger" icon={<TriangleAlert className="size-3.5" />}>
      Kritik
    </Badge>
  );
}

/**
 * Minimum stock of a balance row (effective minimum of the variant in that warehouse) with the "Kritik" badge when the
 * warehouse total is at or below it; "—" when no minimum is set.
 */
export function MinimumCell({ minQuantity, belowMinimum }: { minQuantity: number | string | null | undefined; belowMinimum: boolean | undefined }) {
  if (minQuantity === null || minQuantity === undefined) return <span className="text-fg-muted">—</span>;
  return (
    <span className="inline-flex items-center justify-end gap-2">
      {belowMinimum ? <CriticalBadge /> : null}
      <span className="tabular-nums">{formatQuantity(minQuantity)}</span>
    </span>
  );
}

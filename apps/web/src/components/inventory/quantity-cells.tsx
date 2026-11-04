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

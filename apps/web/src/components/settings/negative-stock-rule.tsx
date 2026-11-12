import { Lock } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";
import { NEGATIVE_STOCK_RULE } from "@/lib/onboarding/tenant-form";

/** Owner decision OD-09: "stock never goes below zero" is a fixed rule, shown — not offered as a choice. */
export function NegativeStockRule() {
  return (
    <div className="flex flex-col gap-1" data-testid="negative-stock-rule">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-body font-medium text-fg">{NEGATIVE_STOCK_RULE.title}</span>
        <Badge icon={<Lock className="size-3.5" aria-hidden="true" />}>Sabit kural</Badge>
      </div>
      <p className="text-body text-fg-secondary">{NEGATIVE_STOCK_RULE.text}</p>
    </div>
  );
}

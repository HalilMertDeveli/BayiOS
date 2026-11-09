import { formatQuantity } from "@/lib/inventory/quantity";
import type { Shortage } from "@/lib/inventory/errors";

/** The `shortages` of an insufficient-stock / below-reserved problem: what was asked vs what is possible, per product. */
export function ShortageList({ shortages, shelfCode, requestedLabel = "istenen", availableLabel = "satılabilir" }: {
  shortages: readonly Shortage[];
  shelfCode?: (locationId: string) => string | null;
  requestedLabel?: string;
  availableLabel?: string;
}) {
  if (shortages.length === 0) return null;
  return (
    <ul className="mt-1 list-disc pl-5" aria-label="Yetersiz stok satırları">
      {shortages.map((s, i) => {
        const shelf = s.locationId ? shelfCode?.(s.locationId) : null;
        return (
          <li key={`${s.variantId}-${s.locationId ?? ""}-${i}`}>
            <span className="font-mono">{s.sku ?? "Ürün"}</span>
            {shelf ? ` (raf ${shelf})` : ""}: {requestedLabel} {formatQuantity(s.requested)}, {availableLabel} {formatQuantity(s.available)}
          </li>
        );
      })}
    </ul>
  );
}

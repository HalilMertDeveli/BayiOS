"use client";

import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import type { Shortage } from "@/lib/inventory/errors";
import { formatQuantity } from "@/lib/inventory/quantity";

/**
 * Tenant policy `warn`: the API refused with `confirmationRequired`. Nothing is saved until the user explicitly
 * confirms here; the caller then repeats the request with `confirmNegativeStock: true`.
 */
export function NegativeStockDialog({
  open,
  shortages,
  skuOf,
  confirming,
  onCancel,
  onConfirm,
}: {
  open: boolean;
  shortages: readonly Shortage[];
  skuOf: (variantId: string, fallback: string | null) => string;
  confirming: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Dialog
      open={open}
      onClose={confirming ? () => undefined : onCancel}
      role="alertdialog"
      dismissOnBackdrop={false}
      title="Stok eksiye düşecek"
      description="Bu işlem satılabilir stoğun altına iniyor. İşletmenizin negatif stok ayarı “uyar” olduğu için açık onayınız gerekiyor."
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onCancel} disabled={confirming}>
            Vazgeç
          </Button>
          <Button variant="danger" size="lg" onClick={onConfirm} loading={confirming}>
            Yine de kaydet
          </Button>
        </>
      }
    >
      {shortages.length > 0 ? (
        <ul className="flex flex-col gap-1">
          {shortages.map((s, i) => (
            <li key={`${s.variantId}-${s.locationId ?? ""}-${i}`}>
              <span className="font-mono font-semibold text-fg">{skuOf(s.variantId, s.sku)}</span>: istenen {formatQuantity(s.requested)}, satılabilir{" "}
              {formatQuantity(s.available)}
            </li>
          ))}
        </ul>
      ) : null}
      <p className="mt-2">Onaylarsanız stok eksi bakiyeye düşer ve bu durum hareket geçmişinde görünür.</p>
    </Dialog>
  );
}

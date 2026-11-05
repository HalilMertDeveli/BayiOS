"use client";

import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { listAddresses, type Address } from "@/lib/customers/api";
import { customerErrorMessage } from "@/lib/customers/errors";
import { addressChoice, type OrderAddressType } from "@/lib/orders/addresses";
import { useSession } from "@/lib/session/session-context";

type Loadable = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; items: Address[] };

const LABELS: Record<OrderAddressType, { label: string; none: string }> = {
  shipping: { label: "Teslimat adresi", none: "Bu müşterinin teslimat adresi yok; müşteri kartından ekleyebilirsiniz." },
  billing: { label: "Fatura adresi", none: "Bu müşterinin fatura adresi yok; müşteri kartından ekleyebilirsiniz." },
};

/**
 * Shipping and billing address of a draft (#92): the selected customer's addresses of each type, the default one
 * preselected ("" = default, resolved by the API when the order is submitted). Needs customers.read (the caller checks).
 */
export function OrderAddressPickers({
  customerId,
  shippingAddressId,
  billingAddressId,
  errors,
  disabled,
  onChange,
}: {
  customerId: string | null;
  shippingAddressId: string;
  billingAddressId: string;
  errors: Record<string, string>;
  disabled?: boolean;
  onChange: (patch: { shippingAddressId?: string; billingAddressId?: string }) => void;
}) {
  const { api } = useSession();
  const [state, setState] = useState<{ key: string; value: Loadable } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const key = customerId ? `${customerId}#${attempt}` : "";

  useEffect(() => {
    if (!customerId) return;
    const controller = new AbortController();
    const requestKey = `${customerId}#${attempt}`;
    listAddresses(api, customerId, controller.signal)
      .then((items) => {
        if (!controller.signal.aborted) setState({ key: requestKey, value: { status: "ready", items } });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ key: requestKey, value: { status: "error", message: customerErrorMessage(error) } });
      });
    return () => controller.abort();
  }, [api, customerId, attempt]);

  if (!customerId) {
    return <p className="text-caption text-fg-muted">Teslimat ve fatura adresi müşteri seçildikten sonra seçilir.</p>;
  }
  const loadable: Loadable = state && state.key === key ? state.value : { status: "loading" };
  if (loadable.status === "error") {
    return (
      <div role="alert" className="flex flex-wrap items-center gap-2 text-body text-danger-fg">
        <span>Müşterinin adresleri yüklenemedi. {loadable.message}</span>
        <Button size="sm" variant="secondary" onClick={() => setAttempt((n) => n + 1)}>
          Tekrar dene
        </Button>
      </div>
    );
  }

  const picker = (type: OrderAddressType, value: string, field: "shippingAddressId" | "billingAddressId") => {
    if (loadable.status === "loading") {
      return <Select label={LABELS[type].label} options={[]} placeholder="Adresler yükleniyor…" value="" disabled onChange={() => undefined} />;
    }
    const choice = addressChoice(loadable.items, type, value);
    const error = errors[field] ?? (choice.missing ? "Seçili adres müşteriden silinmiş. Başka bir adres seçin." : undefined);
    return (
      <Select
        label={LABELS[type].label}
        options={choice.options}
        value={value}
        error={error}
        disabled={disabled || (choice.empty && !value)}
        hint={
          choice.empty
            ? LABELS[type].none
            : "Sipariş onaya gönderildiğinde seçili adres siparişe kopyalanır; sonraki değişiklikler siparişi etkilemez."
        }
        onChange={(e) => onChange({ [field]: e.target.value })}
      />
    );
  };

  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2" aria-busy={loadable.status === "loading" || undefined}>
      {picker("shipping", shippingAddressId, "shippingAddressId")}
      {picker("billing", billingAddressId, "billingAddressId")}
    </div>
  );
}

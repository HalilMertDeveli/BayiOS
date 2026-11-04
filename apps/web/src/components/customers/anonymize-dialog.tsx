"use client";

import { useState } from "react";
import { ShieldAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { anonymizeCustomer, type Customer, type CustomerDetail } from "@/lib/customers/api";
import { confirmsName } from "@/lib/customers/customer-form";
import { customerErrorMessage } from "@/lib/customers/errors";
import { useSession } from "@/lib/session/session-context";

/**
 * KVKK erasure (data-policy §5): irreversible, owner/admin only. The confirm button stays disabled until the
 * customer's name is typed exactly — a deliberate speed bump, not a security control (the API checks the role).
 */
export function AnonymizeDialog({ customer, onClose, onDone }: { customer: Customer; onClose: () => void; onDone: (detail: CustomerDetail) => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // One key for this dialog: a retry after a lost response replays the first result instead of failing with 409.
  const [keys] = useState(() => new IdempotencyKeys());
  const confirmed = confirmsName(typed, customer.name);

  const run = async () => {
    if (!confirmed || busy) return;
    setBusy(true);
    setError(null);
    try {
      const detail = await anonymizeCustomer(api, customer.id, keys.keyFor({ customerId: customer.id }));
      toast({ tone: "success", title: "Müşteri anonimleştirildi", description: `Kod ${detail.customer.code} sipariş geçmişi için korunuyor.` });
      onDone(detail);
    } catch (err) {
      setError(customerErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      role="alertdialog"
      onClose={onClose}
      title="Müşteri anonimleştirilsin mi?"
      description="Bu işlem geri alınamaz."
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" loading={busy} disabled={!confirmed} onClick={() => void run()} icon={<ShieldAlert className="size-4" />}>
            Kalıcı olarak anonimleştir
          </Button>
        </>
      }
    >
      <form
        noValidate
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          void run();
        }}
      >
        <ul className="list-disc pl-5">
          <li>Ad, yetkili, vergi no, telefon, e-posta, WhatsApp ve notlar silinir.</li>
          <li>Tüm adresler kaldırılır; müşteri arşivlenir.</li>
          <li>Müşteri kodu ({customer.code}) ve sipariş geçmişi korunur.</li>
        </ul>
        <Input
          label={`Onaylamak için müşteri adını yazın: ${customer.name}`}
          value={typed}
          autoComplete="off"
          spellCheck={false}
          onChange={(e) => setTyped(e.target.value)}
          hint="Ad tam olarak eşleşince düğme etkinleşir."
        />
        {error ? (
          <p role="alert" className="text-body text-danger-fg">
            {error}
          </p>
        ) : null}
      </form>
    </Dialog>
  );
}

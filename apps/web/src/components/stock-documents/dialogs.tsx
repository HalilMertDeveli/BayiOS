"use client";

import { useState, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/textarea";

/** A required reason (1–500 characters) before an irreversible command. Resolves with an error text, or null when done. */
export function ReasonDialog({
  open,
  title,
  description,
  label,
  confirmLabel,
  onClose,
  onSubmit,
}: {
  open: boolean;
  title: string;
  description: ReactNode;
  label: string;
  confirmLabel: string;
  onClose: () => void;
  onSubmit: (reason: string) => Promise<string | null>;
}) {
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    const text = reason.trim();
    if (!text) {
      setError("Sebebi yazın.");
      return;
    }
    setBusy(true);
    setError(await onSubmit(text));
    setBusy(false);
  };
  return (
    <Dialog
      open={open}
      onClose={() => !busy && onClose()}
      role="alertdialog"
      dismissOnBackdrop={false}
      title={title}
      description={description}
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" size="lg" loading={busy} onClick={() => void submit()}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <Textarea label={label} value={reason} maxLength={500} error={error ?? undefined} onChange={(e) => setReason(e.target.value)} />
    </Dialog>
  );
}

/** Yes/no confirmation of a command (dispatch, submit, apply). */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel,
  busy,
  children,
  onClose,
  onConfirm,
}: {
  open: boolean;
  title: string;
  description?: ReactNode;
  confirmLabel: string;
  busy?: boolean;
  children?: ReactNode;
  onClose: () => void;
  onConfirm: () => void;
}) {
  return (
    <Dialog
      open={open}
      onClose={() => !busy && onClose()}
      role="alertdialog"
      dismissOnBackdrop={false}
      title={title}
      description={description}
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button size="lg" loading={busy} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      {children}
    </Dialog>
  );
}

"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import type { OrderTransition } from "@/lib/orders/api";
import { CANCEL_REASONS } from "@/lib/orders/labels";

export const REASON_MAX_LENGTH = 500;

/** Cancel reason as sent: "<preset>" or "<preset>: <note>" ("Diğer" needs a note). */
export function cancelReasonText(preset: string, note: string): string {
  const text = note.trim();
  return text ? `${preset}: ${text}` : preset;
}

/** Client checks for the reason dialogs; the API checks again (required, ≤ 500 characters). */
export function validateReason(action: "reject" | "cancel", preset: string, note: string): string | null {
  if (action === "cancel") {
    if (!preset) return "Bir iptal sebebi seçin.";
    if (preset === "Diğer" && !note.trim()) return "“Diğer” için kısa bir açıklama yazın.";
    if (cancelReasonText(preset, note).length > REASON_MAX_LENGTH) return `Sebep en fazla ${REASON_MAX_LENGTH} karakter olabilir.`;
    return null;
  }
  if (!note.trim()) return "Ret sebebini yazın; satış ekibi taslağı buna göre düzeltir.";
  if (note.trim().length > REASON_MAX_LENGTH) return `Sebep en fazla ${REASON_MAX_LENGTH} karakter olabilir.`;
  return null;
}

/**
 * Reason dialog for reject (free text) and cancel (preset + note, user-flows AF-04). Cancel shows what happens to the
 * reservation; nothing is sent until the user confirms.
 */
export function ReasonDialog({
  action,
  orderNumber,
  holdsReservation,
  busy,
  error,
  onCancel,
  onConfirm,
}: {
  action: Extract<OrderTransition, "reject" | "cancel"> | null;
  orderNumber: string;
  holdsReservation: boolean;
  busy: boolean;
  error: string | null;
  onCancel: () => void;
  onConfirm: (reason: string) => void;
}) {
  const [preset, setPreset] = useState("");
  const [note, setNote] = useState("");
  const [localError, setLocalError] = useState<string | null>(null);
  const open = action !== null;
  const isCancel = action === "cancel";

  const close = () => {
    if (busy) return;
    setPreset("");
    setNote("");
    setLocalError(null);
    onCancel();
  };

  const confirm = () => {
    if (!action) return;
    const problem = validateReason(action, preset, note);
    setLocalError(problem);
    if (problem) return;
    onConfirm(isCancel ? cancelReasonText(preset, note) : note.trim());
  };

  return (
    <Dialog
      open={open}
      onClose={close}
      role="alertdialog"
      dismissOnBackdrop={false}
      title={isCancel ? `${orderNumber} iptal edilsin mi?` : `${orderNumber} reddedilsin mi?`}
      description={
        isCancel
          ? holdsReservation
            ? "İptal geri alınamaz. Bu sipariş için ayrılan stok serbest bırakılır ve tekrar satılabilir olur."
            : "İptal geri alınamaz. Siparişte henüz stok ayrılmadı."
          : "Sipariş taslağa döner; satış ekibi düzeltip yeniden onaya gönderebilir."
      }
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={close} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant={isCancel ? "danger" : "primary"} size="lg" loading={busy} onClick={confirm}>
            {isCancel ? "Siparişi iptal et" : "Reddet"}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {isCancel ? (
          <Select
            label="İptal sebebi"
            placeholder="Sebep seçin"
            options={CANCEL_REASONS.map((r) => ({ value: r, label: r }))}
            value={preset}
            onChange={(e) => setPreset(e.target.value)}
          />
        ) : null}
        <Textarea
          label={isCancel ? "Açıklama" : "Ret sebebi"}
          hint={isCancel ? "“Diğer” seçildiyse zorunlu." : "Zorunlu; zaman çizelgesinde görünür."}
          value={note}
          maxLength={REASON_MAX_LENGTH}
          error={localError ?? error ?? undefined}
          onChange={(e) => setNote(e.target.value)}
        />
      </div>
    </Dialog>
  );
}

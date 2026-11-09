"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import type { ReasonAction } from "@/lib/orders/actions";
import type { OrderLine } from "@/lib/orders/api";
import { CANCEL_REASONS, CLOSE_REMAINING_REASONS } from "@/lib/orders/labels";
import { formatQuantity } from "@/lib/pricing/money";

export const REASON_MAX_LENGTH = 500;

/** Cancel / close-remaining reason as sent: "<preset>" or "<preset>: <note>" ("Diğer" needs a note). */
export function cancelReasonText(preset: string, note: string): string {
  const text = note.trim();
  return text ? `${preset}: ${text}` : preset;
}

/** Client checks for the reason dialogs; the API checks again (required, ≤ 500 characters). */
export function validateReason(action: ReasonAction, preset: string, note: string): string | null {
  if (action === "cancel" || action === "close-remaining") {
    if (!preset) return action === "cancel" ? "Bir iptal sebebi seçin." : "Kalanı kapatma sebebini seçin.";
    if (preset === "Diğer" && !note.trim()) return "“Diğer” için kısa bir açıklama yazın.";
    if (cancelReasonText(preset, note).length > REASON_MAX_LENGTH) return `Sebep en fazla ${REASON_MAX_LENGTH} karakter olabilir.`;
    return null;
  }
  if (!note.trim()) return "Ret sebebini yazın; satış ekibi taslağı buna göre düzeltir.";
  if (note.trim().length > REASON_MAX_LENGTH) return `Sebep en fazla ${REASON_MAX_LENGTH} karakter olabilir.`;
  return null;
}

/** Lines that still have an open (not yet shipped, not cancelled) quantity — what close-remaining closes. */
export function remainingLines(lines: readonly OrderLine[]): OrderLine[] {
  return lines.filter((l) => Number(l.openQuantity) > 0);
}

type DialogText = { title: string; description: string; confirm: string; presets: readonly string[] | null; noteLabel: string; noteHint: string };

function textFor(action: ReasonAction, orderNumber: string, holdsReservation: boolean): DialogText {
  switch (action) {
    case "cancel":
      return {
        title: `${orderNumber} iptal edilsin mi?`,
        description: holdsReservation
          ? "İptal geri alınamaz. Bu sipariş için ayrılan stok serbest bırakılır ve tekrar satılabilir olur."
          : "İptal geri alınamaz. Siparişte henüz stok ayrılmadı.",
        confirm: "Siparişi iptal et",
        presets: CANCEL_REASONS,
        noteLabel: "Açıklama",
        noteHint: "“Diğer” seçildiyse zorunlu.",
      };
    case "close-remaining":
      return {
        title: `${orderNumber}: kalan miktar kapatılsın mı?`,
        description:
          "Sevk edilmemiş kalan miktar kapatılır ve bu miktar için ayrılan stok serbest bırakılır (tekrar satılabilir olur). Sipariş “Sevk Edildi” durumuna geçer; açık toplama görevleri ve onaylanmamış sevkiyatlar iptal edilir, toplanmış ama sevk edilmemiş ürünler için rafa geri koyma görevi oluşur. Geri alınamaz.",
        confirm: "Kalanı kapat",
        presets: CLOSE_REMAINING_REASONS,
        noteLabel: "Açıklama",
        noteHint: "“Diğer” seçildiyse zorunlu; zaman çizelgesinde görünür.",
      };
    default:
      return {
        title: `${orderNumber} reddedilsin mi?`,
        description: "Sipariş taslağa döner; satış ekibi düzeltip yeniden onaya gönderebilir.",
        confirm: "Reddet",
        presets: null,
        noteLabel: "Ret sebebi",
        noteHint: "Zorunlu; zaman çizelgesinde görünür.",
      };
  }
}

/**
 * Reason dialog for reject (free text), cancel (preset + note, user-flows AF-04) and close-remaining (preset + note,
 * with the open quantities that will be closed). It says what happens to the reservation; nothing is sent until the
 * user confirms.
 */
export function ReasonDialog({
  action,
  orderNumber,
  holdsReservation,
  remaining = [],
  busy,
  error,
  onCancel,
  onConfirm,
}: {
  action: ReasonAction | null;
  orderNumber: string;
  holdsReservation: boolean;
  /** Lines with an open quantity (close-remaining shows them). */
  remaining?: readonly OrderLine[];
  busy: boolean;
  error: string | null;
  onCancel: () => void;
  onConfirm: (reason: string) => void;
}) {
  const [preset, setPreset] = useState("");
  const [note, setNote] = useState("");
  const [localError, setLocalError] = useState<string | null>(null);
  const open = action !== null;
  const text = textFor(action ?? "reject", orderNumber, holdsReservation);
  const withPreset = text.presets !== null;

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
    onConfirm(withPreset ? cancelReasonText(preset, note) : note.trim());
  };

  return (
    <Dialog
      open={open}
      onClose={close}
      role="alertdialog"
      dismissOnBackdrop={false}
      title={text.title}
      description={text.description}
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={close} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant={action === "cancel" ? "danger" : "primary"} size="lg" loading={busy} onClick={confirm}>
            {text.confirm}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {action === "close-remaining" && remaining.length > 0 ? (
          <div className="rounded-md border border-line bg-sunken p-3">
            <p className="text-caption font-medium text-fg">Kapatılacak kalan miktar</p>
            <ul aria-label="Kapatılacak kalan miktar" className="mt-1 flex flex-col gap-0.5 text-body text-fg">
              {remaining.map((l) => (
                <li key={l.id} className="flex justify-between gap-3">
                  <span className="min-w-0 truncate">
                    <span className="font-medium">{l.sku}</span> {l.name}
                  </span>
                  <span className="shrink-0 tabular-nums">
                    {formatQuantity(l.openQuantity)} {l.unit}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
        {withPreset ? (
          <Select
            label={action === "cancel" ? "İptal sebebi" : "Kapatma sebebi"}
            placeholder="Sebep seçin"
            options={(text.presets ?? []).map((r) => ({ value: r, label: r }))}
            value={preset}
            onChange={(e) => setPreset(e.target.value)}
          />
        ) : null}
        <Textarea
          label={text.noteLabel}
          hint={text.noteHint}
          value={note}
          maxLength={REASON_MAX_LENGTH}
          error={localError ?? error ?? undefined}
          onChange={(e) => setNote(e.target.value)}
        />
      </div>
    </Dialog>
  );
}

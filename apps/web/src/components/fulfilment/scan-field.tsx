"use client";

import { useState, type ReactNode, type RefObject } from "react";
import { CircleAlert, CircleCheck, Info, ScanBarcode } from "lucide-react";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/cn";

export type ScanFieldProps = {
  label: string;
  hint?: string;
  /** Called with the trimmed code on Enter (what a keyboard-wedge scanner sends); the field clears and keeps focus. */
  onScan: (code: string) => void;
  inputRef: RefObject<HTMLInputElement | null>;
  disabled?: boolean;
  id?: string;
};

/**
 * Scan-first input for warehouse screens (one hand, Bluetooth / keyboard-wedge scanner): 48 px, no autocorrect,
 * Enter submits, the field empties and keeps focus for the next scan. Typing a barcode or SKU works the same.
 * Unlike VariantScanner (catalog lookup), the code is sent as-is: the fulfilment API resolves and validates it.
 */
export function ScanField({ label, hint, onScan, inputRef, disabled, id }: ScanFieldProps) {
  const [value, setValue] = useState("");
  return (
    <Input
      id={id}
      ref={inputRef}
      label={label}
      hint={hint}
      size="lg"
      prefix={<ScanBarcode className="size-5" />}
      value={value}
      disabled={disabled}
      onChange={(e) => setValue(e.target.value)}
      onKeyDown={(e) => {
        if (e.key !== "Enter") return;
        e.preventDefault();
        const code = value.trim();
        if (!code) return;
        setValue("");
        onScan(code);
      }}
      autoComplete="off"
      autoCapitalize="characters"
      autoCorrect="off"
      spellCheck={false}
      enterKeyHint="go"
      inputMode="text"
    />
  );
}

export type Feedback = { tone: "success" | "error" | "info"; title: string; text?: string };

const feedbackTones = {
  success: { Icon: CircleCheck, className: "border-success-border bg-success-bg text-success-fg" },
  error: { Icon: CircleAlert, className: "border-danger-border bg-danger-bg text-danger-fg" },
  info: { Icon: Info, className: "border-info-border bg-info-bg text-info-fg" },
} as const;

/**
 * Large, colour + icon + text scan result (no sound needed). Always rendered so screen readers get every change
 * through the live region; errors are assertive.
 */
export function ScanFeedback({ feedback, actions, idleText }: { feedback: Feedback | null; actions?: ReactNode; idleText: string }) {
  const tone = feedback ? feedbackTones[feedback.tone] : null;
  const Icon = tone?.Icon ?? ScanBarcode;
  return (
    <div
      data-testid="scan-feedback"
      data-tone={feedback?.tone ?? "idle"}
      role={feedback?.tone === "error" ? "alert" : "status"}
      aria-live={feedback?.tone === "error" ? "assertive" : "polite"}
      className={cn(
        "flex min-h-16 items-start gap-3 rounded-lg border-2 px-4 py-3",
        tone?.className ?? "border-line bg-sunken text-fg-secondary",
      )}
    >
      <Icon className="mt-0.5 size-6 shrink-0" aria-hidden="true" />
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <p className="text-body-lg font-semibold break-words">{feedback?.title ?? idleText}</p>
        {feedback?.text ? <p className="text-body break-words">{feedback.text}</p> : null}
        {actions ? <div className="mt-1 flex flex-wrap gap-2">{actions}</div> : null}
      </div>
    </div>
  );
}

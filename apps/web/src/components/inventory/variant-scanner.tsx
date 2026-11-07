"use client";

import { useId, useRef, useState, type FormEvent, type RefObject } from "react";
import { ScanBarcode, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { findVariantByCode, searchVariants } from "@/lib/inventory/api";
import { inventoryErrorMessage } from "@/lib/inventory/errors";
import type { VariantRef } from "@/lib/inventory/receipt-form";
import { useSession } from "@/lib/session/session-context";

type Status =
  | { kind: "idle" }
  | { kind: "searching" }
  | { kind: "added"; text: string }
  | { kind: "choices"; term: string; items: VariantRef[] }
  | { kind: "none"; term: string }
  | { kind: "error"; message: string };

export type VariantScannerProps = {
  label: string;
  hint?: string;
  /** Called with the chosen variant; the input is cleared and keeps focus for the next scan. */
  onPick: (variant: VariantRef) => void;
  /** Message announced after a pick (e.g. "MOCK-001-1 eklendi (2)"). */
  pickedText?: (variant: VariantRef) => string;
  error?: string;
  inputRef?: RefObject<HTMLInputElement | null>;
  disabled?: boolean;
  id?: string;
};

/**
 * Barcode/SKU entry built for keyboard-wedge scanners: the scanner "types" the code fast and presses Enter.
 * Enter resolves an exact barcode, then an exact SKU; otherwise it falls back to a search (SKU/name contains)
 * and lists the matches as large buttons. Works the same with typing and a phone keyboard.
 */
export function VariantScanner({ label, hint, onPick, pickedText, error, inputRef, disabled, id }: VariantScannerProps) {
  const { api } = useSession();
  const [term, setTerm] = useState("");
  const [status, setStatus] = useState<Status>({ kind: "idle" });
  const resultsId = useId();
  const busy = useRef(false);
  const localRef = useRef<HTMLInputElement | null>(null);
  const ref = inputRef ?? localRef;

  /** `code` = what was looked up: the field is cleared only if it still holds it (not a code typed meanwhile). */
  const pick = (variant: VariantRef, code?: string) => {
    onPick(variant);
    setTerm((current) => (code === undefined || current.trim() === code ? "" : current));
    setStatus({ kind: "added", text: pickedText ? pickedText(variant) : `${variant.sku} seçildi.` });
    ref.current?.focus();
  };

  /** Resolves one code; picks it, or returns what to show instead (no match, several matches, error). */
  const lookup = async (code: string): Promise<Status | null> => {
    try {
      const exact = await findVariantByCode(api, code);
      if (exact) {
        pick({ id: exact.id, sku: exact.sku, name: exact.name ?? null, barcode: exact.barcode ?? null }, code);
        return null;
      }
      const found = await searchVariants(api, code, { limit: 10 });
      const items = found
        .filter((v) => v.status !== "archived")
        .map((v) => ({ id: v.id, sku: v.sku, name: v.name ?? null, barcode: v.barcode ?? null }));
      if (items.length === 1) {
        pick(items[0], code);
        return null;
      }
      return items.length === 0 ? { kind: "none", term: code } : { kind: "choices", term: code, items };
    } catch (err) {
      return { kind: "error", message: inventoryErrorMessage(err) };
    }
  };

  // A scanner can send the next code while the previous lookup is still running. It used to be dropped silently (a
  // missing receipt line; #136, seen as a flaky inventory test). Now it is queued (the field is cleared so the next
  // scan does not append to it) and resolved in order; the first problem (no/several matches, error) stays shown.
  const queue = useRef<string[]>([]);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const code = term.trim();
    if (!code) return;
    if (busy.current) {
      queue.current.push(code);
      setTerm("");
      return;
    }
    busy.current = true;
    let problem: Status | null = null;
    try {
      for (let next: string | undefined = code; next !== undefined; next = queue.current.shift()) {
        setStatus({ kind: "searching" });
        const result = await lookup(next);
        problem ??= result;
      }
    } finally {
      busy.current = false;
    }
    if (problem) setStatus(problem);
  };

  return (
    // Not a <form>: the scanner can sit inside a larger form; Enter is handled here and never submits the outer form.
    <div className="flex flex-col gap-2">
      <div className="flex items-start gap-2">
        <Input
          id={id}
          ref={ref}
          label={label}
          hint={hint}
          error={error}
          size="lg"
          className="flex-1"
          prefix={<ScanBarcode className="size-5" />}
          value={term}
          disabled={disabled}
          onChange={(e) => setTerm(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void submit(e);
          }}
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          enterKeyHint="search"
          aria-controls={status.kind === "choices" ? resultsId : undefined}
        />
        <Button
          size="lg"
          variant="secondary"
          // Aligned with the input below its label (label line + gap).
          className="mt-[1.625rem] h-12"
          icon={<Search className="size-5" />}
          loading={status.kind === "searching"}
          disabled={disabled || !term.trim()}
          onClick={(e) => void submit(e)}
          aria-label="Ürünü bul"
        >
          <span className="hidden sm:inline">Bul</span>
        </Button>
      </div>
      <p role="status" className="min-h-5 text-caption text-fg-muted">
        {status.kind === "searching"
          ? "Aranıyor…"
          : status.kind === "added"
            ? status.text
            : status.kind === "choices"
              ? `"${status.term}" için ${status.items.length} ürün bulundu; birini seçin.`
              : ""}
      </p>
      {status.kind === "none" ? (
        <p role="alert" className="text-body text-danger-fg">
          &quot;{status.term}&quot; ile eşleşen ürün bulunamadı. Barkodu veya SKU&apos;yu kontrol edin.
        </p>
      ) : null}
      {status.kind === "error" ? (
        <p role="alert" className="text-body text-danger-fg">
          {status.message}
        </p>
      ) : null}
      {status.kind === "choices" ? (
        <ul id={resultsId} aria-label="Bulunan ürünler" className="flex flex-col gap-2">
          {status.items.map((v) => (
            <li key={v.id}>
              <button
                type="button"
                onClick={() => pick(v, status.term)}
                className="flex min-h-12 w-full flex-col items-start justify-center rounded-md border border-line-strong bg-card px-4 py-2 text-left hover:bg-sunken"
              >
                <span className="font-mono text-body text-fg">{v.sku}</span>
                <span className="text-caption text-fg-muted">
                  {[v.name, v.barcode].filter(Boolean).join(" · ") || "Varyant adı yok"}
                </span>
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

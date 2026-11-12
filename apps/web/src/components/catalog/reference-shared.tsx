"use client";

import { useEffect, useState, type ReactNode } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { ArrowLeft, CircleAlert } from "@/components/ui/icons";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { ApiError } from "@/lib/api/client";
import { CATALOG_CONFLICTS, catalogErrorMessage, isVersionConflict, serverFieldErrors } from "@/lib/catalog/errors";
import { cn } from "@/lib/cn";

/** The catalog reference screens (#129): brands, categories, units — tabs under Ürünler. */
export const REFERENCE_TABS = [
  { href: "/urunler/markalar", label: "Markalar" },
  { href: "/urunler/kategoriler", label: "Kategoriler" },
  { href: "/urunler/birimler", label: "Birimler" },
] as const;

export const BRANDS_HREF = REFERENCE_TABS[0].href;
export const CATEGORIES_HREF = REFERENCE_TABS[1].href;
export const UNITS_HREF = REFERENCE_TABS[2].href;

/** Back link + tab navigation (links with aria-current, so every tab has its own URL). */
export function ReferenceNav() {
  const pathname = usePathname();
  return (
    <>
      <Link href="/urunler" className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
        <ArrowLeft className="size-4" aria-hidden="true" />
        Ürünler
      </Link>
      <nav aria-label="Katalog tanımları" className="border-b border-line">
        <ul className="-mb-px flex gap-1 overflow-x-auto">
          {REFERENCE_TABS.map((tab) => {
            const current = pathname === tab.href;
            return (
              <li key={tab.href}>
                <Link
                  href={tab.href}
                  aria-current={current ? "page" : undefined}
                  className={cn(
                    "inline-flex min-h-11 items-center border-b-2 px-4 text-body font-medium whitespace-nowrap",
                    current ? "border-brand text-brand-fg" : "border-transparent text-fg-secondary hover:text-fg",
                  )}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
    </>
  );
}

export type ListState<T> = { key: string; status: "ready"; items: T[] } | { key: string; status: "error"; message: string };

/**
 * Loads a reference list for `key` (filters + attempt). `current` is null while the list for this key is loading,
 * so stale rows never show under new filters.
 */
export function useReferenceList<T>(key: string, load: (signal: AbortSignal) => Promise<T[]>): ListState<T> | null {
  const [data, setData] = useState<ListState<T> | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal)
      .then((items) => {
        if (!controller.signal.aborted) setData({ key, status: "ready", items });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setData({ key, status: "error", message: catalogErrorMessage(error) });
      });
    return () => controller.abort();
    // `load` is rebuilt every render; the key carries everything it depends on.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return data?.key === key ? data : null;
}

export function ListError({ title, message, onRetry }: { title: string; message: string; onRetry: () => void }) {
  return (
    <section role="alert" className="rounded-xl border border-line bg-card shadow-e1">
      <EmptyState headingLevel="h2" icon={<CircleAlert className="size-6" />} title={title} description={message} action={<Button onClick={onRetry}>Tekrar dene</Button>} />
    </section>
  );
}

export function ArchivedToggle({ checked, onChange }: { checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <label className="inline-flex min-h-11 items-center gap-2 text-body text-fg">
      <input type="checkbox" className="size-4 accent-(--bos-brand-primary)" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      Arşivlenenleri göster
    </label>
  );
}

export type SaveProblem = { fields: Record<string, string>; summary: string[]; stale: boolean };

/**
 * A failed create / update → field messages (422 field errors; 409 name / code conflicts on `conflictField`) or a summary
 * line. 412 (someone else changed the record) is `stale`: the dialog asks to reload, nothing was saved.
 */
export function saveProblem(error: unknown, conflictField: string, known: ReadonlySet<string>): SaveProblem {
  if (isVersionConflict(error)) {
    return { fields: {}, summary: ["Bu kayıt siz düzenlerken başka biri tarafından değiştirildi. Pencereyi kapatıp listeyi yenileyin; değişikliğiniz kaydedilmedi."], stale: true };
  }
  if (error instanceof ApiError && error.status === 409 && error.code && CATALOG_CONFLICTS[error.code]) {
    return { fields: { [conflictField]: CATALOG_CONFLICTS[error.code] }, summary: [], stale: false };
  }
  if (error instanceof ApiError && error.status === 422 && Object.keys(error.fieldErrors).length > 0) {
    const fields: Record<string, string> = {};
    const summary: string[] = [];
    for (const [key, message] of Object.entries(serverFieldErrors(error))) {
      if (known.has(key)) fields[key] = message;
      else summary.push(message);
    }
    return { fields, summary, stale: false };
  }
  return { fields: {}, summary: [catalogErrorMessage(error)], stale: false };
}

/** Confirmation before archiving (DELETE with If-Match). `blocked` explains why it cannot be done and hides the confirm. */
export function ArchiveDialog({
  title,
  name,
  effect,
  blocked,
  onClose,
  onConfirm,
}: {
  title: string;
  name: string;
  effect: ReactNode;
  blocked?: string | null;
  onClose: () => void;
  onConfirm: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
    } catch (e) {
      setBusy(false);
      setError(
        isVersionConflict(e) ? "Bu kayıt siz bakarken değiştirildi. Pencereyi kapatıp listeyi yenileyin." : catalogErrorMessage(e),
      );
    }
  };

  return (
    <Dialog
      open
      onClose={() => !busy && onClose()}
      role="alertdialog"
      dismissOnBackdrop={false}
      size="sm"
      title={title}
      description={blocked ? `“${name}” şu an arşivlenemez.` : `“${name}” arşivlenecek.`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            {blocked ? "Tamam" : "Vazgeç"}
          </Button>
          {blocked ? null : (
            <Button variant="danger" onClick={confirm} loading={busy}>
              Arşivle
            </Button>
          )}
        </>
      }
    >
      {blocked ? <p className="text-warning-fg">{blocked}</p> : <div className="text-body text-fg">{effect}</div>}
      {error ? (
        <p role="alert" className="mt-3 text-danger-fg">
          {error}
        </p>
      ) : null}
    </Dialog>
  );
}

const dateFormat = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeZone: "Europe/Istanbul" });

export function formatUpdated(updatedAt: string | null | undefined, createdAt: string): string {
  return dateFormat.format(new Date(updatedAt ?? createdAt));
}

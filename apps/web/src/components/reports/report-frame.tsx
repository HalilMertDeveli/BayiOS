"use client";

import { useState, type FormEvent, type ReactNode } from "react";
import Link from "next/link";
import { ArrowLeft, CalendarRange, CircleAlert, Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { useWarehouses } from "@/components/inventory/use-inventory-data";
import { cn } from "@/lib/cn";
import { PRESETS, formatRange, matchPreset, presetRange, rangeError, type DateRange } from "@/lib/reports/date-range";
import { REPORTS_HREF, ordersOnly, reportAccess, type ReportDefinition } from "@/lib/reports/definitions";
import type { ExportFilters } from "@/lib/reports/api";
import { useSession } from "@/lib/session/session-context";
import { ExportButtons } from "./export-buttons";

function BackLink() {
  return (
    <Link href={REPORTS_HREF} className="inline-flex min-h-6 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Raporlar
    </Link>
  );
}

/**
 * Renders the report only for roles that may read it; others get an explanation and no request is made (the API would
 * answer 403 anyway). Wraps each report view so its data hooks never run without access.
 */
export function ReportGate({ report, children }: { report: ReportDefinition; children: ReactNode }) {
  const { access } = useSession();
  if (reportAccess(access, report).read) return <>{children}</>;
  const description = report.billing
    ? "AI kullanım raporunu işletme sahibi ve yöneticiler görebilir."
    : ordersOnly(access)
      ? "Rolünüz yalnızca sipariş raporlarını görebilir. Diğer raporlar için işletme yöneticinizle görüşün."
      : "Rolünüz raporları görmeye izin vermiyor. Gerekirse işletme yöneticinizle görüşün.";
  return (
    <>
      <BackLink />
      <PageHeader title={report.title} />
      <section aria-label={`${report.title} durumu`} className="rounded-lg border border-line bg-card shadow-e1">
        <EmptyState
          icon={<Lock className="size-6" />}
          title="Bu raporu görme yetkiniz yok"
          description={description}
          action={
            <Link href={REPORTS_HREF} className="inline-flex min-h-6 items-center text-body text-brand-fg underline underline-offset-2">
              Raporlara dön
            </Link>
          }
        />
      </section>
    </>
  );
}

/**
 * The frame of every report page (inside ReportGate): back link, title, export buttons (when the role may export), the
 * filter bar and the content.
 */
export function ReportFrame({
  report,
  filters,
  exportFilters,
  today,
  children,
}: {
  report: ReportDefinition;
  filters: ReactNode;
  /** Body of the export request; null disables export (invalid filters). */
  exportFilters: ExportFilters | null;
  today: string;
  children: ReactNode;
}) {
  const { access } = useSession();
  const allowed = reportAccess(access, report);
  return (
    <>
      <BackLink />
      <PageHeader
        title={report.title}
        description={report.description}
        actions={allowed.export ? <ExportButtons report={report.id} filters={exportFilters} today={today} /> : undefined}
      />
      <section aria-label="Filtreler" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1">
        {filters}
      </section>
      {children}
    </>
  );
}

/** Preset buttons (Bugün, Son 7 gün, Son 30 gün, Bu ay) + a custom from/to form. */
export function DateRangeFilter({ range, today, onChange }: { range: DateRange; today: string; onChange: (range: DateRange) => void }) {
  const active = matchPreset(range, today);
  const [draft, setDraft] = useState<DateRange>(range);
  const [shown, setShown] = useState<DateRange>(range);
  const [error, setError] = useState<string | null>(null);
  // Keep the custom fields in step with presets / the URL (adjusting state during render, not in an effect).
  if (shown.from !== range.from || shown.to !== range.to) {
    setShown(range);
    setDraft(range);
    setError(null);
  }

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const invalid = rangeError(draft);
    setError(invalid);
    if (!invalid) onChange(draft);
  };

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Hazır tarih aralıkları">
        <CalendarRange className="size-5 text-fg-muted" aria-hidden="true" />
        {PRESETS.map((preset) => (
          <Button
            key={preset.id}
            size="sm"
            variant={active === preset.id ? "primary" : "secondary"}
            aria-pressed={active === preset.id}
            onClick={() => onChange(presetRange(preset.id, today))}
          >
            {preset.label}
          </Button>
        ))}
      </div>
      <form noValidate onSubmit={apply} aria-label="Özel tarih aralığı" className="flex flex-wrap items-end gap-3">
        <Input
          type="date"
          label="Başlangıç"
          value={draft.from}
          max={today}
          className="w-44"
          onChange={(e) => setDraft((d) => ({ ...d, from: e.target.value }))}
        />
        <Input type="date" label="Bitiş" value={draft.to} max={today} className="w-44" onChange={(e) => setDraft((d) => ({ ...d, to: e.target.value }))} />
        <Button type="submit" variant="secondary">
          Uygula
        </Button>
        <p className="w-full text-caption text-fg-muted sm:w-auto sm:self-center">
          Seçili aralık: {formatRange(range)} · en fazla 366 gün
        </p>
      </form>
      {error ? (
        <p role="alert" className="text-body text-danger-fg">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** "Tüm depolar" + the tenant's warehouses (inactive ones marked). Falls back to "Tüm depolar" only if they cannot load. */
export function WarehouseFilter({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const { state } = useWarehouses();
  const warehouses = state.status === "ready" ? state.data : [];
  const options = [
    { value: "", label: "Tüm depolar" },
    ...warehouses.map((w) => ({ value: w.id, label: w.status === "active" ? w.name : `${w.name} (pasif)` })),
  ];
  if (value && !warehouses.some((w) => w.id === value)) options.push({ value, label: state.status === "loading" ? "Yükleniyor…" : "Seçili depo" });
  return (
    <Select
      label="Depo"
      className="w-56"
      value={value}
      options={options}
      hint={state.status === "error" ? "Depolar yüklenemedi; tüm depolar gösteriliyor." : undefined}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

/** Report-level error with retry; stays on the page. */
export function ReportError({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
      <EmptyState
        headingLevel="h2"
        icon={<CircleAlert className="size-6" />}
        title="Rapor yüklenemedi"
        description={message}
        action={<Button onClick={onRetry}>Tekrar dene</Button>}
      />
    </div>
  );
}

/** Shown instead of the report while the range is invalid (nothing is requested). */
export function InvalidRange({ message }: { message: string }) {
  return (
    <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
      <EmptyState headingLevel="h2" icon={<CalendarRange className="size-6" />} title="Tarih aralığını düzeltin" description={message} />
    </div>
  );
}

export type Stat = { label: string; value: string; hint?: string };

/** Key figures as a description list (screen readers read label + value pairs). */
export function StatList({ label, stats, loading = false }: { label: string; stats: readonly Stat[]; loading?: boolean }) {
  return (
    <section aria-label={label} aria-busy={loading || undefined}>
      <dl className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
        {stats.map((s) => (
          <div key={s.label} className="flex flex-col gap-1 rounded-lg border border-line bg-card p-4 shadow-e1">
            <dt className="text-caption text-fg-muted">{s.label}</dt>
            <dd className={cn("text-h2 text-fg tabular-nums", loading && "animate-pulse text-fg-muted")}>{loading ? "…" : s.value}</dd>
            {s.hint ? <dd className="text-caption text-fg-muted">{s.hint}</dd> : null}
          </div>
        ))}
      </dl>
    </section>
  );
}

/** A titled card around a table / chart. */
export function ReportSection({ id, title, description, children }: { id: string; title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-3">
      <div className="flex flex-col gap-1">
        <h2 id={id} className="text-h3 text-fg">
          {title}
        </h2>
        {description ? <p className="text-caption text-fg-muted">{description}</p> : null}
      </div>
      {children}
    </section>
  );
}

export const count = (value: number | string | null | undefined): string => {
  if (value === null || value === undefined || value === "") return "—";
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n.toLocaleString("tr-TR") : String(value);
};

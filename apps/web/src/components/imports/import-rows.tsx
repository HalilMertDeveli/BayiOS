"use client";

import { useEffect, useMemo, useState } from "react";
import { CircleAlert } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Table, type TableColumn } from "@/components/ui/table";
import { listImportRows, num, type ImportRow, type RowFilter } from "@/lib/imports/api";
import { importErrorMessage } from "@/lib/imports/errors";
import { formatCount, rollbackStatusMeta, rowActionMeta, rowOutcomeLabels } from "@/lib/imports/labels";
import { useSession } from "@/lib/session/session-context";
import { cn } from "@/lib/cn";

export type RowFilterOption = { id: string; label: string; filter: RowFilter; count?: number };

type RowsState = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; rows: ImportRow[]; nextCursor: string | null };

/** Values worth showing in a row summary, in this order (the rest is in the error report). */
const SUMMARY_FIELDS: [string, string][] = [
  ["sku", "SKU"],
  ["barcode", "Barkod"],
  ["name", "Ad"],
  ["price", "Fiyat"],
  ["minQuantity", "Min. miktar"],
];

function rowSummary(row: ImportRow): string {
  const parts = SUMMARY_FIELDS.filter(([k]) => row.values[k]).map(([k, label]) => `${label}: ${row.values[k]}`);
  return parts.length ? parts.join(" · ") : (row.raw.filter(Boolean).slice(0, 3).join(" · ") || "—");
}

function RowErrors({ row }: { row: ImportRow }) {
  if (row.errors.length === 0) return <span className="text-fg-muted">—</span>;
  return (
    <ul className="flex flex-col gap-1">
      {row.errors.map((e, i) => (
        <li key={`${e.code}-${i}`} className="text-caption text-danger-fg">
          {e.column ? <span className="font-semibold">{e.column}: </span> : null}
          {e.message}
        </li>
      ))}
    </ul>
  );
}

function RowResult({ row }: { row: ImportRow }) {
  if (row.rollbackStatus) {
    const meta = rollbackStatusMeta[row.rollbackStatus];
    return <Badge tone={meta?.tone ?? "neutral"}>{meta?.label ?? row.rollbackStatus}</Badge>;
  }
  if (row.outcome) return <span>{rowOutcomeLabels[row.outcome] ?? row.outcome}</span>;
  return <span className="text-fg-muted">—</span>;
}

/**
 * Rows of an import, paged on the server (keyset `cursor`, ROW_PAGE_SIZE per page) and filterable (all / errors / by
 * action / rollback conflicts). "Önceki" walks back through the cursors already seen.
 */
export function ImportRows({
  jobId,
  options,
  initial,
  version,
  caption,
}: {
  jobId: string;
  options: RowFilterOption[];
  initial?: string;
  /** Reloads the first page when it changes (e.g. after validation finished). */
  version: string;
  caption: string;
}) {
  const { api } = useSession();
  const [filterId, setFilterId] = useState(initial ?? options[0]?.id ?? "all");
  // cursors[i] = cursor of page i (page 0 = null).
  const [cursors, setCursors] = useState<(string | null)[]>([null]);
  const [state, setState] = useState<{ key: string; value: RowsState } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const option = options.find((o) => o.id === filterId) ?? options[0];
  const pageIndex = cursors.length - 1;
  const cursor = cursors[pageIndex];
  // The filter as a string: parents rebuild `options` on every render, the request must not follow object identity.
  const filterJson = JSON.stringify(option?.filter ?? {});
  const key = `${jobId}|${version}|${filterJson}|${cursor ?? ""}|${attempt}`;

  useEffect(() => {
    const controller = new AbortController();
    listImportRows(api, jobId, JSON.parse(filterJson) as RowFilter, { cursor, signal: controller.signal })
      .then((page) => {
        if (!controller.signal.aborted)
          setState({ key, value: { status: "ready", rows: page.items, nextCursor: page.page.hasMore ? page.page.nextCursor : null } });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ key, value: { status: "error", message: importErrorMessage(error) } });
      });
    return () => controller.abort();
  }, [api, jobId, filterJson, cursor, key]);

  const current: RowsState = state?.key === key ? state.value : { status: "loading" };
  const rows = current.status === "ready" ? current.rows : [];

  const columns: TableColumn<ImportRow>[] = useMemo(
    () => [
      { key: "row", header: "Satır", width: "5rem", numeric: true, cell: (r) => num(r.rowNumber) },
      {
        key: "action",
        header: "İşlem",
        width: "9rem",
        cell: (r) => <Badge tone={rowActionMeta[r.action]?.tone ?? "neutral"}>{rowActionMeta[r.action]?.label ?? r.action}</Badge>,
      },
      { key: "values", header: "Değerler", cell: (r) => <span className="text-caption break-words text-fg-secondary">{rowSummary(r)}</span> },
      { key: "errors", header: "Hata / not", cell: (r) => <RowErrors row={r} /> },
      { key: "result", header: "Sonuç", width: "11rem", cell: (r) => <RowResult row={r} /> },
    ],
    [],
  );

  const selectFilter = (id: string) => {
    setFilterId(id);
    setCursors([null]);
  };

  return (
    <div className="flex flex-col gap-3">
      <div role="group" aria-label="Satır filtresi" className="flex flex-wrap gap-2">
        {options.map((o) => (
          <button
            key={o.id}
            type="button"
            aria-pressed={o.id === option?.id}
            onClick={() => selectFilter(o.id)}
            className={cn(
              "inline-flex min-h-9 items-center gap-1 rounded-full border px-3 text-body",
              o.id === option?.id ? "border-brand bg-brand-bg font-medium text-brand-fg" : "border-line-strong bg-card text-fg hover:bg-sunken",
            )}
          >
            {o.label}
            {o.count !== undefined ? <span className="tabular-nums">({formatCount(o.count)})</span> : null}
          </button>
        ))}
      </div>

      {current.status === "error" ? (
        <div role="alert" className="rounded-lg border border-line bg-card">
          <EmptyState
            headingLevel="h3"
            icon={<CircleAlert className="size-6" />}
            title="Satırlar yüklenemedi"
            description={current.message}
            action={<Button onClick={() => setAttempt((n) => n + 1)}>Tekrar dene</Button>}
          />
        </div>
      ) : (
        <>
          <div className="hidden md:block">
            <Table
              caption={caption}
              columns={columns}
              rows={rows}
              getRowId={(r) => String(r.rowNumber)}
              density="compact"
              loading={current.status === "loading"}
              loadingRows={4}
              maxHeight="32rem"
              empty={<EmptyState headingLevel="h3" title="Bu filtrede satır yok" description="Başka bir filtre seçin." />}
            />
          </div>
          <ul aria-label={caption} className="flex flex-col gap-2 md:hidden">
            {current.status === "loading" ? <li className="py-6 text-center text-body text-fg-muted">Yükleniyor…</li> : null}
            {current.status === "ready" && rows.length === 0 ? (
              <li className="rounded-lg border border-line bg-card p-4 text-body text-fg-muted">Bu filtrede satır yok.</li>
            ) : null}
            {rows.map((r) => (
              <li key={String(r.rowNumber)} className="flex flex-col gap-1 rounded-lg border border-line bg-card p-3">
                <div className="flex items-center justify-between gap-2">
                  <span className="text-body font-medium text-fg">Satır {num(r.rowNumber)}</span>
                  <Badge tone={rowActionMeta[r.action]?.tone ?? "neutral"}>{rowActionMeta[r.action]?.label ?? r.action}</Badge>
                </div>
                <p className="text-caption break-words text-fg-secondary">{rowSummary(r)}</p>
                <RowErrors row={r} />
                {r.outcome || r.rollbackStatus ? (
                  <div className="text-caption">
                    <RowResult row={r} />
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        </>
      )}

      <nav aria-label="Satır sayfaları" className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-caption text-fg-muted" aria-live="polite">
          {current.status === "ready" && rows.length > 0 ? `Sayfa ${pageIndex + 1} · ${formatCount(rows.length)} satır gösteriliyor` : ""}
        </p>
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" disabled={pageIndex === 0 || current.status === "loading"} onClick={() => setCursors((c) => c.slice(0, -1))}>
            Önceki
          </Button>
          <Button
            variant="secondary"
            size="sm"
            disabled={current.status !== "ready" || !current.nextCursor}
            onClick={() => current.status === "ready" && current.nextCursor && setCursors((c) => [...c, current.nextCursor])}
          >
            Sonraki
          </Button>
        </div>
      </nav>
    </div>
  );
}

import type { ReactNode } from "react";
import { cn } from "@/lib/cn";
import { EmptyState } from "./empty-state";

export type TableColumn<T> = {
  /** Stable key for the column. */
  key: string;
  header: ReactNode;
  cell: (row: T) => ReactNode;
  /** Right-aligns and uses tabular numbers (quantities, prices). */
  numeric?: boolean;
  /** Optional CSS width, e.g. "8rem". */
  width?: string;
  /** Keeps the cell on one line (codes, dates, short labels); numeric columns never wrap. */
  nowrap?: boolean;
};

export type TableDensity = "comfortable" | "compact";

export type TableProps<T> = {
  /** Accessible name of the table; rendered as <caption> (visually hidden unless showCaption). */
  caption: string;
  showCaption?: boolean;
  columns: readonly TableColumn<T>[];
  rows: readonly T[];
  getRowId: (row: T) => string;
  density?: TableDensity;
  /** While true, the body shows skeleton rows and the table is aria-busy. */
  loading?: boolean;
  /** Number of skeleton rows while loading. */
  loadingRows?: number;
  /** Rendered when not loading and rows is empty. */
  empty?: ReactNode;
  /**
   * Max height of the scroll area (CSS length). The header stays sticky inside it.
   * Large lists are expected to be paginated server-side; this component renders what it is given.
   */
  maxHeight?: string;
  className?: string;
};

const cellPadding: Record<TableDensity, string> = {
  comfortable: "px-4 py-3",
  compact: "px-3 py-1.5",
};

export function Table<T>({
  caption,
  showCaption = false,
  columns,
  rows,
  getRowId,
  density = "comfortable",
  loading = false,
  loadingRows = 5,
  empty,
  maxHeight,
  className,
}: TableProps<T>) {
  const pad = cellPadding[density];
  const isEmpty = !loading && rows.length === 0;

  return (
    // Focusable scroll region so keyboard users can scroll wide/tall tables (WCAG 2.1.1).
    <div
      role="region"
      aria-label={caption}
      tabIndex={0}
      className={cn("overflow-auto rounded-xl border border-line bg-card shadow-e1", className)}
      style={maxHeight ? { maxHeight } : undefined}
    >
      <table className="w-full border-separate border-spacing-0 text-left text-body" aria-busy={loading || undefined}>
        <caption className={showCaption ? "px-4 py-3 text-left text-h3 text-fg" : "sr-only"}>{caption}</caption>
        <thead>
          <tr>
            {columns.map((col) => (
              <th
                key={col.key}
                scope="col"
                style={col.width ? { width: col.width } : undefined}
                className={cn(
                  // Sticky header: stays visible while scrolling inside the region.
                  "sticky top-0 z-(--bos-z-sticky) border-b border-line bg-sunken text-body font-medium whitespace-nowrap text-fg-secondary",
                  pad,
                  col.numeric && "text-right",
                )}
              >
                {col.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="[&>tr:last-child>td]:border-b-0">
          {loading
            ? Array.from({ length: loadingRows }, (_, i) => (
                <tr key={`skeleton-${i}`} aria-hidden="true">
                  {columns.map((col) => (
                    <td key={col.key} className={cn("border-b border-line", pad)}>
                      <span className="block h-4 w-3/4 animate-pulse rounded-sm bg-sunken" />
                    </td>
                  ))}
                </tr>
              ))
            : null}
          {loading ? (
            <tr>
              <td colSpan={columns.length} className="sr-only">
                Yükleniyor…
              </td>
            </tr>
          ) : null}
          {isEmpty ? (
            <tr>
              <td colSpan={columns.length}>
                {empty ?? <EmptyState headingLevel="h3" title="Kayıt yok" description="Gösterilecek kayıt bulunamadı." />}
              </td>
            </tr>
          ) : null}
          {!loading &&
            rows.map((row) => (
              <tr key={getRowId(row)} className="transition-colors hover:bg-brand-subtle">
                {columns.map((col) => (
                  <td
                    key={col.key}
                    className={cn("border-b border-line align-middle text-fg", pad, col.numeric && "text-right whitespace-nowrap tabular-nums", col.nowrap && "whitespace-nowrap")}
                  >
                    {col.cell(row)}
                  </td>
                ))}
              </tr>
            ))}
        </tbody>
      </table>
    </div>
  );
}

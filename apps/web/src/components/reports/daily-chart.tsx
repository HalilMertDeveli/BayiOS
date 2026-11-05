import { useId } from "react";
import { formatDay, formatShortDay } from "@/lib/reports/date-range";

export type DailyPoint = { day: string; value: number };

/**
 * Days of the range with the summed values; days without data are 0 (a gap in the data is a real zero for counts).
 * Pure: exported for tests.
 */
export function fillDays(days: readonly string[], values: readonly { day: string; value: number }[]): DailyPoint[] {
  const sums = new Map<string, number>();
  for (const v of values) sums.set(v.day, (sums.get(v.day) ?? 0) + (Number.isFinite(v.value) ? v.value : 0));
  return days.map((day) => ({ day, value: sums.get(day) ?? 0 }));
}

/** Summary sentence for the chart's accessible name: total, busiest day. */
export function chartSummary(points: readonly DailyPoint[], label: string): string {
  const total = points.reduce((sum, p) => sum + p.value, 0);
  if (points.length === 0 || total === 0) return `${label}: seçilen aralıkta kayıt yok.`;
  const peak = points.reduce((best, p) => (p.value > best.value ? p : best), points[0]);
  return `${label}: ${points.length} günde toplam ${total.toLocaleString("tr-TR")}; en yüksek ${peak.value.toLocaleString("tr-TR")} (${formatDay(peak.day)}).`;
}

/**
 * Single-series daily column chart in plain CSS (no chart library): one brand-colour hue that follows the theme, a
 * zero baseline, the maximum on the axis, first/last day labels. Decorative detail is hidden from assistive technology;
 * the figure carries a summary as its accessible name and the page shows the same numbers in a table.
 */
export function DailyChart({ title, label, points }: { title: string; label: string; points: readonly DailyPoint[] }) {
  const max = points.reduce((m, p) => Math.max(m, p.value), 0);
  const summary = chartSummary(points, label);
  const summaryId = useId();
  return (
    <figure className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
      <figcaption className="text-body font-medium text-fg">{title}</figcaption>
      <div role="img" aria-label={`${title} grafiği`} aria-describedby={summaryId} className="flex gap-2">
        <div className="flex h-40 flex-col justify-between text-right text-caption text-fg-muted tabular-nums" aria-hidden="true">
          <span>{max.toLocaleString("tr-TR")}</span>
          <span>0</span>
        </div>
        <div className="flex min-w-0 flex-1 flex-col gap-1" aria-hidden="true">
          <div className="flex h-40 items-end gap-px border-b border-line-strong">
            {points.map((p) => (
              <div
                key={p.day}
                title={`${formatDay(p.day)}: ${p.value.toLocaleString("tr-TR")}`}
                className="min-w-0.5 flex-1 rounded-t-sm bg-brand"
                style={{ height: max > 0 && p.value > 0 ? `max(2px, ${(p.value / max) * 100}%)` : "0" }}
              />
            ))}
          </div>
          {points.length > 0 ? (
            <div className="flex justify-between text-caption text-fg-muted">
              <span>{formatShortDay(points[0].day)}</span>
              {points.length > 1 ? <span>{formatShortDay(points[points.length - 1].day)}</span> : null}
            </div>
          ) : null}
        </div>
      </div>
      <p id={summaryId} className="text-caption text-fg-muted">
        {summary}
      </p>
    </figure>
  );
}

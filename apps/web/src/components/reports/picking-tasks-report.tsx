"use client";

import { useId } from "react";
import { ClipboardList } from "@/components/ui/icons";
import { EmptyState } from "@/components/ui/empty-state";
import { Table, type TableColumn } from "@/components/ui/table";
import { getPickingTasks, type PickingTasksReport } from "@/lib/reports/api";
import { formatDay, listDays } from "@/lib/reports/date-range";
import { reportDefinition } from "@/lib/reports/definitions";
import { useSession } from "@/lib/session/session-context";
import { DailyChart, fillDays } from "./daily-chart";
import { DateRangeFilter, InvalidRange, ReportError, ReportFrame, ReportGate, ReportSection, StatList, WarehouseFilter, count } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

type DayRow = PickingTasksReport["days"][number];

const REPORT = reportDefinition("picking-tasks");

const minutesFormat = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 1 });

/** "12,5 dk" or "—" when no task was completed. */
export function formatMinutes(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === "") return "—";
  const n = Number(value);
  return Number.isFinite(n) ? `${minutesFormat.format(n)} dk` : "—";
}

/** REP-05 (tenant level): picking tasks by their own event day; average picking time. No per-person breakdown. */
export function PickingTasksReportView() {
  return (
    <ReportGate report={REPORT}>
      <PickingTasksReportBody />
    </ReportGate>
  );
}

function PickingTasksReportBody() {
  const uid = useId();
  const { api } = useSession();
  const { today, range, rangeError, params, update, setRange } = useReportUrl();
  const warehouseId = params.get("depo") ?? "";
  const key = rangeError ? null : JSON.stringify([range, warehouseId]);
  const { state, reload } = useReport(key, (signal) => getPickingTasks(api, range, warehouseId, signal));
  const report = state.status === "ready" ? state.data : null;

  const filters = (
    <>
      <DateRangeFilter range={range} today={today} onChange={setRange} />
      <WarehouseFilter value={warehouseId} onChange={(id) => update({ depo: id })} />
    </>
  );
  const exportFilters = rangeError ? null : { from: range.from, to: range.to, ...(warehouseId ? { warehouseId } : {}) };
  const totals = report?.totals;
  const empty = totals && Number(totals.created) + Number(totals.completed) + Number(totals.cancelled) === 0;

  const columns: TableColumn<DayRow>[] = [
    { key: "day", header: "Gün", width: "9rem", cell: (r) => formatDay(r.day) },
    { key: "created", header: "Oluşturulan", numeric: true, cell: (r) => count(r.created) },
    { key: "completed", header: "Tamamlanan", numeric: true, cell: (r) => count(r.completed) },
    { key: "short", header: "Eksikle tamamlanan", numeric: true, cell: (r) => count(r.completedWithShortage) },
    { key: "cancelled", header: "İptal", numeric: true, cell: (r) => count(r.cancelled) },
    { key: "avg", header: "Ort. toplama süresi", numeric: true, cell: (r) => formatMinutes(r.averagePickMinutes) },
  ];

  return (
    <ReportFrame report={REPORT} filters={filters} exportFilters={exportFilters} today={today}>
      {rangeError ? (
        <InvalidRange message={rangeError} />
      ) : state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : empty ? (
        <div className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState icon={<ClipboardList className="size-6" />} title="Bu aralıkta toplama görevi yok" description="Seçilen günlerde ve depoda oluşturulan, tamamlanan veya iptal edilen görev bulunamadı." />
        </div>
      ) : (
        <>
          <StatList
            label="Toplama görevi özeti"
            loading={!totals}
            stats={[
              { label: "Oluşturulan", value: count(totals?.created) },
              { label: "Tamamlanan", value: count(totals?.completed) },
              { label: "Eksikle tamamlanan", value: count(totals?.completedWithShortage) },
              { label: "İptal", value: count(totals?.cancelled) },
              { label: "Ort. toplama süresi", value: formatMinutes(totals?.averagePickMinutes), hint: "Başlatmadan tamamlamaya" },
            ]}
          />
          {report ? (
            <DailyChart
              title="Günlük tamamlanan görev"
              label="Günlük tamamlanan görev"
              points={fillDays(listDays(report), report.days.map((d) => ({ day: d.day, value: Number(d.completed) })))}
            />
          ) : null}
          <ReportSection id={`${uid}-picking-by-day`} title="Günlük döküm" description="Her sayı kendi olayının gününe yazılır (oluşturma, tamamlama, iptal).">
            <Table caption="Günlük toplama görevleri" columns={columns} rows={report?.days ?? []} getRowId={(r) => r.day} loading={!report} loadingRows={3} maxHeight="28rem" density="compact" />
          </ReportSection>
        </>
      )}
    </ReportFrame>
  );
}

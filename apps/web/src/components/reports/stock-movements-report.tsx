"use client";

import { useId } from "react";
import { ArrowLeftRight } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { EmptyState } from "@/components/ui/empty-state";
import { Table, type TableColumn } from "@/components/ui/table";
import { movementTypeLabel, movementTypeTone } from "@/lib/inventory/labels";
import { formatQuantity } from "@/lib/pricing/money";
import { getStockMovements, type StockMovementsReport } from "@/lib/reports/api";
import { formatDay } from "@/lib/reports/date-range";
import { reportDefinition } from "@/lib/reports/definitions";
import { useSession } from "@/lib/session/session-context";
import { DateRangeFilter, InvalidRange, ReportError, ReportFrame, ReportGate, ReportSection, WarehouseFilter, count } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

type TypeRow = StockMovementsReport["totals"][number];
type DayRow = StockMovementsReport["days"][number];

const REPORT = reportDefinition("stock-movements");

const qty = (value: number | string) => (Number(value) === 0 ? "—" : formatQuantity(value));

/** REP-07: ledger movements by day × type — count, quantity in, quantity out (no cost). */
export function StockMovementsReportView() {
  return (
    <ReportGate report={REPORT}>
      <StockMovementsReportBody />
    </ReportGate>
  );
}

function StockMovementsReportBody() {
  const uid = useId();
  const { api } = useSession();
  const { today, range, rangeError, params, update, setRange } = useReportUrl();
  const warehouseId = params.get("depo") ?? "";
  const key = rangeError ? null : JSON.stringify([range, warehouseId]);
  const { state, reload } = useReport(key, (signal) => getStockMovements(api, range, warehouseId, signal));

  const filters = (
    <>
      <DateRangeFilter range={range} today={today} onChange={setRange} />
      <WarehouseFilter value={warehouseId} onChange={(id) => update({ depo: id })} />
    </>
  );
  const exportFilters = rangeError ? null : { from: range.from, to: range.to, ...(warehouseId ? { warehouseId } : {}) };
  const report = state.status === "ready" ? state.data : null;

  const typeCell = (type: string) => <Badge tone={movementTypeTone(type)}>{movementTypeLabel(type)}</Badge>;
  const typeColumns: TableColumn<TypeRow>[] = [
    { key: "type", header: "Hareket tipi", cell: (r) => typeCell(r.type) },
    { key: "count", header: "Hareket", numeric: true, width: "8rem", cell: (r) => count(r.movementCount) },
    { key: "in", header: "Giriş", numeric: true, width: "9rem", cell: (r) => qty(r.quantityIn) },
    { key: "out", header: "Çıkış", numeric: true, width: "9rem", cell: (r) => qty(r.quantityOut) },
  ];
  const dayColumns: TableColumn<DayRow>[] = [
    { key: "day", header: "Gün", width: "9rem", cell: (r) => formatDay(r.day) },
    ...typeColumns,
  ];

  return (
    <ReportFrame report={REPORT} filters={filters} exportFilters={exportFilters} today={today}>
      {rangeError ? (
        <InvalidRange message={rangeError} />
      ) : state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : report && report.totals.length === 0 ? (
        <div className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<ArrowLeftRight className="size-6" />}
            title="Bu aralıkta stok hareketi yok"
            description="Seçilen günlerde ve depoda mal kabul, düzeltme veya sevkiyat kaydı bulunamadı."
          />
        </div>
      ) : (
        <>
          <p className="text-caption text-fg-muted">Miktarlar ürünlerin kendi birimlerindedir; farklı birimler toplamda birlikte sayılır. Maliyet gösterilmez.</p>
          <ReportSection id={`${uid}-movements-by-type`} title="Hareket tipine göre">
            <Table caption="Hareket tipine göre toplamlar" columns={typeColumns} rows={report?.totals ?? []} getRowId={(r) => r.type} loading={!report} loadingRows={3} />
          </ReportSection>
          <ReportSection id={`${uid}-movements-by-day`} title="Günlük döküm">
            <Table
              caption="Günlük stok hareketleri"
              columns={dayColumns}
              rows={report?.days ?? []}
              getRowId={(r) => `${r.day}|${r.type}`}
              loading={!report}
              loadingRows={3}
              maxHeight="28rem"
              density="compact"
            />
          </ReportSection>
        </>
      )}
    </ReportFrame>
  );
}

"use client";

import { useId } from "react";
import { ShoppingCart } from "@/components/ui/icons";
import { OrderStatus } from "@/components/orders/order-badges";
import { EmptyState } from "@/components/ui/empty-state";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { ORDER_STATUS_FILTERS } from "@/lib/orders/list-query";
import { getOrdersReport, type OrdersReport } from "@/lib/reports/api";
import { formatDay, listDays } from "@/lib/reports/date-range";
import { reportDefinition } from "@/lib/reports/definitions";
import { visibleMoney } from "@/lib/reports/errors";
import { useSession } from "@/lib/session/session-context";
import { DailyChart, fillDays } from "./daily-chart";
import { DateRangeFilter, InvalidRange, ReportError, ReportFrame, ReportGate, ReportSection, StatList, count } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

type StatusRow = OrdersReport["byStatus"][number];
type DayRow = OrdersReport["days"][number];

const REPORT = reportDefinition("orders");

/** REP-04: orders created in the range × current status × currency; totals only with amountsVisible. */
export function OrdersReportView() {
  return (
    <ReportGate report={REPORT}>
      <OrdersReportBody />
    </ReportGate>
  );
}

function OrdersReportBody() {
  const { api } = useSession();
  const { today, range, rangeError, params, update, setRange } = useReportUrl();
  const statusValue = ORDER_STATUS_FILTERS.some((f) => f.value === params.get("durum")) ? (params.get("durum") ?? "") : "";
  const statuses = ORDER_STATUS_FILTERS.find((f) => f.value === statusValue)?.statuses ?? [];
  const key = rangeError ? null : JSON.stringify([range, statuses]);
  const { state, reload } = useReport(key, (signal) => getOrdersReport(api, range, statuses, signal));

  const filters = (
    <>
      <DateRangeFilter range={range} today={today} onChange={setRange} />
      <Select
        label="Sipariş durumu"
        className="w-64"
        value={statusValue}
        options={ORDER_STATUS_FILTERS.map((f) => ({ value: f.value, label: f.label }))}
        onChange={(e) => update({ durum: e.target.value })}
      />
    </>
  );
  const exportFilters = rangeError ? null : { from: range.from, to: range.to, ...(statuses.length > 0 ? { statuses: [...statuses] } : {}) };

  return (
    <ReportFrame report={REPORT} filters={filters} exportFilters={exportFilters} today={today}>
      {rangeError ? (
        <InvalidRange message={rangeError} />
      ) : state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : (
        <OrdersContent report={state.status === "ready" ? state.data : null} />
      )}
    </ReportFrame>
  );
}

function OrdersContent({ report }: { report: OrdersReport | null }) {
  const uid = useId();
  const loading = report === null;
  const amounts = report?.amountsVisible ?? false;
  const rows = report?.byStatus ?? [];
  const totalOrders = rows.reduce((sum, r) => sum + Number(r.orderCount), 0);
  const points = report ? fillDays(listDays(report), report.days.map((d) => ({ day: d.day, value: Number(d.orderCount) }))) : [];

  const statusColumns: TableColumn<StatusRow>[] = [
    { key: "status", header: "Durum", cell: (r) => <OrderStatus status={r.status} /> },
    { key: "currency", header: "Para birimi", width: "8rem", cell: (r) => r.currency },
    { key: "count", header: "Sipariş", numeric: true, width: "8rem", cell: (r) => count(r.orderCount) },
    ...(amounts ? [{ key: "total", header: "Genel toplam", numeric: true, width: "11rem", cell: (r: StatusRow) => visibleMoney(true, r.grandTotal, r.currency) }] : []),
  ];
  const dayColumns: TableColumn<DayRow>[] = [
    { key: "day", header: "Gün", width: "9rem", cell: (r) => formatDay(r.day) },
    { key: "status", header: "Durum", cell: (r) => <OrderStatus status={r.status} /> },
    { key: "currency", header: "Para birimi", width: "8rem", cell: (r) => r.currency },
    { key: "count", header: "Sipariş", numeric: true, width: "8rem", cell: (r) => count(r.orderCount) },
    ...(amounts ? [{ key: "total", header: "Genel toplam", numeric: true, width: "11rem", cell: (r: DayRow) => visibleMoney(true, r.grandTotal, r.currency) }] : []),
  ];

  if (report && rows.length === 0) {
    return (
      <div className="rounded-xl border border-line bg-card shadow-e1">
        <EmptyState
          icon={<ShoppingCart className="size-6" />}
          title="Bu aralıkta sipariş yok"
          description="Seçilen günlerde ve durumda oluşturulmuş sipariş bulunamadı. Tarih aralığını genişletmeyi deneyin."
        />
      </div>
    );
  }

  return (
    <>
      <StatList label="Özet" loading={loading} stats={[{ label: "Sipariş", value: count(totalOrders) }]} />
      {!amounts && report ? <p className="text-caption text-fg-muted">Tutarlar rolünüz için gizli; yalnızca adetler gösteriliyor.</p> : null}
      {report ? <DailyChart title="Günlük sipariş sayısı" label="Günlük sipariş sayısı" points={points} /> : null}
      <ReportSection id={`${uid}-orders-by-status`} title="Duruma göre" description="Durum, siparişin bugünkü durumudur; tutarlar para birimine göre ayrı toplanır.">
        <Table caption="Duruma göre siparişler" columns={statusColumns} rows={rows} getRowId={(r) => `${r.status}|${r.currency}`} loading={loading} loadingRows={3} />
      </ReportSection>
      <ReportSection id={`${uid}-orders-by-day`} title="Günlük döküm">
        <Table
          caption="Günlük sipariş dökümü"
          columns={dayColumns}
          rows={report?.days ?? []}
          getRowId={(r) => `${r.day}|${r.status}|${r.currency}`}
          loading={loading}
          loadingRows={3}
          maxHeight="28rem"
          density="compact"
        />
      </ReportSection>
    </>
  );
}

"use client";

import { Trophy } from "@/components/ui/icons";
import { EmptyState } from "@/components/ui/empty-state";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { formatQuantity } from "@/lib/pricing/money";
import { DEFAULT_TOP_PRODUCTS, TOP_PRODUCT_LIMITS, getTopProducts, type TopProductItem, type TopProductsReport } from "@/lib/reports/api";
import { reportDefinition } from "@/lib/reports/definitions";
import { visibleMoney } from "@/lib/reports/errors";
import { useSession } from "@/lib/session/session-context";
import { DateRangeFilter, InvalidRange, ReportError, ReportFrame, ReportGate, count } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

const REPORT = reportDefinition("top-products");

/** REP-06: lines of approved-and-later orders; quantity, order count, revenue (excl. VAT) only with amountsVisible. */
export function TopProductsReportView() {
  return (
    <ReportGate report={REPORT}>
      <TopProductsReportBody />
    </ReportGate>
  );
}

function TopProductsReportBody() {
  const { api } = useSession();
  const { today, range, rangeError, params, update, setRange } = useReportUrl();
  const requested = Number(params.get("adet"));
  const limit = (TOP_PRODUCT_LIMITS as readonly number[]).includes(requested) ? requested : DEFAULT_TOP_PRODUCTS;
  const key = rangeError ? null : JSON.stringify([range, limit]);
  const { state, reload } = useReport(key, (signal) => getTopProducts(api, range, limit, signal));

  const filters = (
    <>
      <DateRangeFilter range={range} today={today} onChange={setRange} />
      <Select
        label="Gösterilecek ürün"
        className="w-48"
        value={String(limit)}
        options={TOP_PRODUCT_LIMITS.map((n) => ({ value: String(n), label: `İlk ${n}` }))}
        onChange={(e) => update({ adet: e.target.value === String(DEFAULT_TOP_PRODUCTS) ? null : e.target.value })}
      />
    </>
  );

  return (
    <ReportFrame report={REPORT} filters={filters} exportFilters={rangeError ? null : { from: range.from, to: range.to, limit }} today={today}>
      {rangeError ? (
        <InvalidRange message={rangeError} />
      ) : state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : (
        <TopProductsTable report={state.status === "ready" ? state.data : null} />
      )}
    </ReportFrame>
  );
}

function TopProductsTable({ report }: { report: TopProductsReport | null }) {
  const amounts = report?.amountsVisible ?? false;
  const columns: TableColumn<TopProductItem>[] = [
    { key: "rank", header: "Sıra", numeric: true, width: "4.5rem", cell: (r) => String(r.rank) },
    { key: "sku", header: "Stok kodu", width: "10rem", cell: (r) => <span className="font-mono text-caption">{r.sku}</span> },
    { key: "name", header: "Ürün", cell: (r) => r.name },
    { key: "qty", header: "Miktar", numeric: true, width: "9rem", cell: (r) => `${formatQuantity(r.quantity)} ${r.unit}` },
    { key: "orders", header: "Sipariş", numeric: true, width: "7rem", cell: (r) => count(r.orderCount) },
    ...(amounts ? [{ key: "revenue", header: "Tutar (KDV hariç)", numeric: true, width: "11rem", cell: (r: TopProductItem) => visibleMoney(true, r.revenue, r.currency) }] : []),
  ];
  return (
    <>
      {report && !amounts ? <p className="text-caption text-fg-muted">Tutarlar rolünüz için gizli; miktar ve sipariş sayısı gösteriliyor.</p> : null}
      <Table
        caption="En çok sipariş edilen ürünler"
        columns={columns}
        rows={report?.items ?? []}
        getRowId={(r) => `${r.id}|${r.currency}`}
        loading={report === null}
        empty={
          <EmptyState
            headingLevel="h2"
            icon={<Trophy className="size-6" />}
            title="Bu aralıkta onaylanmış sipariş yok"
            description="Rapor yalnızca onaylanmış ve sonraki durumlardaki siparişleri sayar. Tarih aralığını genişletmeyi deneyin."
          />
        }
      />
    </>
  );
}

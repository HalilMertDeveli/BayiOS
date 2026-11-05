"use client";

import { useState, type FormEvent } from "react";
import { PackageCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Table, type TableColumn } from "@/components/ui/table";
import { formatQuantity, parseDecimalInput, toDecimalInput } from "@/lib/pricing/money";
import { getCriticalStock, type CriticalStockItem } from "@/lib/reports/api";
import { reportDefinition } from "@/lib/reports/definitions";
import { reportErrorMessage } from "@/lib/reports/errors";
import { useSession } from "@/lib/session/session-context";
import { ReportError, ReportFrame, ReportGate, WarehouseFilter } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

const REPORT = reportDefinition("critical-stock");

/** Threshold from the URL (`esik`, API decimal "2.5"); invalid or missing → "0". */
function thresholdOf(value: string | null): string {
  if (!value) return "0";
  const parsed = parseDecimalInput(value, 3);
  return parsed.ok ? parsed.value : "0";
}

type More = { key: string; items: CriticalStockItem[]; nextCursor: string | null; hasMore: boolean };

/**
 * REP-03: active variants whose available quantity (on hand − reserved) is ≤ the threshold; variants with no balance
 * count as 0. Server-side keyset paging in SKU order ("Daha fazla yükle").
 */
export function CriticalStockReportView() {
  return (
    <ReportGate report={REPORT}>
      <CriticalStockReportBody />
    </ReportGate>
  );
}

function CriticalStockReportBody() {
  const { api } = useSession();
  const { today, params, update } = useReportUrl();
  const threshold = thresholdOf(params.get("esik"));
  const warehouseId = params.get("depo") ?? "";
  const filter = { threshold, warehouseId };
  const key = JSON.stringify(filter);
  const { state, reload } = useReport(key, (signal) => getCriticalStock(api, filter, null, signal));
  const [more, setMore] = useState<More | null>(null);
  const [moreState, setMoreState] = useState<{ key: string; status: "loading" | "error"; message?: string } | null>(null);

  const first = state.status === "ready" ? state.data : null;
  const extra = more?.key === key ? more : null;
  const items = first ? [...first.items, ...(extra?.items ?? [])] : [];
  const hasMore = extra ? extra.hasMore : (first?.page.hasMore ?? false);
  const cursor = extra ? extra.nextCursor : (first?.page.nextCursor ?? null);
  const loadingMore = moreState?.key === key && moreState.status === "loading";
  const moreError = moreState?.key === key && moreState.status === "error" ? moreState.message : null;

  const loadMore = async () => {
    if (!cursor || loadingMore) return;
    setMoreState({ key, status: "loading" });
    try {
      const page = await getCriticalStock(api, filter, cursor);
      setMore({ key, items: [...(extra?.items ?? []), ...page.items], nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
      setMoreState(null);
    } catch (error) {
      setMoreState({ key, status: "error", message: reportErrorMessage(error) });
    }
  };

  const filters = (
    <div className="flex flex-wrap items-start gap-3">
      <ThresholdForm value={threshold} onApply={(value) => update({ esik: value === "0" ? null : value })} />
      <WarehouseFilter value={warehouseId} onChange={(id) => update({ depo: id })} />
    </div>
  );

  const columns: TableColumn<CriticalStockItem>[] = [
    { key: "sku", header: "Stok kodu", width: "10rem", cell: (r) => <span className="font-mono text-caption">{r.sku}</span> },
    { key: "name", header: "Ürün", cell: (r) => r.name },
    { key: "onHand", header: "Eldeki", numeric: true, width: "8rem", cell: (r) => formatQuantity(r.onHand) },
    { key: "reserved", header: "Rezerve", numeric: true, width: "8rem", cell: (r) => formatQuantity(r.reserved) },
    {
      key: "available",
      header: "Satılabilir",
      numeric: true,
      width: "9rem",
      cell: (r) => (
        <span className={Number(r.available) <= 0 ? "font-semibold text-danger-fg" : "font-semibold"}>
          {formatQuantity(r.available)} {r.unit}
        </span>
      ),
    },
  ];

  return (
    <ReportFrame report={REPORT} filters={filters} exportFilters={{ threshold, ...(warehouseId ? { warehouseId } : {}) }} today={today}>
      {state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : (
        <section aria-label="Kritik stoktaki ürünler" className="flex flex-col gap-3">
          <p className="text-caption text-fg-muted">
            Satılabilir = eldeki − rezerve. Eşik: {formatQuantity(threshold)} · {first ? `${items.length.toLocaleString("tr-TR")} ürün gösteriliyor` : "yükleniyor"}
            {hasMore ? " (devamı var)" : ""}.
          </p>
          <Table
            caption="Kritik stok"
            columns={columns}
            rows={items}
            getRowId={(r) => r.id}
            loading={state.status !== "ready"}
            maxHeight="60vh"
            empty={
              <EmptyState
                headingLevel="h2"
                icon={<PackageCheck className="size-6" />}
                title="Kritik stokta ürün yok"
                description="Tüm aktif ürünlerin satılabilir miktarı eşiğin üzerinde. Daha yüksek bir eşikle kontrol edebilirsiniz."
              />
            }
          />
          {moreError ? (
            <p role="alert" className="text-body text-danger-fg">
              {moreError}
            </p>
          ) : null}
          {hasMore ? (
            <div className="flex justify-center">
              <Button variant="secondary" loading={loadingMore} onClick={() => void loadMore()}>
                Daha fazla yükle
              </Button>
            </div>
          ) : null}
        </section>
      )}
    </ReportFrame>
  );
}

function ThresholdForm({ value, onApply }: { value: string; onApply: (value: string) => void }) {
  const [text, setText] = useState(() => toDecimalInput(value));
  const [shown, setShown] = useState(value);
  const [error, setError] = useState<string | undefined>();
  if (shown !== value) {
    setShown(value);
    setText(toDecimalInput(value));
    setError(undefined);
  }
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const parsed = parseDecimalInput(text || "0", 3);
    if (!parsed.ok) {
      setError("0 veya daha büyük, en fazla 3 ondalıklı bir sayı girin.");
      return;
    }
    setError(undefined);
    onApply(parsed.value);
  };
  return (
    <form noValidate onSubmit={submit} aria-label="Kritik eşik filtresi" className="flex flex-col gap-1">
      <div className="flex items-end gap-2">
        <Input
          id="critical-threshold"
          label="Kritik eşik"
          inputMode="decimal"
          className="w-40"
          value={text}
          aria-invalid={error ? true : undefined}
          aria-describedby="critical-threshold-help"
          onChange={(e) => setText(e.target.value)}
        />
        <Button type="submit" variant="secondary">
          Uygula
        </Button>
      </div>
      <p id="critical-threshold-help" className={error ? "text-caption text-danger-fg" : "text-caption text-fg-muted"}>
        {error ?? "Satılabilir miktarı bu değere eşit veya altında olanlar listelenir."}
      </p>
    </form>
  );
}

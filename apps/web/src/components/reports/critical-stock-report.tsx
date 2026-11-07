"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import { Gauge, PackageCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Table, type TableColumn } from "@/components/ui/table";
import { formatQuantity, parseDecimalInput, toDecimalInput } from "@/lib/pricing/money";
import { getCriticalStock, type CriticalStockFilter, type CriticalStockItem } from "@/lib/reports/api";
import { reportDefinition } from "@/lib/reports/definitions";
import { reportErrorMessage } from "@/lib/reports/errors";
import { useSession } from "@/lib/session/session-context";
import { ReportError, ReportFrame, ReportGate, WarehouseFilter } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

const REPORT = reportDefinition("critical-stock");

/** Fixed threshold from the URL (`esik`, API decimal "2.5"); missing or invalid → null = each product's minimum stock. */
function thresholdOf(value: string | null): string | null {
  if (!value) return null;
  const parsed = parseDecimalInput(value, 3);
  return parsed.ok ? parsed.value : null;
}

type More = { key: string; items: CriticalStockItem[]; nextCursor: string | null; hasMore: boolean };

/**
 * REP-03 (#137): by default every active variant against its own minimum stock (available = on hand − reserved ≤
 * minimum; variants without one are left out unless "stoğu tükenenler" is on). A fixed threshold (`esik`) lists every
 * active variant at or below that value instead. Server-side keyset paging in SKU order ("Daha fazla yükle").
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
  const includeOutOfStock = threshold === null && params.get("tukenen") === "1";
  const filter: CriticalStockFilter = { threshold, warehouseId, includeOutOfStock };
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
      <ThresholdForm value={threshold} onApply={(value) => update(value ? { esik: value, tukenen: null } : { esik: null })} />
      <WarehouseFilter value={warehouseId} onChange={(id) => update({ depo: id })} />
      {threshold === null ? (
        <CheckboxField
          label="Eşiği olmayan, stoğu tükenmiş ürünleri de göster"
          hint="Minimum stok girilmemiş ürünlerden satılabilir miktarı 0 veya altında olanlar."
          checked={includeOutOfStock}
          onChange={(checked) => update({ tukenen: checked ? "1" : null })}
        />
      ) : null}
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
    {
      key: "minimum",
      header: "Min. stok",
      numeric: true,
      width: "8rem",
      cell: (r) => (r.minQuantity === null || r.minQuantity === undefined ? <span className="text-fg-muted">—</span> : formatQuantity(r.minQuantity)),
    },
  ];

  const notConfigured = first !== null && threshold === null && !first.thresholdsConfigured;
  const exportFilters = {
    ...(threshold !== null ? { threshold } : {}),
    ...(warehouseId ? { warehouseId } : {}),
    ...(includeOutOfStock ? { includeOutOfStock: true } : {}),
  };

  return (
    <ReportFrame report={REPORT} filters={filters} exportFilters={exportFilters} today={today}>
      {state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : (
        <section aria-label="Kritik stoktaki ürünler" className="flex flex-col gap-3">
          <p className="text-caption text-fg-muted">
            Satılabilir = eldeki − rezerve.{" "}
            {threshold === null ? "Her ürün kendi minimum stoğuna göre değerlendirilir" : `Sabit eşik: ${formatQuantity(threshold)}`} ·{" "}
            {first ? `${items.length.toLocaleString("tr-TR")} ürün gösteriliyor` : "yükleniyor"}
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
              notConfigured && !includeOutOfStock ? (
                <EmptyState
                  headingLevel="h2"
                  icon={<Gauge className="size-6" />}
                  title="Henüz minimum stok tanımlanmadı"
                  description="Kritik stok, ürünlere girilen minimum stoğa göre hesaplanır. Ürün düzenleme ekranındaki “Minimum stok” bölümünden (depoya özel eşik için ürünün stok sayfasından) değer girin ya da yukarıda sabit bir eşik deneyin."
                  action={
                    <Link href="/urunler" className="text-body font-medium text-brand-fg underline-offset-2 hover:underline">
                      Ürünlere git
                    </Link>
                  }
                />
              ) : (
                <EmptyState
                  headingLevel="h2"
                  icon={<PackageCheck className="size-6" />}
                  title="Kritik stokta ürün yok"
                  description={
                    threshold === null
                      ? "Minimum stoğu tanımlı tüm aktif ürünlerin satılabilir miktarı eşiğin üzerinde."
                      : "Tüm aktif ürünlerin satılabilir miktarı eşiğin üzerinde. Daha yüksek bir eşikle kontrol edebilirsiniz."
                  }
                />
              )
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

function ThresholdForm({ value, onApply }: { value: string | null; onApply: (value: string | null) => void }) {
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
    if (text.trim() === "") {
      setError(undefined);
      onApply(null);
      return;
    }
    const parsed = parseDecimalInput(text, 3);
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
          placeholder="Ürün eşiği"
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
        {error ?? "Boş: her ürünün kendi minimum stoğu. Değer girerseniz tüm ürünler bu sabit eşiğe göre listelenir."}
      </p>
    </form>
  );
}

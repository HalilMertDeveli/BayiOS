import { PackageCheck, TriangleAlert } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Table, type TableColumn } from "@/components/ui/table";
import type { OrderDetail, OrderLine, OrderTotals } from "@/lib/orders/api";
import type { Shortage } from "@/lib/orders/errors";
import { linePriceSourceLabels } from "@/lib/orders/labels";
import { vatModeLabel } from "@/lib/pricing/labels";
import { formatMoney, formatPercent, formatQuantity } from "@/lib/pricing/money";

/** Unit price as the API resolved it; a line without a price is "Fiyat yok" — never 0. */
export function LinePrice({ line, currency }: { line: OrderLine; currency: string }) {
  const money = line.priceStatus === "priced" ? formatMoney(line.unitPrice, currency) : null;
  if (money === null) return <Badge tone="danger">Fiyat yok</Badge>;
  return (
    <span className="flex flex-col items-end">
      <span className="font-medium tabular-nums">{money}</span>
      <span className="text-caption text-fg-muted">{line.priceSource ? (linePriceSourceLabels[line.priceSource] ?? line.priceSource) : ""}</span>
    </span>
  );
}

export function lineDiscountText(line: OrderLine, currency: string): string {
  if (line.discountAmount !== null && line.discountAmount !== undefined) return formatMoney(line.discountAmount, currency) ?? "—";
  return formatPercent(line.discountPercent) ?? "—";
}

const lineTotal = (line: OrderLine, currency: string) => (line.priceStatus === "priced" ? (formatMoney(line.lineTotal, currency) ?? "—") : "—");

/** Read-only lines (non-draft orders, or roles that cannot edit drafts). */
export function OrderLinesTable({ detail, showFulfilment }: { detail: OrderDetail; showFulfilment: boolean }) {
  const currency = detail.order.currency;
  const columns: TableColumn<OrderLine>[] = [
    { key: "no", header: "#", width: "3rem", numeric: true, cell: (l) => String(l.lineNo) },
    {
      key: "item",
      header: "Ürün",
      cell: (l) => (
        <span className="flex flex-col">
          <span className="font-mono text-caption text-fg">{l.sku}</span>
          {l.name ? <span className="text-caption text-fg-muted">{l.name}</span> : null}
        </span>
      ),
    },
    { key: "qty", header: "Miktar", numeric: true, width: "7rem", cell: (l) => `${formatQuantity(l.quantity)} ${l.unit}` },
    { key: "price", header: "Birim fiyat", numeric: true, width: "10rem", cell: (l) => <LinePrice line={l} currency={currency} /> },
    { key: "discount", header: "İskonto", numeric: true, width: "7rem", cell: (l) => lineDiscountText(l, currency) },
    { key: "vat", header: "KDV", numeric: true, width: "5rem", cell: (l) => formatPercent(l.taxRate) ?? "—" },
    { key: "total", header: "Satır toplamı", numeric: true, width: "9rem", cell: (l) => lineTotal(l, currency) },
  ];
  return (
    <>
      <div className="hidden md:block">
        <Table caption="Sipariş satırları" columns={columns} rows={detail.lines} getRowId={(l) => l.id} empty={<p className="p-4 text-body text-fg-muted">Satır yok.</p>} />
      </div>
      <ul aria-label="Sipariş satırları" className="flex flex-col gap-2 md:hidden">
        {detail.lines.map((l) => (
          <li key={l.id} className="rounded-lg border border-line bg-card p-3 shadow-e1">
            <div className="flex items-start justify-between gap-2">
              <span className="flex flex-col">
                <span className="font-mono text-body text-fg">{l.sku}</span>
                {l.name ? <span className="text-caption text-fg-muted">{l.name}</span> : null}
              </span>
              <LinePrice line={l} currency={currency} />
            </div>
            <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-caption">
              <dt className="text-fg-muted">Miktar</dt>
              <dd className="text-fg">
                {formatQuantity(l.quantity)} {l.unit}
              </dd>
              <dt className="text-fg-muted">İskonto</dt>
              <dd className="text-fg">{lineDiscountText(l, currency)}</dd>
              <dt className="text-fg-muted">Satır toplamı</dt>
              <dd className="text-fg">{lineTotal(l, currency)}</dd>
              {showFulfilment ? (
                <>
                  <dt className="text-fg-muted">Sevk edilen</dt>
                  <dd className="text-fg">{formatQuantity(l.shippedQuantity)}</dd>
                </>
              ) : null}
            </dl>
          </li>
        ))}
      </ul>
    </>
  );
}

/** Totals and the VAT breakdown exactly as the API computed them (OrderMath, PRD ORD-04). */
export function OrderTotalsPanel({ totals, currency }: { totals: OrderTotals; currency: string }) {
  const money = (v: OrderTotals["subtotal"]) => formatMoney(v, currency) ?? "—";
  return (
    <section aria-labelledby="order-totals" className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
      <h2 id="order-totals" className="text-h3 text-fg">
        Toplamlar <span className="text-caption font-normal text-fg-muted">({vatModeLabel(totals.pricesIncludeTax)} fiyatlar)</span>
      </h2>
      {totals.hasUnpricedLines ? (
        <FormAlert tone="warning" title="Fiyatsız satırlar toplamlara dahil değil">
          “Fiyat yok” satırları fiyatlanmadan sipariş onaya gönderilemez.
        </FormAlert>
      ) : null}
      <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 text-body">
        <dt className="text-fg-secondary">Ara toplam</dt>
        <dd className="text-right tabular-nums">{money(totals.subtotal)}</dd>
        <dt className="text-fg-secondary">İskonto</dt>
        <dd className="text-right tabular-nums">{money(totals.discountTotal)}</dd>
        <dt className="text-fg-secondary">Matrah</dt>
        <dd className="text-right tabular-nums">{money(totals.netTotal)}</dd>
        {totals.taxBreakdown.map((t) => (
          <FragmentRow key={String(t.rate)} label={`KDV ${formatPercent(t.rate) ?? ""} (matrah ${money(t.taxBase)})`} value={money(t.tax)} />
        ))}
        <dt className="text-fg-secondary">KDV toplamı</dt>
        <dd className="text-right tabular-nums">{money(totals.taxTotal)}</dd>
        <dt className="border-t border-line pt-2 font-semibold text-fg">Genel toplam</dt>
        <dd className="border-t border-line pt-2 text-right text-h3 font-semibold tabular-nums" data-testid="order-grand-total">
          {money(totals.grandTotal)}
        </dd>
      </dl>
    </section>
  );
}

function FragmentRow({ label, value }: { label: string; value: string }) {
  return (
    <>
      <dt className="pl-3 text-caption text-fg-muted">{label}</dt>
      <dd className="text-right text-caption tabular-nums text-fg-muted">{value}</dd>
    </>
  );
}

/**
 * Approval refused for lack of stock: nothing was reserved (all or nothing). Lists each short line with what was
 * requested and what is available, so the approver can decide (wait for goods, edit the draft after reject, …).
 */
export function ShortagesPanel({ shortages, lines, onDismiss }: { shortages: readonly Shortage[]; lines: readonly OrderLine[]; onDismiss: () => void }) {
  const lineOf = (variantId: string) => lines.find((l) => l.variantId === variantId);
  return (
    <section aria-labelledby="order-shortages" className="flex flex-col gap-3 rounded-lg border border-danger-border bg-danger-bg p-4 text-danger-fg">
      <h2 id="order-shortages" tabIndex={-1} className="flex items-center gap-2 text-h3">
        <TriangleAlert className="size-5" aria-hidden="true" />
        Onaylanamadı: stok yetersiz
      </h2>
      <p className="text-body">
        Sipariş ya hep ya hiç onaylanır: aşağıdaki satırlar için satılabilir stok yetmediği için <strong>hiçbir satıra stok ayrılmadı</strong> ve sipariş
        onay bekliyor durumunda kaldı.
      </p>
      <ul aria-label="Stoğu yetmeyen satırlar" className="flex flex-col gap-1 text-body">
        {shortages.map((s, i) => {
          const line = lineOf(s.variantId);
          return (
            <li key={`${s.variantId}-${s.locationId ?? ""}-${i}`}>
              <span className="font-mono font-semibold">{line?.sku ?? s.sku ?? s.variantId}</span>
              {line?.name ? ` — ${line.name}` : ""}: istenen {formatQuantity(s.requested)}, satılabilir {formatQuantity(s.available)}
            </li>
          );
        })}
      </ul>
      <p className="text-caption">Mal kabulü yapın veya siparişi reddedip miktarları düzenleyin; ardından tekrar onaylayın.</p>
      <div>
        <button type="button" onClick={onDismiss} className="min-h-11 text-body underline underline-offset-2">
          Bu uyarıyı kapat
        </button>
      </div>
    </section>
  );
}

/**
 * Fulfilment, read only: reserved / shipped / cancelled / open per line and the shipments. Picking, packing, shipping and
 * short-close move to the Warehouse picking/packing/shipment APIs (JOB 10) and are not done from this screen.
 */
export function FulfilmentSection({ detail }: { detail: OrderDetail }) {
  const columns: TableColumn<OrderLine>[] = [
    { key: "item", header: "Ürün", cell: (l) => <span className="font-mono text-caption">{l.sku}</span> },
    { key: "qty", header: "Sipariş", numeric: true, cell: (l) => formatQuantity(l.quantity) },
    { key: "reserved", header: "Ayrılmış", numeric: true, cell: (l) => formatQuantity(l.reservedQuantity) },
    { key: "shipped", header: "Sevk edilen", numeric: true, cell: (l) => formatQuantity(l.shippedQuantity) },
    { key: "cancelled", header: "Kapatılan", numeric: true, cell: (l) => formatQuantity(l.cancelledQuantity) },
    { key: "open", header: "Açık", numeric: true, cell: (l) => formatQuantity(l.openQuantity) },
  ];
  const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "short", timeStyle: "short", timeZone: "Europe/Istanbul" });
  return (
    <section aria-labelledby="order-fulfilment" className="flex flex-col gap-3">
      <h2 id="order-fulfilment" className="text-h3 text-fg">
        Depo ve sevkiyat
      </h2>
      {detail.order.hasShortage ? (
        <FormAlert tone="warning" title="Depoda eksik bildirildi">
          Toplama sırasında en az bir satırda eksik bildirildi. Karar (kısmi sevk, kalanı kapatma) depo/yönetici ekranlarında verilir.
        </FormAlert>
      ) : null}
      <Table caption="Satır bazında sevkiyat durumu" columns={columns} rows={detail.lines} getRowId={(l) => l.id} density="compact" />
      {detail.shipments.length > 0 ? (
        <ul aria-label="Sevkiyatlar" className="flex flex-col gap-2">
          {detail.shipments.map((s) => (
            <li key={s.id} className="rounded-md border border-line bg-card p-3 text-body">
              <span className="font-medium">{dateTime.format(new Date(s.shippedAt))}</span>
              {s.carrier ? ` · ${s.carrier}` : ""}
              {s.trackingNumber ? ` · takip no ${s.trackingNumber}` : ""}
              {s.packageCount !== null && s.packageCount !== undefined ? ` · ${formatQuantity(s.packageCount)} koli` : ""}
              <span className="text-caption text-fg-muted"> ({s.lines.length} satır)</span>
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-body text-fg-muted">Henüz sevkiyat yok.</p>
      )}
      <div role="note" className="flex gap-3 rounded-md border border-line bg-sunken px-4 py-3 text-body text-fg-secondary">
        <PackageCheck className="mt-0.5 size-5 shrink-0" aria-hidden="true" />
        <p>
          Toplama, paketleme, sevkiyat ve kalan miktarı kapatma işlemleri Depo modülünün toplama görevleri ve sevkiyat ekranlarından yapılır; bu ekrandan
          yapılamaz. Burada yalnızca durumları görürsünüz.
        </p>
      </div>
    </section>
  );
}

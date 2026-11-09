"use client";

import { useEffect, useState, useId } from "react";
import Link from "next/link";
import { ArrowLeft, CircleAlert, History, Lock, SearchX, SlidersHorizontal } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { ApiError } from "@/lib/api/client";
import type { Variant } from "@/lib/catalog/api";
import { balanceQuery, getVariant, listBalances, listMovements, movementQuery, type Balance, type Movement } from "@/lib/inventory/api";
import { adjustmentHref } from "@/lib/inventory/balance-query";
import { inventoryErrorMessage } from "@/lib/inventory/errors";
import { formatDateTime, movementTypeLabel, movementTypeLabels, movementTypeTone } from "@/lib/inventory/labels";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import { addQuantities, formatQuantity, formatSignedQuantity, toNumber } from "@/lib/inventory/quantity";
import { useSession } from "@/lib/session/session-context";
import { AvailableQuantity, MinimumCell } from "./quantity-cells";
import { MovementPlace } from "@/components/stock-documents/movement-place";
import { movementReference } from "@/lib/stock-documents/transit";
import { useLocationNames, useWarehouseNames, useWarehouses } from "./use-inventory-data";
import { VariantThresholds } from "./variant-thresholds";

type Head = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; variant: Variant; balances: Balance[] };

/** /stok/varyant/[id]: one variant's balances per warehouse/location and its movement history (W08 stock card). */
export function VariantStock({ variantId }: { variantId: string }) {
  const uid = useId();
  const { api, access } = useSession();
  const permissions = inventoryPermissions(access);
  const { state: warehouses } = useWarehouses();
  const warehouseById = useWarehouseNames(warehouses);
  const [head, setHead] = useState<Head>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    Promise.all([
      getVariant(api, variantId, controller.signal),
      // One variant has few balance rows (one per warehouse/location); 200 is the API's page maximum.
      listBalances(api, balanceQuery({ variantId }, null, 200), controller.signal),
    ])
      .then(([variant, page]) => {
        if (!controller.signal.aborted) setHead({ status: "ready", variant, balances: page.items });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setHead({ status: "error", message: inventoryErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, variantId, attempt]);

  const balances = head.status === "ready" ? head.balances : [];
  const locationNames = useLocationNames(balances.filter((b) => b.locationId).map((b) => b.warehouseId));
  const warehouseLabel = (id: string) => warehouseById.get(id)?.name ?? "—";

  if (head.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Ürün stoğu" />
        <section role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={head.notFound ? <SearchX className="size-6" /> : <CircleAlert className="size-6" />}
            title={head.notFound ? "Ürün bulunamadı" : "Stok bilgileri yüklenemedi"}
            description={head.notFound ? "Ürün silinmiş ya da bağlantı hatalı olabilir." : head.message}
            action={head.notFound ? undefined : <Button onClick={() => setAttempt((n) => n + 1)}>Tekrar dene</Button>}
          />
        </section>
      </>
    );
  }

  const variant = head.status === "ready" ? head.variant : null;
  const totals = balances.reduce(
    (acc, b) => ({
      onHand: addQuantities(acc.onHand, toNumber(b.onHand)),
      reserved: addQuantities(acc.reserved, toNumber(b.reserved)),
      available: addQuantities(acc.available, toNumber(b.available)),
    }),
    { onHand: 0, reserved: 0, available: 0 },
  );

  const balanceColumns: TableColumn<Balance>[] = [
    { key: "warehouse", header: "Depo", cell: (b) => warehouseLabel(b.warehouseId) },
    { key: "location", header: "Raf/göz", cell: (b) => <span className="font-mono text-caption">{b.locationId ? (locationNames.get(b.locationId) ?? "…") : "—"}</span> },
    { key: "onHand", header: "Rafta", numeric: true, cell: (b) => formatQuantity(b.onHand) },
    { key: "reserved", header: "Ayrılmış", numeric: true, cell: (b) => formatQuantity(b.reserved) },
    { key: "available", header: "Satılabilir", numeric: true, cell: (b) => <AvailableQuantity value={b.available} /> },
    { key: "minimum", header: "Min. stok", numeric: true, cell: (b) => <MinimumCell minQuantity={b.minQuantity} belowMinimum={b.belowMinimum} /> },
  ];

  return (
    <>
      <BackLink />
      <PageHeader
        title={variant ? variant.sku : "Ürün stoğu"}
        description={variant ? [variant.name, variant.barcode ? `Barkod ${variant.barcode}` : null].filter(Boolean).join(" · ") || "Varyant stoğu" : "Yükleniyor…"}
        actions={
          permissions.adjust && variant ? (
            <Link href={adjustmentHref({ variantId: variant.id })} className={buttonClasses("secondary")}>
              <SlidersHorizontal className="size-4" aria-hidden="true" />
              Stok düzelt
            </Link>
          ) : null
        }
      />

      <section aria-labelledby={`${uid}-variant-balances`} className="flex flex-col gap-3">
        <h2 id={`${uid}-variant-balances`} className="text-h3 text-fg">
          Depo ve raf bakiyeleri
        </h2>
        {head.status === "ready" ? (
          <dl className="grid grid-cols-3 gap-2 sm:max-w-xl">
            {[
              ["Toplam rafta", formatQuantity(totals.onHand)],
              ["Toplam ayrılmış", formatQuantity(totals.reserved)],
            ].map(([label, value]) => (
              <div key={label} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                <dt className="text-caption text-fg-muted">{label}</dt>
                <dd className="text-h2 tabular-nums text-fg">{value}</dd>
              </div>
            ))}
            <div className="rounded-lg border border-line bg-card p-3 shadow-e1">
              <dt className="text-caption text-fg-muted">Toplam satılabilir</dt>
              <dd className="text-h2">
                <AvailableQuantity value={totals.available} />
              </dd>
            </div>
          </dl>
        ) : null}
        <Table
          caption="Depo ve raf bakiyeleri"
          columns={balanceColumns}
          rows={balances}
          getRowId={(b) => b.id}
          loading={head.status === "loading"}
          loadingRows={2}
          empty={<EmptyState headingLevel="h3" title="Stok kaydı yok" description="Bu ürün için henüz stok hareketi olmamış." />}
        />
      </section>

      {variant && warehouses.status === "ready" ? (
        <VariantThresholds
          variant={variant}
          warehouses={warehouses.data}
          canEdit={permissions.manageThresholds && variant.status !== "archived"}
          onSaved={() => setAttempt((n) => n + 1)}
        />
      ) : null}

      {permissions.movements ? (
        <MovementHistory
          variantId={variantId}
          ownOnly={permissions.movementsOwnOnly}
          warehouseOptions={warehouses.status === "ready" ? warehouses.data : []} warehouseLabel={warehouseLabel} />
      ) : (
        <section aria-label="Hareket geçmişi" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            headingLevel="h3"
            icon={<Lock className="size-6" />}
            title="Hareket geçmişini görme yetkiniz yok"
            description="Rolünüz stok hareketlerini görmeye izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
          />
        </section>
      )}
    </>
  );
}

function BackLink() {
  return (
    <Link href="/stok" className="inline-flex min-h-11 items-center gap-1 self-start text-body font-medium text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Stok listesine dön
    </Link>
  );
}

type MovementData =
  | { key: string; status: "ready"; items: Movement[]; nextCursor: string | null; hasMore: boolean }
  | { key: string; status: "error"; message: string };

function MovementHistory({
  variantId,
  ownOnly,
  warehouseOptions,
  warehouseLabel,
}: {
  variantId: string;
  /** inventory.movements.read is limited to the user's own movements and task context (the API filters). */
  ownOnly: boolean;
  warehouseOptions: { id: string; name: string; code: string }[];
  warehouseLabel: (id: string) => string;
}) {
  const uid = useId();
  const { api } = useSession();
  const [warehouseId, setWarehouseId] = useState("");
  const [type, setType] = useState("");
  const [attempt, setAttempt] = useState(0);
  const [data, setData] = useState<MovementData | null>(null);
  const [moreLoading, setMoreLoading] = useState(false);
  const [moreError, setMoreError] = useState<string | null>(null);
  const filter = { variantId, warehouseId: warehouseId || null, type: type || null };
  const key = `${movementQuery(filter)}#${attempt}`;

  useEffect(() => {
    const controller = new AbortController();
    listMovements(api, movementQuery({ variantId, warehouseId: warehouseId || null, type: type || null }), controller.signal)
      .then((page) => {
        if (!controller.signal.aborted) setData({ key, status: "ready", items: page.items, nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setData({ key, status: "error", message: inventoryErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, key, variantId, warehouseId, type]);

  const current = data?.key === key ? data : null;
  const rows = current?.status === "ready" ? current.items : [];
  const locationNames = useLocationNames(rows.filter((m) => m.locationId).map((m) => m.warehouseId));

  const loadMore = async () => {
    if (current?.status !== "ready" || !current.nextCursor) return;
    setMoreLoading(true);
    setMoreError(null);
    try {
      const page = await listMovements(api, movementQuery(filter, current.nextCursor));
      setData((prev) =>
        prev && prev.key === current.key && prev.status === "ready"
          ? { ...prev, items: [...prev.items, ...page.items.filter((m) => !prev.items.some((x) => x.id === m.id))], nextCursor: page.page.nextCursor, hasMore: page.page.hasMore }
          : prev,
      );
    } catch (error) {
      setMoreError(inventoryErrorMessage(error));
    } finally {
      setMoreLoading(false);
    }
  };

  const columns: TableColumn<Movement>[] = [
    { key: "date", header: "Tarih", width: "10rem", cell: (m) => <span className="tabular-nums">{formatDateTime(m.occurredAt)}</span> },
    { key: "type", header: "Hareket", cell: (m) => <Badge tone={movementTypeTone(m.type)}>{movementTypeLabel(m.type)}</Badge> },
    { key: "warehouse", header: "Depo", cell: (m) => <MovementPlace movement={m} warehouseLabel={warehouseLabel} /> },
    { key: "location", header: "Raf/göz", cell: (m) => <span className="font-mono text-caption">{m.locationId ? (locationNames.get(m.locationId) ?? "…") : "—"}</span> },
    { key: "quantity", header: "Miktar", numeric: true, cell: (m) => <span className="font-semibold">{formatSignedQuantity(m.quantity)}</span> },
    { key: "after", header: "Sonraki bakiye", numeric: true, cell: (m) => formatQuantity(m.balanceAfter) },
    { key: "reason", header: "Açıklama / referans", cell: (m) => movementReference(m) ?? <span className="text-fg-muted">—</span> },
  ];

  return (
    <section aria-labelledby={`${uid}-movement-history`} className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <h2 id={`${uid}-movement-history`} className="flex items-center gap-2 text-h3 text-fg">
          <History className="size-5" aria-hidden="true" />
          Hareket geçmişi
        </h2>
        <p role="status" className="text-caption text-fg-muted">
          {current === null ? "Yükleniyor…" : current.status === "ready" ? `${current.items.length} hareket${current.hasMore ? " (devamı var)" : ""}` : ""}
        </p>
      </div>
      {ownOnly ? <p className="text-caption text-fg-muted">Yalnızca sizin yaptığınız ve görevlerinizle ilgili hareketler gösterilir.</p> : null}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:max-w-2xl">
        <Select
          label="Depo"
          placeholder="Tüm depolar"
          options={warehouseOptions.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }))}
          value={warehouseId}
          onChange={(e) => setWarehouseId(e.target.value)}
        />
        <Select
          label="Hareket türü"
          placeholder="Tüm hareketler"
          options={Object.entries(movementTypeLabels).map(([value, label]) => ({ value, label }))}
          value={type}
          onChange={(e) => setType(e.target.value)}
        />
      </div>
      {current?.status === "error" ? (
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            headingLevel="h3"
            icon={<CircleAlert className="size-6" />}
            title="Hareketler yüklenemedi"
            description={current.message}
            action={
              <Button
                onClick={() => {
                  setData(null);
                  setAttempt((n) => n + 1);
                }}
              >
                Tekrar dene
              </Button>
            }
          />
        </div>
      ) : (
        <>
          <div className="hidden md:block">
            <Table
              caption="Hareket geçmişi"
              columns={columns}
              rows={rows}
              getRowId={(m) => m.id}
              loading={current === null}
              empty={<EmptyState headingLevel="h3" title="Hareket yok" description="Bu filtrede stok hareketi bulunmuyor." />}
              maxHeight="60vh"
            />
          </div>
          <div className="md:hidden">
            {current === null ? (
              <p className="py-6 text-center text-body text-fg-muted">Yükleniyor…</p>
            ) : rows.length === 0 ? (
              <p className="rounded-lg border border-line bg-card p-4 text-body text-fg-muted">Bu filtrede stok hareketi bulunmuyor.</p>
            ) : (
              <ul aria-label="Hareket geçmişi" className="flex flex-col gap-2">
                {rows.map((m) => (
                  <li key={m.id} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                    <div className="flex items-center justify-between gap-2">
                      <Badge tone={movementTypeTone(m.type)}>{movementTypeLabel(m.type)}</Badge>
                      <span className="text-h3 tabular-nums text-fg">{formatSignedQuantity(m.quantity)}</span>
                    </div>
                    <p className="mt-1 text-caption text-fg-muted">
                      {formatDateTime(m.occurredAt)} · <MovementPlace movement={m} warehouseLabel={warehouseLabel} />
                      {m.locationId ? ` · ${locationNames.get(m.locationId) ?? "…"}` : ""} · bakiye {formatQuantity(m.balanceAfter)}
                    </p>
                    {movementReference(m) ? <p className="mt-1 text-body text-fg-secondary">{movementReference(m)}</p> : null}
                  </li>
                ))}
              </ul>
            )}
          </div>
          {current?.status === "ready" && current.hasMore ? (
            <div className="flex flex-col items-center gap-2">
              <Button variant="secondary" size="lg" loading={moreLoading} onClick={loadMore}>
                Daha fazla yükle
              </Button>
              {moreError ? (
                <p role="alert" className="text-caption text-danger-fg">
                  {moreError}
                </p>
              ) : null}
            </div>
          ) : null}
        </>
      )}
    </section>
  );
}

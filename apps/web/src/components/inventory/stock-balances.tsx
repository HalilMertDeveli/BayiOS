"use client";

import { useEffect, useState, type FormEvent } from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { Boxes, CircleAlert, PackagePlus, Rows3, Rows4, ScanBarcode, SearchX, SlidersHorizontal } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { balanceQuery, listBalances, type Balance } from "@/lib/inventory/api";
import { RECEIPT_HREF, adjustmentHref, parseBalanceQuery, toBalanceSearch, variantStockHref, type BalanceListQuery } from "@/lib/inventory/balance-query";
import { inventoryErrorMessage, isSearchTooBroad } from "@/lib/inventory/errors";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import { formatQuantity } from "@/lib/inventory/quantity";
import { useSession } from "@/lib/session/session-context";
import { usePersistentBoolean } from "@/lib/use-persistent-boolean";
import { AvailableQuantity } from "./quantity-cells";
import { useLocationNames, useVariantDirectory, useWarehouseNames, useWarehouses } from "./use-inventory-data";

type Filter = { q: string | null; warehouseId: string | null };

type ListData =
  | { key: string; status: "ready"; filter: Filter; items: Balance[]; nextCursor: string | null; hasMore: boolean }
  | { key: string; status: "nomatch"; term: string }
  | { key: string; status: "tooBroad"; term: string }
  | { key: string; status: "error"; message: string };

type MoreState = { status: "idle" } | { status: "loading" } | { status: "error"; message: string };

/**
 * W07 stock balances: physical (rafta), reserved (ayrılmış) and available (satılabilir) per variant, warehouse and
 * location. Filter by warehouse and by free text (GET /inventory/balances?q=: SKU, barcode or product name — scanner
 * friendly, Enter submits); keyset paging. A term matching too many products gets 422 → "Aramayı daraltın".
 */
export function StockBalances() {
  const { api, access } = useSession();
  const permissions = inventoryPermissions(access);
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const query = parseBalanceQuery(searchParams);
  const queryKey = toBalanceSearch(query);
  const [dense, setDense] = usePersistentBoolean("bayios.stok.dense");
  const { state: warehouses } = useWarehouses();
  const warehouseById = useWarehouseNames(warehouses);

  const navigate = (next: BalanceListQuery) => {
    const search = toBalanceSearch(next);
    router.replace(search ? `${pathname}?${search}` : pathname, { scroll: false });
  };

  const [searchText, setSearchText] = useState(query.q);
  const [seenQ, setSeenQ] = useState(query.q);
  if (query.q !== seenQ) {
    // URL changed (back/forward, "clear"): follow it.
    setSeenQ(query.q);
    setSearchText(query.q);
  }

  const [data, setData] = useState<ListData | null>(null);
  const [more, setMore] = useState<MoreState>({ status: "idle" });
  const [attempt, setAttempt] = useState(0);
  const dataKey = `${queryKey}#${attempt}`;

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    (async () => {
      const filter: Filter = { q: query.q || null, warehouseId: query.warehouseId || null };
      const page = await listBalances(api, balanceQuery(filter), signal);
      if (signal.aborted) return;
      setMore({ status: "idle" });
      if (filter.q && page.items.length === 0) {
        setData({ key: dataKey, status: "nomatch", term: filter.q });
        return;
      }
      setData({ key: dataKey, status: "ready", filter, items: page.items, nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
    })().catch((error: unknown) => {
      if (signal.aborted) return;
      if (isSearchTooBroad(error)) setData({ key: dataKey, status: "tooBroad", term: query.q });
      else setData({ key: dataKey, status: "error", message: inventoryErrorMessage(error) });
    });
    return () => controller.abort();
    // query.q / query.warehouseId are encoded in dataKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, dataKey]);

  const current = data?.key === dataKey ? data : null;
  const loading = current === null;
  const rows = current?.status === "ready" ? current.items : [];

  const variants = useVariantDirectory(rows.map((b) => b.variantId));
  const locationNames = useLocationNames(rows.filter((b) => b.locationId).map((b) => b.warehouseId));

  const loadMore = async () => {
    if (current?.status !== "ready" || !current.nextCursor) return;
    setMore({ status: "loading" });
    try {
      const page = await listBalances(api, balanceQuery(current.filter, current.nextCursor));
      setData((prev) =>
        prev && prev.key === current.key && prev.status === "ready"
          ? {
              ...prev,
              items: [...prev.items, ...page.items.filter((b) => !prev.items.some((x) => x.id === b.id))],
              nextCursor: page.page.nextCursor,
              hasMore: page.page.hasMore,
            }
          : prev,
      );
      setMore({ status: "idle" });
    } catch (error) {
      setMore({ status: "error", message: inventoryErrorMessage(error) });
    }
  };

  const submitSearch = (event: FormEvent) => {
    event.preventDefault();
    navigate({ ...query, q: searchText.trim() });
  };

  const hasCriteria = query.q !== "" || query.warehouseId !== "";
  const skuOf = (b: Balance) => variants.get(b.variantId);
  const warehouseLabel = (id: string) => {
    const w = warehouseById.get(id);
    return w ? `${w.name}${w.status !== "active" ? " (pasif)" : ""}` : "—";
  };
  const locationLabel = (b: Balance) => (b.locationId ? (locationNames.get(b.locationId) ?? "…") : "—");

  const columns: TableColumn<Balance>[] = [
    {
      key: "sku",
      header: "SKU",
      cell: (b) => {
        const v = skuOf(b);
        const text = v === undefined ? "…" : v === null ? "Bilinmeyen ürün" : v.sku;
        return permissions.read ? (
          <Link href={variantStockHref(b.variantId)} className="font-mono text-caption font-medium text-brand-fg underline-offset-2 hover:underline">
            {text}
          </Link>
        ) : (
          <span className="font-mono text-caption">{text}</span>
        );
      },
    },
    { key: "name", header: "Varyant", cell: (b) => skuOf(b)?.name ?? <span className="text-fg-muted">—</span> },
    { key: "warehouse", header: "Depo", cell: (b) => warehouseLabel(b.warehouseId) },
    { key: "location", header: "Raf/göz", cell: (b) => <span className="font-mono text-caption">{locationLabel(b)}</span> },
    { key: "onHand", header: "Rafta", numeric: true, width: "7rem", cell: (b) => formatQuantity(b.onHand) },
    { key: "reserved", header: "Ayrılmış", numeric: true, width: "7rem", cell: (b) => formatQuantity(b.reserved) },
    { key: "available", header: "Satılabilir", numeric: true, width: "9rem", cell: (b) => <AvailableQuantity value={b.available} /> },
    ...(permissions.adjust
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            width: "6rem",
            cell: (b: Balance) => (
              <Link
                href={adjustmentHref({ variantId: b.variantId, warehouseId: b.warehouseId, locationId: b.locationId })}
                className="text-caption font-medium text-brand-fg underline-offset-2 hover:underline"
                aria-label={`${skuOf(b)?.sku ?? "Ürün"} stoğunu düzelt`}
              >
                Düzelt
              </Link>
            ),
          },
        ]
      : []),
  ];

  const emptyView =
    current?.status === "tooBroad" ? (
      <EmptyState
        headingLevel="h3"
        icon={<SearchX className="size-6" />}
        title="Aramayı daraltın"
        description={`"${current.term}" çok fazla ürünle eşleşiyor. Daha uzun bir SKU, barkod veya ürün adı yazın.`}
        action={
          <Button variant="secondary" onClick={() => navigate({ warehouseId: query.warehouseId, q: "" })}>
            Aramayı temizle
          </Button>
        }
      />
    ) : current?.status === "nomatch" ? (
      <EmptyState
        headingLevel="h3"
        icon={<SearchX className="size-6" />}
        title="Ürün bulunamadı"
        description={`"${current.term}" ile eşleşen stok kaydı yok. SKU, barkod veya ürün adını kontrol edin.`}
        action={
          <Button variant="secondary" onClick={() => navigate({ warehouseId: query.warehouseId, q: "" })}>
            Aramayı temizle
          </Button>
        }
      />
    ) : hasCriteria ? (
      <EmptyState
        headingLevel="h3"
        icon={<SearchX className="size-6" />}
        title="Bu filtrede stok kaydı yok"
        description="Seçili depoda veya üründe henüz stok hareketi olmamış."
        action={
          <Button variant="secondary" onClick={() => navigate({ warehouseId: "", q: "" })}>
            Filtreleri temizle
          </Button>
        }
      />
    ) : (
      <EmptyState
        headingLevel="h3"
        icon={<Boxes className="size-6" />}
        title="Henüz stok yok"
        description={permissions.receive ? "İlk mal kabulü yaparak stok girişine başlayın." : "Bu işletmede henüz stok girişi yapılmamış."}
        action={
          permissions.receive ? (
            <Link href={RECEIPT_HREF} className={buttonClasses("primary")}>
              <PackagePlus className="size-4" aria-hidden="true" />
              Mal kabul yap
            </Link>
          ) : undefined
        }
      />
    );

  return (
    <>
      <PageHeader
        title="Stok"
        description="Depo ve raf bazında rafta, ayrılmış ve satılabilir miktarlar."
        actions={
          <>
            {permissions.adjust ? (
              <Link href={adjustmentHref()} className={buttonClasses("secondary")}>
                <SlidersHorizontal className="size-4" aria-hidden="true" />
                Stok düzelt
              </Link>
            ) : null}
            {permissions.receive ? (
              <Link href={RECEIPT_HREF} className={buttonClasses("primary")}>
                <PackagePlus className="size-4" aria-hidden="true" />
                Mal kabul
              </Link>
            ) : null}
          </>
        }
      />

      <section aria-label="Filtreler" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-[1fr_2fr]">
          <Select
            label="Depo"
            placeholder="Tüm depolar"
            options={
              warehouses.status === "ready"
                ? warehouses.data.map((w) => ({ value: w.id, label: `${w.name} (${w.code})${w.status !== "active" ? " — pasif" : ""}` }))
                : []
            }
            value={query.warehouseId}
            disabled={warehouses.status !== "ready"}
            onChange={(e) => navigate({ ...query, warehouseId: e.target.value })}
          />
          <form role="search" aria-label="Stokta ara" onSubmit={submitSearch} className="flex items-start gap-2">
            <Input
              label="SKU, barkod veya ürün adı"
              hint="Okutun veya yazıp Enter'a basın."
              className="flex-1"
              prefix={<ScanBarcode className="size-4" />}
              value={searchText}
              onChange={(e) => setSearchText(e.target.value)}
              autoComplete="off"
              spellCheck={false}
              enterKeyHint="search"
            />
            <Button type="submit" variant="secondary" className="mt-[1.625rem]">
              Ara
            </Button>
          </form>
        </div>
        {warehouses.status === "error" ? <p className="text-caption text-danger-fg">Depolar yüklenemedi: {warehouses.message}</p> : null}
        {hasCriteria ? (
          <div>
            <Button variant="ghost" size="sm" onClick={() => navigate({ warehouseId: "", q: "" })}>
              Filtreleri temizle
            </Button>
          </div>
        ) : null}
      </section>

      <section aria-labelledby="balance-results" className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="balance-results" className="text-h3 text-fg">
            Stok bakiyeleri
          </h2>
          <div className="flex items-center gap-3">
            <p role="status" className="text-caption text-fg-muted">
              {loading
                ? "Yükleniyor…"
                : current.status === "ready"
                  ? `${current.items.length} kayıt gösteriliyor${current.hasMore ? " (devamı var)" : ""}`
                  : ""}
            </p>
            <Button
              variant="secondary"
              size="sm"
              aria-pressed={dense}
              className="hidden md:inline-flex"
              icon={dense ? <Rows4 className="size-4" /> : <Rows3 className="size-4" />}
              onClick={() => setDense(!dense)}
            >
              Sık görünüm
            </Button>
          </div>
        </div>

        {current?.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState
              headingLevel="h3"
              icon={<CircleAlert className="size-6" />}
              title="Stok bilgileri yüklenemedi"
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
                caption="Stok bakiyeleri"
                columns={columns}
                rows={rows}
                getRowId={(b) => b.id}
                density={dense ? "compact" : "comfortable"}
                loading={loading}
                empty={emptyView}
                maxHeight="70vh"
              />
            </div>
            <div className="md:hidden">
              {loading ? (
                <p className="py-6 text-center text-body text-fg-muted">Yükleniyor…</p>
              ) : rows.length === 0 ? (
                <div className="rounded-lg border border-line bg-card">{emptyView}</div>
              ) : (
                <ul aria-label="Stok bakiyeleri" className="flex flex-col gap-2">
                  {rows.map((b) => {
                    const v = skuOf(b);
                    return (
                      <li key={b.id} className="rounded-lg border border-line bg-card p-4 shadow-e1">
                        <div className="flex items-start justify-between gap-3">
                          <div className="flex min-w-0 flex-col">
                            <Link
                              href={variantStockHref(b.variantId)}
                              className="flex min-h-11 items-center font-mono text-body font-semibold text-brand-fg underline-offset-2 hover:underline"
                            >
                              {v === undefined ? "…" : v === null ? "Bilinmeyen ürün" : v.sku}
                            </Link>
                            {v?.name ? <span className="text-caption text-fg-secondary">{v.name}</span> : null}
                          </div>
                          {b.locationId ? (
                            <Badge tone="neutral" className="font-mono">
                              {locationLabel(b)}
                            </Badge>
                          ) : null}
                        </div>
                        <p className="mt-1 text-caption text-fg-muted">{warehouseLabel(b.warehouseId)}</p>
                        <dl className="mt-3 grid grid-cols-3 gap-2 text-center">
                          <div className="rounded-md bg-sunken p-2">
                            <dt className="text-caption text-fg-muted">Rafta</dt>
                            <dd className="text-h3 tabular-nums text-fg">{formatQuantity(b.onHand)}</dd>
                          </div>
                          <div className="rounded-md bg-sunken p-2">
                            <dt className="text-caption text-fg-muted">Ayrılmış</dt>
                            <dd className="text-h3 tabular-nums text-fg">{formatQuantity(b.reserved)}</dd>
                          </div>
                          <div className="rounded-md bg-sunken p-2">
                            <dt className="text-caption text-fg-muted">Satılabilir</dt>
                            <dd className="text-h3">
                              <AvailableQuantity value={b.available} />
                            </dd>
                          </div>
                        </dl>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>
            {current?.status === "ready" && current.hasMore ? (
              <div className="flex flex-col items-center gap-2">
                <Button variant="secondary" size="lg" loading={more.status === "loading"} onClick={loadMore}>
                  Daha fazla yükle
                </Button>
                {more.status === "error" ? (
                  <p role="alert" className="text-caption text-danger-fg">
                    {more.message}
                  </p>
                ) : null}
              </div>
            ) : null}
          </>
        )}
      </section>
    </>
  );
}

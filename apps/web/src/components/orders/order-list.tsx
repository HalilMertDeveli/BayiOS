"use client";

import { useEffect, useMemo, useState, useId } from "react";
import Link from "next/link";
import { CircleAlert, Lock, MessageSquareText, Plus, Rows3, Rows4, Search, SearchX, ShoppingCart } from "lucide-react";
import { CustomerPicker } from "@/components/customers/customer-picker";
import { Button, buttonClasses } from "@/components/ui/button";
import { SkeletonCards } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { WHATSAPP_ORDER_HREF } from "@/lib/assistant/api";
import { assistantPermissions } from "@/lib/assistant/permissions";
import { FEATURES, featureGate } from "@/lib/billing/entitlements";
import { NEW_ORDER_HREF, listOrders, orderHref, type OrderSummary } from "@/lib/orders/api";
import { orderErrorMessage } from "@/lib/orders/errors";
import { ORDER_SOURCES, orderSourceLabels } from "@/lib/orders/labels";
import {
  ORDER_STATUS_FILTERS,
  dateRangeError,
  emptyOrderQuery,
  hasOrderCriteria,
  parseOrderQuery,
  toOrderApiQuery,
  toOrderSearchString,
  type OrderListQuery,
} from "@/lib/orders/list-query";
import { orderPermissions } from "@/lib/orders/permissions";
import { customerPermissions } from "@/lib/customers/permissions";
import { formatMoney, formatQuantity } from "@/lib/pricing/money";
import { useSession } from "@/lib/session/session-context";
import { usePersistentBoolean } from "@/lib/use-persistent-boolean";
import { useUrlQuery } from "@/lib/use-url-query";
import { OrderStatusCell } from "./order-badges";
import { useCustomerNames } from "./use-order-reference";

const SEARCH_DEBOUNCE_MS = 300;

type ListData =
  | { key: string; status: "ready"; items: OrderSummary[]; nextCursor: string | null; hasMore: boolean }
  | { key: string; status: "error"; message: string };

type MoreState = { status: "idle" } | { status: "loading" } | { status: "error"; message: string };

const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "short", timeStyle: "short", timeZone: "Europe/Istanbul" });
export const formatOrderDate = (iso: string) => dateTime.format(new Date(iso));

/**
 * W04 order list: order-number search, status / customer / source / created-date filters in the URL, keyset paging
 * with "Daha fazla yükle" (newest first), status + fulfilment badges, table on desktop and cards on phones, dense mode.
 * Totals are the API's `grandTotal`, formatted only.
 */
export function OrderList() {
  const { api, access, entitlements } = useSession();
  const permissions = orderPermissions(access);
  if (!permissions.read) return <NoAccess />;
  return (
    <OrderListView
      api={api}
      canCreate={permissions.create}
      canParseWhatsapp={assistantPermissions(access).parse}
      whatsappLocked={featureGate(entitlements, FEATURES.aiOrderParsing).locked}
      canReadCustomers={customerPermissions(access).read}
      taskLinkedOnly={permissions.readTaskLinkedOnly}
    />
  );
}

function NoAccess() {
  return (
    <>
      <PageHeader title="Siparişler" />
      <section aria-label="Siparişler durumu" className="rounded-lg border border-line bg-card shadow-e1">
        <EmptyState
          icon={<Lock className="size-6" />}
          title="Sipariş listesini görme yetkiniz yok"
          description="Rolünüz siparişleri görmeye izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
        />
      </section>
    </>
  );
}

function OrderListView({
  api,
  canCreate,
  canParseWhatsapp,
  whatsappLocked,
  canReadCustomers,
  taskLinkedOnly,
}: {
  api: ReturnType<typeof useSession>["api"];
  canCreate: boolean;
  /** whatsapp.parse: the "WhatsApp siparişi" entry (the draft itself also needs orders.create; checked on the review page). */
  canParseWhatsapp: boolean;
  /** The AI assistant add-on is not in the plan (#51): the entry stays (started reviews), marked as locked. */
  whatsappLocked: boolean;
  /** customers.read: names and the customer filter (warehouse staff have neither). */
  canReadCustomers: boolean;
  /** orders.read limited to task-linked orders without prices (warehouse staff). */
  taskLinkedOnly: boolean;
}) {
  const uid = useId();
  // Filters change through navigate / setFilter only: a second change before the URL has caught up builds on the URL
  // just requested, not on the stale query; otherwise it silently brings the cleared filters back (#136, #152).
  const { query, latest, navigate } = useUrlQuery(parseOrderQuery, toOrderSearchString);
  const rangeError = dateRangeError(query);
  const apiKey = toOrderApiQuery(query);
  const [dense, setDense] = usePersistentBoolean("bayios.siparisler.dense");
  /** One filter changed: applied on top of the latest requested query. */
  const setFilter = (patch: Partial<OrderListQuery>) => navigate({ ...latest(), ...patch });

  // ---- Search box: local text, pushed to the URL after a pause (same pattern as the other lists). ----
  const [searchText, setSearchText] = useState(query.q);
  const [pushedQ, setPushedQ] = useState(query.q);
  const [seenQ, setSeenQ] = useState(query.q);
  if (query.q !== seenQ) {
    setSeenQ(query.q);
    if (query.q !== pushedQ) {
      setPushedQ(query.q);
      setSearchText(query.q);
    }
  }
  useEffect(() => {
    const text = searchText.trim();
    if (text === latest().q) return;
    const timer = setTimeout(() => {
      setPushedQ(text);
      navigate({ ...latest(), q: text });
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [searchText, query, latest, navigate]);

  // ---- Data: first page per query; more pages appended with the cursor. ----
  const [data, setData] = useState<ListData | null>(null);
  const [more, setMore] = useState<MoreState>({ status: "idle" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (rangeError) return;
    const controller = new AbortController();
    listOrders(api, apiKey, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        setMore({ status: "idle" });
        setData({ key: apiKey, status: "ready", items: page.items, nextCursor: page.page.nextCursor ?? null, hasMore: page.page.hasMore });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setData({ key: apiKey, status: "error", message: orderErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, apiKey, attempt, rangeError]);

  const current = data?.key === apiKey ? data : null;
  const loading = current === null && !rangeError;

  const loadMore = async () => {
    if (current?.status !== "ready" || !current.nextCursor) return;
    setMore({ status: "loading" });
    try {
      const page = await listOrders(api, toOrderApiQuery(query, current.nextCursor));
      setData((prev) =>
        prev && prev.key === current.key && prev.status === "ready"
          ? {
              ...prev,
              items: [...prev.items, ...page.items.filter((o) => !prev.items.some((x) => x.id === o.id))],
              nextCursor: page.page.nextCursor ?? null,
              hasMore: page.page.hasMore,
            }
          : prev,
      );
      setMore({ status: "idle" });
    } catch (error) {
      setMore({ status: "error", message: orderErrorMessage(error) });
    }
  };

  const rows = useMemo(() => (current?.status === "ready" ? current.items : []), [current]);
  // Row names come with the list (#86); only the filter's customer is looked up. customerName is null for warehouse staff.
  const filterIds = useMemo(() => (canReadCustomers && query.customerId ? [query.customerId] : []), [canReadCustomers, query.customerId]);
  const customers = useCustomerNames(filterIds);
  const customerName = (o: OrderSummary) => o.customerName ?? "—";
  const filterCustomer = query.customerId
    ? { id: query.customerId, name: customers.get(query.customerId)?.name ?? "Seçili müşteri", code: customers.get(query.customerId)?.code ?? "" }
    : null;
  const hasCriteria = hasOrderCriteria(query);

  const columns: TableColumn<OrderSummary>[] = [
    {
      key: "number",
      header: "Sipariş no",
      nowrap: true,
      cell: (o) => (
        <Link href={orderHref(o.id)} className="font-mono font-medium text-brand-fg underline-offset-2 hover:underline">
          {o.number}
        </Link>
      ),
    },
    { key: "customer", header: "Müşteri", cell: (o) => <span className="block min-w-40">{customerName(o)}</span> },
    { key: "status", header: "Durum", nowrap: true, cell: (o) => <OrderStatusCell status={o.status} fulfillmentStatus={o.fulfillmentStatus} /> },
    { key: "source", header: "Kaynak", nowrap: true, cell: (o) => orderSourceLabels[o.source] ?? o.source },
    { key: "lines", header: "Satır", numeric: true, cell: (o) => formatQuantity(o.lineCount) },
    { key: "total", header: "Genel toplam", numeric: true, cell: (o) => formatMoney(o.grandTotal, o.currency) ?? "—" },
    { key: "created", header: "Oluşturma", nowrap: true, cell: (o) => <span className="text-fg-muted tabular-nums">{formatOrderDate(o.createdAt)}</span> },
  ];

  const emptyView = hasCriteria ? (
    <EmptyState
      headingLevel="h3"
      icon={<SearchX className="size-6" />}
      title="Eşleşen sipariş bulunamadı"
      description="Sipariş numarasının bir kısmını yazın (ör. 000123) veya filtreleri değiştirip tekrar deneyin."
      action={
        <Button variant="secondary" onClick={() => navigate(emptyOrderQuery)}>
          Aramayı ve filtreleri temizle
        </Button>
      }
    />
  ) : (
    <EmptyState
      headingLevel="h3"
      icon={<ShoppingCart className="size-6" />}
      title="Henüz sipariş yok"
      description={taskLinkedOnly ? "Görevlerinize bağlı sipariş yok. Size atanan veya havuzdaki görevlerin siparişleri burada görünür." : canCreate ? "İlk siparişi oluşturun: müşteri seçin, ürünleri okutun; fiyatlar müşterinin fiyat listesinden gelir." : "Bu işletmede henüz sipariş yok."}
      action={
        canCreate ? (
          <Link href={NEW_ORDER_HREF} className={buttonClasses("primary")}>
            <Plus className="size-4" aria-hidden="true" />
            İlk siparişi oluştur
          </Link>
        ) : undefined
      }
    />
  );

  return (
    <>
      <PageHeader
        title="Siparişler"
        description={
          taskLinkedOnly
            ? "Görevlerinize bağlı siparişler; fiyatlar rolünüz gereği gösterilmez."
            : "Siparişleri numarayla arayın, duruma, müşteriye, kaynağa ve tarihe göre süzün."
        }
        actions={
          canCreate || canParseWhatsapp ? (
            <>
              {canParseWhatsapp ? (
                <Link href={WHATSAPP_ORDER_HREF} className={buttonClasses("secondary")}>
                  {whatsappLocked ? <Lock className="size-4" aria-hidden="true" /> : <MessageSquareText className="size-4" aria-hidden="true" />}
                  WhatsApp siparişi
                  {whatsappLocked ? <span className="sr-only"> (paketinizde kapalı)</span> : null}
                </Link>
              ) : null}
              {canCreate ? (
                <Link href={NEW_ORDER_HREF} className={buttonClasses("primary")}>
                  <Plus className="size-4" aria-hidden="true" />
                  Yeni sipariş
                </Link>
              ) : null}
            </>
          ) : null
        }
      />

      <section aria-label="Arama ve filtreler" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
        <form role="search" onSubmit={(e) => e.preventDefault()}>
          <Input
            label="Sipariş no ara"
            type="search"
            placeholder="ör. SIP-2026-000123 veya 123"
            prefix={<Search className="size-4" />}
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            autoComplete="off"
            hint="Sipariş numarasının içinde geçen metin aranır."
          />
        </form>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Select
            label="Durum"
            options={ORDER_STATUS_FILTERS.map((f) => ({ value: f.value, label: f.label }))}
            value={query.status}
            onChange={(e) => setFilter({ status: e.target.value })}
          />
          <Select
            label="Kaynak"
            placeholder="Tüm kaynaklar"
            options={ORDER_SOURCES.map((s) => ({ value: s, label: orderSourceLabels[s] }))}
            value={query.source}
            onChange={(e) => setFilter({ source: e.target.value })}
          />
          <Input label="Başlangıç tarihi" type="date" value={query.from} max={query.to || undefined} onChange={(e) => setFilter({ from: e.target.value })} />
          <Input
            label="Bitiş tarihi"
            type="date"
            value={query.to}
            min={query.from || undefined}
            error={rangeError ?? undefined}
            onChange={(e) => setFilter({ to: e.target.value })}
          />
        </div>
        {canReadCustomers ? (
          <CustomerPicker
            label="Müşteriye göre süz"
            pickedLabel="Müşteri filtresi"
            hint="Ad veya kodun bir kısmı; tam vergi no / telefon."
            value={filterCustomer}
            onChange={(c) => setFilter({ customerId: c?.id ?? "" })}
          />
        ) : null}
        <p className="text-caption text-fg-muted">Tarihler siparişin oluşturulduğu güne göre (UTC) süzülür.</p>
        {hasCriteria ? (
          <div>
            <Button variant="ghost" size="sm" onClick={() => navigate(emptyOrderQuery)}>
              Aramayı ve filtreleri temizle
            </Button>
          </div>
        ) : null}
      </section>

      <section aria-labelledby={`${uid}-order-results`} className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id={`${uid}-order-results`} className="text-h3 text-fg">
            Sipariş listesi
          </h2>
          <div className="flex items-center gap-3">
            <p role="status" className="text-caption text-fg-muted">
              {loading ? "Yükleniyor…" : current?.status === "ready" ? `${current.items.length} sipariş gösteriliyor${current.hasMore ? " (devamı var)" : ""}` : ""}
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

        {rangeError ? (
          <p role="alert" className="rounded-lg border border-line bg-card p-4 text-body text-danger-fg">
            {rangeError} Tarihleri düzeltin.
          </p>
        ) : current?.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState
              headingLevel="h3"
              icon={<CircleAlert className="size-6" />}
              title="Siparişler yüklenemedi"
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
                caption="Siparişler"
                columns={columns}
                rows={rows}
                getRowId={(o) => o.id}
                loading={loading}
                empty={emptyView}
                density={dense ? "compact" : "comfortable"}
                maxHeight="70vh"
              />
            </div>
            <div className="md:hidden">
              {loading ? (
                <SkeletonCards />
              ) : rows.length === 0 ? (
                <div className="rounded-lg border border-line bg-card">{emptyView}</div>
              ) : (
                <ul aria-label="Siparişler" className="flex flex-col gap-2">
                  {rows.map((o) => (
                    <li key={o.id} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                      <div className="flex items-start justify-between gap-2">
                        <Link href={orderHref(o.id)} className="min-h-11 py-2 font-mono font-medium text-brand-fg underline-offset-2 hover:underline">
                          {o.number}
                        </Link>
                        <span className="pt-2 font-medium tabular-nums text-fg">{formatMoney(o.grandTotal, o.currency) ?? "—"}</span>
                      </div>
                      <OrderStatusCell status={o.status} fulfillmentStatus={o.fulfillmentStatus} />
                      <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-caption">
                        <dt className="text-fg-muted">Müşteri</dt>
                        <dd className="text-fg">{customerName(o)}</dd>
                        <dt className="text-fg-muted">Satır</dt>
                        <dd className="text-fg">{formatQuantity(o.lineCount)}</dd>
                        <dt className="text-fg-muted">Kaynak</dt>
                        <dd className="text-fg">{orderSourceLabels[o.source] ?? o.source}</dd>
                        <dt className="text-fg-muted">Oluşturma</dt>
                        <dd className="text-fg">{formatOrderDate(o.createdAt)}</dd>
                      </dl>
                    </li>
                  ))}
                </ul>
              )}
            </div>
            {current?.status === "ready" && current.hasMore ? (
              <div className="flex flex-col items-center gap-2">
                <Button variant="secondary" loading={more.status === "loading"} onClick={loadMore}>
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

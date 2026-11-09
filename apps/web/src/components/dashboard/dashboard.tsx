"use client";

import { useCallback, useEffect, useId, useState, type ReactNode } from "react";
import Link from "next/link";
import { Bot, ChevronRight, CircleAlert, ClipboardCheck, ClipboardList, LayoutDashboard, PackageMinus, Plus, ShoppingCart, Sparkles, type LucideIcon } from "lucide-react";
import { TaskStatusBadge } from "@/components/fulfilment/fulfilment-badges";
import { SetupChecklist } from "@/components/onboarding/setup-checklist";
import { OrderStatus } from "@/components/orders/order-badges";
import { Badge, orderStatusMeta, type BadgeTone } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { listOrderParses, orderParseHref, WHATSAPP_ORDER_HREF, type OrderParseListItem } from "@/lib/assistant/api";
import { PLAN_PAGE_HREF, planLabel, subscriptionStatusLabels } from "@/lib/billing/labels";
import { trialDaysLeft } from "@/lib/billing/trial";
import { PARSES_PAGE, PENDING_PAGE, TILE_ROWS, anyTile, countText, dashboardTiles, quickLinks, summarizeOrders } from "@/lib/dashboard/tiles";
import { listMyTasks, taskHref, TASKS_HREF, type TaskSummary } from "@/lib/fulfilment/api";
import { taskTypeLabels } from "@/lib/fulfilment/labels";
import { ORDERS_HREF, listOrders, orderHref, type OrderPage } from "@/lib/orders/api";
import { orderErrorMessage } from "@/lib/orders/errors";
import { orderBadgeKey, orderStatusLabel } from "@/lib/orders/labels";
import { formatMoney, formatQuantity } from "@/lib/pricing/money";
import { getCriticalStock, getOrdersReport, type CriticalStockReport, type OrdersReport } from "@/lib/reports/api";
import { istanbulToday } from "@/lib/reports/date-range";
import { reportHref } from "@/lib/reports/definitions";
import { reportErrorMessage } from "@/lib/reports/errors";
import { useSession } from "@/lib/session/session-context";

type TileState<T> = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: T };

/** One request per tile, started when the tile mounts (all tiles mount together, so the requests run in parallel). */
function useTile<T>(load: (signal: AbortSignal) => Promise<T>, errorMessage: (error: unknown) => string): { state: TileState<T>; retry: () => void } {
  const [state, setState] = useState<TileState<T>>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setState({ status: "ready", data });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", message: errorMessage(error) });
      });
    return () => controller.abort();
    // The loaders only depend on the session's api client, which is stable for the page.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt]);
  const retry = useCallback(() => {
    setState({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);
  return { state, retry };
}

const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "short", timeStyle: "short", timeZone: "Europe/Istanbul" });
const dateLong = new Intl.DateTimeFormat("tr-TR", { dateStyle: "long", timeZone: "Europe/Istanbul" });
const dateFull = new Intl.DateTimeFormat("tr-TR", { dateStyle: "full", timeZone: "Europe/Istanbul" });

/** /pano (W03): role-aware overview from existing endpoints; tiles the user may not load are not rendered. */
export function Dashboard() {
  const { access, entitlements, state } = useSession();
  const tiles = dashboardTiles(access, entitlements);
  const links = quickLinks(access, entitlements);
  const name = state.status === "ready" ? (state.user.fullName ?? null) : null;
  const [today] = useState(() => dateFull.format(new Date()));

  return (
    <>
      <PageHeader
        title="Pano"
        description={
          <>
            {name ? `Hoş geldiniz, ${name}. İşletmenizin bugünkü özeti.` : "İşletmenizin bugünkü özeti."}
            <span className="mt-0.5 block text-caption">{today}</span>
          </>
        }
        actions={
          links.length > 0 ? (
            <nav aria-label="Hızlı işlemler">
              <ul className="flex flex-wrap gap-2 sm:justify-end">
                {links.map((l, i) => (
                  <li key={l.href}>
                    <Link href={l.href} className={buttonClasses(i === 0 ? "primary" : "secondary")}>
                      {i === 0 ? <Plus className="size-4" aria-hidden="true" /> : null}
                      {l.label}
                    </Link>
                  </li>
                ))}
              </ul>
            </nav>
          ) : null
        }
      />
      <SetupChecklist />
      {anyTile(tiles) ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          {tiles.pendingApproval ? <PendingApprovalTile /> : null}
          {tiles.todayOrders ? <TodayOrdersTile /> : null}
          {tiles.criticalStock ? <CriticalStockTile /> : null}
          {tiles.myTasks ? <MyTasksTile /> : null}
          {tiles.whatsappReview ? <WhatsAppTile /> : null}
          {tiles.plan ? <PlanTile /> : null}
        </div>
      ) : (
        <section aria-label="Pano durumu" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<LayoutDashboard className="size-6" />}
            title="Panoda gösterilecek özet yok"
            description="Rolünüz sipariş, stok veya görev özetlerini görmeye izin vermiyor. Menüden yetkili olduğunuz ekranlara geçebilirsiniz."
          />
        </section>
      )}
    </>
  );
}

function Tile<T>({
  id,
  title,
  Icon,
  href,
  linkLabel,
  state,
  retry,
  children,
}: {
  id: string;
  title: string;
  Icon: LucideIcon;
  href: string;
  linkLabel: string;
  state: TileState<T>;
  retry: () => void;
  children: (data: T) => ReactNode;
}) {
  // Unique per instance: a hidden (kept-alive) previous route may hold the same tile (a11y audit A-1).
  const headingId = `${useId()}-tile-${id}`;
  return (
    <section aria-labelledby={headingId} aria-busy={state.status === "loading" || undefined} className="flex flex-col rounded-xl border border-line bg-card shadow-e1">
      <h2 id={headingId} className="flex items-center gap-3 px-5 pt-5 text-h3 text-fg">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-brand-subtle text-brand-fg" aria-hidden="true">
          <Icon className="size-5" />
        </span>
        {title}
      </h2>
      <div className="flex flex-1 flex-col gap-3 px-5 pt-4 pb-5">
        {state.status === "loading" ? (
          <div aria-hidden="true" className="flex flex-col gap-2">
            <span className="block h-8 w-16 animate-pulse rounded-sm bg-sunken" />
            <span className="block h-4 w-3/4 animate-pulse rounded-sm bg-sunken" />
            <span className="block h-4 w-2/3 animate-pulse rounded-sm bg-sunken" />
          </div>
        ) : null}
        {state.status === "loading" ? <p className="sr-only">Yükleniyor…</p> : null}
        {state.status === "error" ? (
          <div role="alert" className="flex flex-col items-start gap-2">
            <p className="flex items-start gap-2 text-body text-danger-fg">
              <CircleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              {state.message}
            </p>
            <Button size="sm" variant="secondary" onClick={retry}>
              Tekrar dene
            </Button>
          </div>
        ) : null}
        {state.status === "ready" ? children(state.data) : null}
      </div>
      <div className="border-t border-line px-5 py-3">
        <Link href={href} className="inline-flex min-h-6 w-fit items-center gap-1 text-body font-medium text-brand-fg underline-offset-2 hover:underline">
          {linkLabel}
          <ChevronRight className="size-4" aria-hidden="true" />
        </Link>
      </div>
    </section>
  );
}

function Kpi({ value, label }: { value: string; label: string }) {
  return (
    <p className="flex flex-wrap items-baseline gap-x-2">
      <span className="text-display font-semibold tracking-tight text-fg tabular-nums">{value}</span>
      <span className="text-body text-fg-muted">{label}</span>
    </p>
  );
}

/** Bar fill per badge tone (decorative; the list under the bar carries the numbers). */
const toneFill: Record<BadgeTone, string> = {
  neutral: "bg-neutral-border",
  info: "bg-info-fg",
  success: "bg-success-fg",
  warning: "bg-warning-border",
  danger: "bg-danger-fg",
  purple: "bg-purple-fg",
  teal: "bg-teal-fg",
  brand: "bg-brand",
  ai: "bg-ai-fg",
};

/** A simple proportional bar of today's orders by status — decorative, hidden from assistive technology. */
function StatusBar({ items }: { items: readonly { status: string; count: number }[] }) {
  const total = items.reduce((sum, i) => sum + i.count, 0);
  if (total === 0) return null;
  return (
    <div aria-hidden="true" data-testid="today-status-bar" className="flex h-2.5 w-full gap-0.5 overflow-hidden rounded-full bg-sunken">
      {items.map((i) => {
        const key = orderBadgeKey(i.status);
        const tone = key ? orderStatusMeta[key].tone : "neutral";
        return <span key={i.status} className={toneFill[tone]} style={{ width: `${(i.count / total) * 100}%` }} />;
      })}
    </div>
  );
}

function RowList({ label, children }: { label: string; children: ReactNode }) {
  return (
    <ul aria-label={label} className="flex flex-col divide-y divide-line">
      {children}
    </ul>
  );
}

function PendingApprovalTile() {
  const { api, access } = useSession();
  const showMoney = access.canSome("prices.read.sell");
  const { state, retry } = useTile<OrderPage>(
    (signal) => listOrders(api, new URLSearchParams({ status: "pending_approval", limit: String(PENDING_PAGE) }).toString(), signal),
    orderErrorMessage,
  );
  return (
    <Tile id="pending" title="Onay bekleyen siparişler" Icon={ClipboardCheck} href={`${ORDERS_HREF}?durum=pending_approval`} linkLabel="Tümünü gör" state={state} retry={retry}>
      {(page) =>
        page.items.length === 0 ? (
          <p className="text-body text-fg-muted">Onay bekleyen sipariş yok.</p>
        ) : (
          <>
            <Kpi value={countText(page.items.length, page.page.hasMore)} label="sipariş onay bekliyor" />
            <RowList label="Son onay bekleyenler">
              {page.items.slice(0, TILE_ROWS).map((o) => (
                <li key={o.id} className="flex items-center justify-between gap-3 py-1.5 text-body">
                  <span className="flex min-w-0 flex-col">
                    <Link href={orderHref(o.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
                      {o.number}
                    </Link>
                    <span className="truncate text-caption text-fg-muted">
                      {o.customerName ?? "—"} · {dateTime.format(new Date(o.createdAt))}
                    </span>
                  </span>
                  {showMoney && o.grandTotal !== null && o.grandTotal !== undefined ? (
                    <span className="shrink-0 tabular-nums">{formatMoney(o.grandTotal, o.currency)}</span>
                  ) : null}
                </li>
              ))}
            </RowList>
          </>
        )
      }
    </Tile>
  );
}

function TodayOrdersTile() {
  const { api, access } = useSession();
  const [today] = useState(() => istanbulToday());
  const { state, retry } = useTile<OrdersReport>((signal) => getOrdersReport(api, { from: today, to: today }, [], signal), reportErrorMessage);
  return (
    <Tile id="today" title="Bugünkü siparişler" Icon={ShoppingCart} href={`${reportHref("orders")}?baslangic=${today}&bitis=${today}`} linkLabel="Sipariş raporu" state={state} retry={retry}>
      {(report) => {
        const summary = summarizeOrders(report.byStatus);
        // Money only when the API sends it (unconditional prices.read.sell) — never for roles without price permission.
        const money = report.amountsVisible && access.canSome("prices.read.sell") ? summary.amounts : [];
        if (summary.total === 0) return <p className="text-body text-fg-muted">Bugün henüz sipariş oluşturulmadı.</p>;
        return (
          <>
            <Kpi value={String(summary.total)} label="sipariş bugün oluşturuldu" />
            {money.length > 0 ? (
              <p className="text-body text-fg">
                Tutar (iptal hariç):{" "}
                <span className="font-medium tabular-nums">{money.map((m) => formatMoney(m.amount, m.currency)).join(" · ")}</span>
              </p>
            ) : null}
            <StatusBar items={summary.byStatus} />
            <RowList label="Bugünkü siparişler durumlara göre">
              {summary.byStatus.map((s) => (
                <li key={s.status} className="flex items-center justify-between gap-3 py-1.5 text-body">
                  <OrderStatus status={s.status} />
                  <span className="tabular-nums" aria-label={`${orderStatusLabel(s.status)}: ${s.count}`}>
                    {s.count}
                  </span>
                </li>
              ))}
            </RowList>
          </>
        );
      }}
    </Tile>
  );
}

function CriticalStockTile() {
  const { api } = useSession();
  // #137: each variant against its own minimum stock (the report's default mode), all warehouses.
  const { state, retry } = useTile<CriticalStockReport>(
    (signal) => getCriticalStock(api, { threshold: null, warehouseId: "" }, null, signal),
    reportErrorMessage,
  );
  return (
    <Tile id="critical" title="Kritik stok" Icon={PackageMinus} href={reportHref("critical-stock")} linkLabel="Kritik stok raporu" state={state} retry={retry}>
      {(report) =>
        report.items.length === 0 ? (
          report.thresholdsConfigured ? (
            <p className="text-body text-fg-muted">Minimum stoğun altına inen ürün yok.</p>
          ) : (
            <p className="text-body text-fg-muted">
              Henüz minimum stok tanımlanmadı. Ürün düzenleme ekranındaki “Minimum stok” bölümünden (depoya özel eşik için ürünün stok sayfasından) değer
              girin; satılabilir miktarı bu değere inen ürünler burada sayılır.
            </p>
          )
        ) : (
          <>
            <Kpi value={countText(report.items.length, report.page.hasMore)} label="ürün minimum stokta veya altında" />
            <RowList label="Minimum stoğun altındaki ürünler">
              {report.items.slice(0, TILE_ROWS).map((item) => (
                <li key={item.id} className="flex items-center justify-between gap-3 py-1.5 text-body">
                  <span className="flex min-w-0 flex-col">
                    <span className="font-medium text-fg">{item.sku}</span>
                    <span className="truncate text-caption text-fg-muted">{item.name}</span>
                  </span>
                  <span className="flex shrink-0 flex-col items-end gap-0.5 text-right">
                    <Badge tone={Number(item.available) <= 0 ? "danger" : "warning"} className="tabular-nums">
                      satılabilir {formatQuantity(item.available)}
                    </Badge>
                    <span className="text-caption text-fg-muted tabular-nums">
                      {item.minQuantity !== null && item.minQuantity !== undefined ? `min. ${formatQuantity(item.minQuantity)} ` : ""}
                      {item.unit}
                    </span>
                  </span>
                </li>
              ))}
            </RowList>
          </>
        )
      }
    </Tile>
  );
}

function MyTasksTile() {
  const { api, state: session } = useSession();
  const me = session.status === "ready" ? session.user.id : null;
  const { state, retry } = useTile<TaskSummary[]>((signal) => listMyTasks(api, "all", signal), orderErrorMessage);
  return (
    <Tile id="tasks" title="Görevlerim" Icon={ClipboardList} href={TASKS_HREF} linkLabel="Görevlere git" state={state} retry={retry}>
      {(tasks) => {
        const mine = tasks.filter((t) => t.assignedTo !== null && t.assignedTo === me);
        const pool = tasks.filter((t) => t.assignedTo === null);
        if (mine.length === 0 && pool.length === 0) return <p className="text-body text-fg-muted">Size atanmış veya havuzda bekleyen görev yok.</p>;
        return (
          <>
            <Kpi value={String(mine.length)} label="görev size atanmış" />
            <p className="text-body text-fg-muted">Havuzda {pool.length} görev bekliyor.</p>
            {mine.length > 0 ? (
              <RowList label="Bana atanan görevler">
                {mine.slice(0, TILE_ROWS).map((t) => (
                  <li key={t.id} className="flex items-center justify-between gap-3 py-1.5 text-body">
                    <span className="flex min-w-0 flex-col">
                      <Link href={taskHref(t.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
                        {taskTypeLabels[t.type] ?? t.type} · Sipariş {t.orderNumber}
                      </Link>
                      <span className="text-caption text-fg-muted tabular-nums">
                        {formatQuantity(t.quantityPicked)} / {formatQuantity(t.quantityToPick)} toplandı
                      </span>
                    </span>
                    <TaskStatusBadge status={t.status} />
                  </li>
                ))}
              </RowList>
            ) : null}
          </>
        );
      }}
    </Tile>
  );
}

function WhatsAppTile() {
  const { api } = useSession();
  const { state, retry } = useTile<OrderParseListItem[]>((signal) => listOrderParses(api, { status: "needs_review", limit: PARSES_PAGE, signal }), orderErrorMessage);
  return (
    <Tile id="whatsapp" title="İncelenecek WhatsApp siparişleri" Icon={Bot} href={WHATSAPP_ORDER_HREF} linkLabel="WhatsApp'tan sipariş" state={state} retry={retry}>
      {(parses) =>
        parses.length === 0 ? (
          <p className="text-body text-fg-muted">İnceleme bekleyen ayrıştırma yok.</p>
        ) : (
          <>
            <Kpi value={countText(parses.length, parses.length >= PARSES_PAGE)} label="mesaj incelemenizi bekliyor" />
            <RowList label="İnceleme bekleyen mesajlar">
              {parses.slice(0, TILE_ROWS).map((p) => (
                <li key={p.id} className="flex items-center justify-between gap-3 py-1.5 text-body">
                  <Link href={orderParseHref(p.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
                    {dateTime.format(new Date(p.createdAt))}
                  </Link>
                  <span className="text-caption text-fg-muted">
                    {String(p.summary.lines)} satır · {String(p.summary.matched)} eşleşti
                  </span>
                </li>
              ))}
            </RowList>
          </>
        )
      }
    </Tile>
  );
}

/** Plan and trial from GET /me entitlements — no request. Free trial: no payment is taken. */
function PlanTile() {
  const { entitlements } = useSession();
  const [now] = useState(() => new Date());
  const days = entitlements.status === "trialing" ? trialDaysLeft(entitlements.trialEndsAt, now) : null;
  const state: TileState<null> = { status: "ready", data: null };
  return (
    <Tile id="plan" title="Paket" Icon={Sparkles} href={PLAN_PAGE_HREF} linkLabel="Paket ve modüller" state={state} retry={() => undefined}>
      {() => (
        <>
          <p className="flex flex-wrap items-center gap-2">
            <span className="text-h2 font-semibold text-fg">{planLabel(entitlements.plan)}</span>
            {entitlements.status ? (
              <Badge tone={entitlements.status === "expired" ? "warning" : entitlements.status === "trialing" ? "info" : "success"}>
                {subscriptionStatusLabels[entitlements.status]}
              </Badge>
            ) : null}
          </p>
          {days !== null && entitlements.trialEndsAt ? (
            <p className="text-body text-fg">
              Deneme süresi {days <= 0 ? "bugün bitiyor" : `${days} gün sonra bitiyor`} ({dateLong.format(new Date(entitlements.trialEndsAt))}). Ödeme alınmaz.
            </p>
          ) : null}
          {entitlements.status === "expired" ? (
            <p className="text-body text-fg">Deneme süresi bitti; işletmeniz Temel paketle devam ediyor. Verileriniz silinmedi.</p>
          ) : null}
        </>
      )}
    </Tile>
  );
}

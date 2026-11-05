"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { ArrowLeft, CircleAlert, Lock, RotateCcw } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { NegativeStockDialog } from "@/components/inventory/negative-stock-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { availableActions, isDraftEditable, isInFulfilment, type OrderAction } from "@/lib/orders/actions";
import { ORDERS_HREF, getOrder, transitionOrder, type LoadedOrder, type OrderTransition, type TransitionBody } from "@/lib/orders/api";
import {
  ORDER_BUSY_FINAL_MESSAGE,
  isInsufficientStock,
  isOrderBusy,
  isOrderVersionConflict,
  isPriceMissing,
  isStaleState,
  needsStockConfirmation,
  orderErrorMessage,
  readShortages,
  unpricedLineIndexes,
  type Shortage,
} from "@/lib/orders/errors";
import { orderSourceLabels } from "@/lib/orders/labels";
import { orderPermissions, type OrderPermissions } from "@/lib/orders/permissions";
import { useSession } from "@/lib/session/session-context";
import { FulfillmentBadge, OrderStatus } from "./order-badges";
import { ReasonDialog } from "./order-actions";
import { OrderDraftEditor } from "./order-draft-editor";
import { FulfilmentSection, OrderLinesTable, OrderTotalsPanel, ShortagesPanel } from "./order-lines";
import { OrderTimeline } from "./order-timeline";
import { useCustomerNames, useWarehouses, warehouseLabel } from "./use-order-reference";

type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; loaded: LoadedOrder };

const SUCCESS: Record<OrderTransition, string> = {
  submit: "Sipariş onaya gönderildi",
  withdraw: "Sipariş taslağa geri çekildi",
  reject: "Sipariş reddedildi ve taslağa döndü",
  approve: "Sipariş onaylandı; stok ayrıldı",
  cancel: "Sipariş iptal edildi",
};

const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short", timeZone: "Europe/Istanbul" });
const dateOnly = new Intl.DateTimeFormat("tr-TR", { dateStyle: "long", timeZone: "UTC" });
const formatDate = (value: string) => dateOnly.format(new Date(`${value}T00:00:00Z`));

function BackLink() {
  return (
    <Link href={ORDERS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Siparişler
    </Link>
  );
}

/** /siparisler/[id]: order detail, draft editing, approval actions, fulfilment (read only) and the status history. */
export function OrderDetailPage({ orderId }: { orderId: string }) {
  const { api, role } = useSession();
  const permissions = orderPermissions(role);
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!permissions.read) return;
    const controller = new AbortController();
    getOrder(api, orderId, controller.signal)
      .then((loaded) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", loaded });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: orderErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, orderId, attempt, permissions.read]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  if (!permissions.read) {
    return (
      <>
        <BackLink />
        <PageHeader title="Sipariş" />
        <section aria-label="Sipariş durumu" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Siparişi görme yetkiniz yok" description="Depo personeli siparişleri toplama görevleri üzerinden görür." />
        </section>
      </>
    );
  }
  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Sipariş" />
        <p role="status" className="text-body text-fg-muted">
          Sipariş yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Sipariş" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Sipariş bulunamadı" : "Sipariş yüklenemedi"}
            description={load.notFound ? "Kayıt bu işletmeye ait değil veya bağlantı hatalı." : load.message}
            action={
              load.notFound ? undefined : (
                <Button
                  onClick={() => {
                    setLoad({ status: "loading" });
                    reload();
                  }}
                >
                  Tekrar dene
                </Button>
              )
            }
          />
        </div>
      </>
    );
  }
  return <OrderView loaded={load.loaded} permissions={permissions} onLoaded={(loaded) => setLoad({ status: "ready", loaded })} onReload={reload} />;
}

type Notice = { tone: "error" | "warning" | "info"; title: string; text: string } | null;

function OrderView({
  loaded,
  permissions,
  onLoaded,
  onReload,
}: {
  loaded: LoadedOrder;
  permissions: OrderPermissions;
  onLoaded: (loaded: LoadedOrder) => void;
  onReload: () => void;
}) {
  const { api, state } = useSession();
  const { toast } = useToast();
  const { order } = loaded.detail;
  const warehouses = useWarehouses();
  const customers = useCustomerNames([order.customerId]);
  const currentUserId = state.status === "ready" ? state.user.id : null;

  const [keys] = useState(() => new IdempotencyKeys());
  const [pending, setPending] = useState<OrderTransition | null>(null);
  const [retrying, setRetrying] = useState(false);
  const [notice, setNotice] = useState<Notice>(null);
  const [conflict, setConflict] = useState(false);
  const [shortages, setShortages] = useState<Shortage[] | null>(null);
  const [confirmShortages, setConfirmShortages] = useState<Shortage[] | null>(null);
  const [reasonFor, setReasonFor] = useState<"reject" | "cancel" | null>(null);
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [unpriced, setUnpriced] = useState<number[]>([]);
  const [dirty, setDirty] = useState(false);
  const noticeRef = useRef<HTMLDivElement>(null);
  const shortagesRef = useRef<HTMLDivElement>(null);

  const actions = availableActions(order, permissions);
  const editable = isDraftEditable(order, permissions);
  const fulfilment = isInFulfilment(order.status);
  const submitBlock =
    order.status !== "draft"
      ? null
      : dirty
        ? "Önce taslaktaki değişiklikleri kaydedin."
        : loaded.detail.lines.length === 0
          ? "Onaya göndermek için en az bir satır ekleyin."
          : order.totals?.hasUnpricedLines
            ? "Fiyatı olmayan satırlar var; fiyatlanmadan onaya gönderilemez."
            : null;

  const show = (next: Notice) => {
    setNotice(next);
    if (next) requestAnimationFrame(() => noticeRef.current?.focus());
  };

  const run = async (action: OrderTransition, body?: TransitionBody) => {
    if (pending) return;
    setPending(action);
    setRetrying(false);
    show(null);
    setReasonError(null);
    const key = keys.keyFor({ id: order.id, version: order.version, action, body: body ?? null });
    try {
      const next = await withBusyRetry(() => transitionOrder(api, order.id, action, { etag: loaded.etag, idempotencyKey: key, body }), {
        isBusy: isOrderBusy,
        onRetry: () => setRetrying(true),
      });
      keys.reset();
      setShortages(null);
      setConfirmShortages(null);
      setReasonFor(null);
      setUnpriced([]);
      setConflict(false);
      toast({ tone: "success", title: `${next.detail.order.number}: ${SUCCESS[action]}` });
      onLoaded(next);
    } catch (error) {
      handleError(action, error);
    } finally {
      setPending(null);
      setRetrying(false);
    }
  };

  const handleError = (action: OrderTransition, error: unknown) => {
    if (action === "approve" && needsStockConfirmation(error)) {
      setConfirmShortages(readShortages(error));
      return;
    }
    setConfirmShortages(null);
    if (isInsufficientStock(error)) {
      setShortages(readShortages(error));
      requestAnimationFrame(() => shortagesRef.current?.querySelector("h2")?.focus());
      return;
    }
    if (reasonFor && error instanceof ApiError && error.status === 422) {
      setReasonError(orderErrorMessage(error));
      return;
    }
    setReasonFor(null);
    if (isOrderVersionConflict(error)) {
      setConflict(true);
      return;
    }
    if (isStaleState(error)) {
      show({ tone: "warning", title: "Sipariş durumu değişmiş", text: orderErrorMessage(error) });
      onReload();
      return;
    }
    if (isOrderBusy(error)) {
      show({ tone: "error", title: "İşlem tamamlanamadı", text: ORDER_BUSY_FINAL_MESSAGE });
      return;
    }
    if (isPriceMissing(error)) setUnpriced(unpricedLineIndexes(error));
    show({ tone: "error", title: "İşlem tamamlanamadı", text: orderErrorMessage(error) });
  };

  const onAction = (a: OrderAction) => {
    if (a.needsReason) {
      setReasonError(null);
      setReasonFor(a.action as "reject" | "cancel");
      return;
    }
    void run(a.action, a.action === "approve" ? { confirmNegativeStock: false } : undefined);
  };

  const skuOf = (variantId: string, fallback: string | null) => loaded.detail.lines.find((l) => l.variantId === variantId)?.sku ?? fallback ?? variantId;
  const customer = customers.get(order.customerId);

  return (
    <>
      <BackLink />
      <PageHeader
        title={`Sipariş ${order.number}`}
        description={customer ? `${customer.name} · ${customer.code}` : undefined}
        actions={
          <span className="flex flex-wrap items-center gap-2">
            <OrderStatus status={order.status} />
            <FulfillmentBadge status={order.fulfillmentStatus} showNone={fulfilment} />
            {order.hasShortage ? <Badge tone="warning">Depoda eksik bildirildi</Badge> : null}
          </span>
        }
      />

      {conflict ? (
        <FormAlert
          tone="warning"
          title="Sipariş başkası tarafından değiştirildi"
          actions={
            <Button
              size="sm"
              icon={<RotateCcw className="size-4" />}
              onClick={() => {
                setConflict(false);
                onReload();
              }}
            >
              Yeniden yükle
            </Button>
          }
        >
          Bu sipariş siz bakarken güncellendi; işleminiz uygulanmadı. Güncel hâlini yükleyip kontrol edin, ardından tekrar deneyin.
        </FormAlert>
      ) : null}
      {notice ? (
        <FormAlert ref={noticeRef} tone={notice.tone} title={notice.title}>
          {notice.text}
        </FormAlert>
      ) : null}
      {shortages ? (
        <div ref={shortagesRef}>
          <ShortagesPanel shortages={shortages} lines={loaded.detail.lines} onDismiss={() => setShortages(null)} />
        </div>
      ) : null}

      <section aria-labelledby="order-summary" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
        <h2 id="order-summary" className="sr-only">
          Sipariş bilgileri
        </h2>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-2 text-body sm:grid-cols-2 lg:grid-cols-3">
          <Info label="Müşteri" value={customer ? `${customer.name} (${customer.code})` : "Yükleniyor…"} />
          <Info label="Depo" value={warehouseLabel(warehouses, order.warehouseId)} />
          <Info label="Kaynak" value={orderSourceLabels[order.source] ?? order.source} />
          <Info label="Oluşturma" value={dateTime.format(new Date(order.createdAt))} />
          <Info label="Fiyat tarihi" value={formatDate(order.priceDate)} />
          <Info label="İstenen teslim" value={order.requestedDeliveryDate ? formatDate(order.requestedDeliveryDate) : "—"} />
          <Info label="Para birimi" value={order.currency} />
          {order.approvedAt ? <Info label="Onay" value={dateTime.format(new Date(order.approvedAt))} /> : null}
          {order.cancelledAt ? <Info label="İptal" value={`${dateTime.format(new Date(order.cancelledAt))}${order.cancelReason ? ` — ${order.cancelReason}` : ""}`} /> : null}
          {order.notes && !editable ? <Info label="Not" value={order.notes} /> : null}
        </dl>
      </section>

      {actions.length > 0 ? (
        <section aria-labelledby="order-actions" className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
          <h2 id="order-actions" className="text-h3 text-fg">
            İşlemler
          </h2>
          <div className="flex flex-wrap gap-2">
            {actions.map((a) => (
              <Button
                key={a.action}
                variant={a.variant}
                loading={pending === a.action}
                disabled={(pending !== null && pending !== a.action) || (a.action === "submit" && submitBlock !== null)}
                onClick={() => onAction(a)}
              >
                {a.label}
              </Button>
            ))}
          </div>
          {submitBlock && actions.some((a) => a.action === "submit") ? <p className="text-caption text-fg-muted">{submitBlock}</p> : null}
          {retrying ? (
            <p role="status" className="text-caption text-fg-muted">
              Sipariş meşgul; bir kez daha deneniyor…
            </p>
          ) : null}
        </section>
      ) : null}

      {editable ? (
        <OrderDraftEditor
          key={`${order.id}-${order.version}`}
          loaded={loaded}
          permissions={permissions}
          unpricedLineIndexes={unpriced}
          onSaved={(next) => {
            setUnpriced([]);
            onLoaded(next);
          }}
          onDirtyChange={setDirty}
          onReload={onReload}
        />
      ) : (
        <section aria-labelledby="order-lines" className="flex flex-col gap-3">
          <h2 id="order-lines" className="text-h3 text-fg">
            Satırlar
          </h2>
          {order.status === "draft" && permissions.submit && !permissions.editDraft ? (
            <p className="text-caption text-fg-muted">Taslağı düzenleme yetkiniz yok; satırlar salt okunur. Düzeltme için yöneticinize başvurun.</p>
          ) : null}
          <OrderLinesTable detail={loaded.detail} showFulfilment={fulfilment} />
        </section>
      )}

      {order.totals ? <OrderTotalsPanel totals={order.totals} currency={order.currency} /> : null}

      {fulfilment ? <FulfilmentSection detail={loaded.detail} /> : null}

      <OrderTimeline history={loaded.detail.history} currentUserId={currentUserId} />

      <ReasonDialog
        key={reasonFor ?? "closed"}
        action={reasonFor}
        orderNumber={order.number}
        holdsReservation={order.status === "approved" || order.status === "picking" || order.status === "packed"}
        busy={pending === reasonFor && reasonFor !== null}
        error={reasonError}
        onCancel={() => setReasonFor(null)}
        onConfirm={(reason) => reasonFor && void run(reasonFor, { reason })}
      />
      <NegativeStockDialog
        open={confirmShortages !== null}
        shortages={confirmShortages ?? []}
        skuOf={skuOf}
        confirming={pending === "approve"}
        onCancel={() => setConfirmShortages(null)}
        onConfirm={() => void run("approve", { confirmNegativeStock: true })}
        description="Bazı satırlarda satılabilir stok yetmiyor. İşletmenizin negatif stok ayarı “uyar” olduğu için onay ancak açık teyidinizle yapılır."
        confirmLabel="Yine de onayla"
        footnote="Onaylarsanız tüm satırlar için stok ayrılır ve satılabilir stok eksiye düşer."
      />
    </>
  );
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col">
      <dt className="text-caption text-fg-muted">{label}</dt>
      <dd className="text-fg">{value}</dd>
    </div>
  );
}

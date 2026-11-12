"use client";

import Link from "next/link";
import { ArrowLeftRight, ArrowRight, CircleAlert, Lock, Plus, RefreshCw } from "@/components/ui/icons";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/inventory/labels";
import { formatQuantity, toNumber } from "@/lib/inventory/quantity";
import { NEW_TRANSFER_HREF, documentListQuery, listTransfers, transferHref, type TransferSummary } from "@/lib/stock-documents/api";
import { TRANSFER_FILTERS, type TransferFilter } from "@/lib/stock-documents/labels";
import { parseTransferListQuery, toTransferListSearch } from "@/lib/stock-documents/list-query";
import { transferPermissions } from "@/lib/stock-documents/permissions";
import { useSession } from "@/lib/session/session-context";
import { useUrlQuery } from "@/lib/use-url-query";
import { useWarehouses } from "@/components/inventory/use-inventory-data";
import { DiscrepancyBadge, OverdueBadge, TransferStatusBadge } from "./badges";
import { useDocumentList } from "./use-document-list";

/** /depo/transferler: two-stage warehouse transfers (W11), newest first, keyset paged; filters in the URL. */
export function TransferList() {
  const { api, access } = useSession();
  const permissions = transferPermissions(access);
  const { query, latest, navigate } = useUrlQuery(parseTransferListQuery, toTransferListSearch);
  const { state: warehouses } = useWarehouses();
  const statuses = TRANSFER_FILTERS[query.filter].statuses;
  const { data, more, loadMore, reload } = useDocumentList<TransferSummary>(
    toTransferListSearch(query),
    (cursor, signal) => listTransfers(api, documentListQuery({ statuses, warehouseId: query.warehouseId || null }, cursor), signal),
    permissions.read,
  );

  if (!permissions.read) {
    return (
      <>
        <PageHeader title="Transferler" />
        <section aria-label="Transferler" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Transferleri görme yetkiniz yok" description="Rolünüz depolar arası transferlere izin vermiyor. Gerekirse işletme yöneticinizle görüşün." />
        </section>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Transferler"
        description="Depolar arası transfer iki aşamalıdır: gönderilen mal teslim alınana kadar yoldadır ve hiçbir depoda satılamaz."
        actions={
          <span className="flex flex-wrap gap-2">
            <Button size="lg" variant="secondary" icon={<RefreshCw className="size-5" />} loading={data.status === "loading"} onClick={reload}>
              Yenile
            </Button>
            {permissions.manage ? (
              <Link href={NEW_TRANSFER_HREF} className={cn(buttonClasses("primary", "lg"))}>
                <Plus className="size-5" aria-hidden="true" />
                Yeni transfer
              </Link>
            ) : null}
          </span>
        }
      />
      <section aria-label="Filtreler" className="grid grid-cols-1 gap-3 rounded-xl border border-line bg-card p-4 shadow-e1 sm:grid-cols-2">
        <Select
          label="Durum"
          size="lg"
          value={query.filter}
          onChange={(e) => navigate({ ...latest(), filter: e.target.value as TransferFilter })}
          options={Object.entries(TRANSFER_FILTERS).map(([value, f]) => ({ value, label: f.label }))}
        />
        <Select
          label="Depo (kaynak veya hedef)"
          size="lg"
          placeholder="Tüm depolar"
          value={query.warehouseId}
          disabled={warehouses.status !== "ready"}
          onChange={(e) => navigate({ ...latest(), warehouseId: e.target.value })}
          options={warehouses.status === "ready" ? warehouses.data.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` })) : []}
        />
      </section>

      {data.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Transferler yükleniyor…
        </p>
      ) : data.status === "error" ? (
        <div role="alert" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState icon={<CircleAlert className="size-6" />} title="Transferler yüklenemedi" description={data.message} action={<Button size="lg" onClick={reload}>Tekrar dene</Button>} />
        </div>
      ) : data.items.length === 0 ? (
        <section aria-label="Transferler" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState
            icon={<ArrowLeftRight className="size-6" />}
            title="Bu filtrede transfer yok"
            description={permissions.manage ? "Bir depodan diğerine mal göndermek için yeni transfer oluşturun." : "Size gönderilen transferler burada görünür."}
            action={
              permissions.manage ? (
                <Link href={NEW_TRANSFER_HREF} className={buttonClasses("primary", "lg")}>
                  <Plus className="size-5" aria-hidden="true" />
                  Yeni transfer
                </Link>
              ) : undefined
            }
          />
        </section>
      ) : (
        <ul aria-label="Transferler" className="flex flex-col gap-3">
          {data.items.map((t) => (
            <li key={t.id} data-testid={`transfer-${t.number}`} className="flex flex-col gap-2 rounded-xl border border-line bg-card p-4 shadow-e1">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <h2 className="text-h3 text-fg">
                  <Link href={transferHref(t.id)} className="underline-offset-2 hover:underline">
                    {t.number}
                  </Link>
                </h2>
                <span className="flex flex-wrap gap-1">
                  <TransferStatusBadge status={t.status} />
                  {t.isOverdue ? <OverdueBadge /> : null}
                  {t.hasDiscrepancy ? <DiscrepancyBadge /> : null}
                </span>
              </div>
              <p className="flex flex-wrap items-center gap-1 text-body text-fg-secondary">
                <span>{t.sourceWarehouseName ?? "Kaynak depo"}</span>
                <ArrowRight className="size-4" aria-hidden="true" />
                <span className="sr-only">hedef:</span>
                <span>{t.destinationWarehouseName ?? "Hedef depo"}</span>
              </p>
              <p className="text-body text-fg-secondary">
                {formatQuantity(t.lineCount)} satır · gönderilen {formatQuantity(t.quantity)} · teslim alınan {formatQuantity(t.receivedQuantity)}
                {toNumber(t.inTransitQuantity) > 0 ? ` · yolda ${formatQuantity(t.inTransitQuantity)}` : ""}
                {toNumber(t.lostQuantity) > 0 ? ` · kayıp ${formatQuantity(t.lostQuantity)}` : ""}
                {toNumber(t.returnedQuantity) > 0 ? ` · iade ${formatQuantity(t.returnedQuantity)}` : ""}
              </p>
              <p className="text-caption text-fg-muted">
                Oluşturma {formatDateTime(t.createdAt)}
                {t.dispatchedAt ? ` · Gönderim ${formatDateTime(t.dispatchedAt)}` : ""}
                {t.lastReceivedAt ? ` · Son teslim ${formatDateTime(t.lastReceivedAt)}` : ""}
              </p>
            </li>
          ))}
        </ul>
      )}
      {more.error ? (
        <p role="alert" className="text-body text-danger-fg">
          {more.error}
        </p>
      ) : null}
      {data.status === "ready" && data.hasMore ? (
        <Button size="lg" variant="secondary" className="self-center" loading={more.loading} onClick={() => void loadMore()}>
          Daha fazla yükle
        </Button>
      ) : null}
    </>
  );
}

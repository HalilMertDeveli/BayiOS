"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { CircleAlert, ClipboardList, Lock, RefreshCw, Truck } from "@/components/ui/icons";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { cn } from "@/lib/cn";
import { TASKS_HREF, listShipments, shipmentHref, shipmentListQuery, type ShipmentSummary } from "@/lib/fulfilment/api";
import { fulfilmentErrorMessage } from "@/lib/fulfilment/errors";
import { SHIPMENT_OPEN_STATUSES } from "@/lib/fulfilment/labels";
import { fulfilmentPermissions } from "@/lib/fulfilment/permissions";
import { formatDateTime } from "@/lib/inventory/labels";
import { formatQuantity } from "@/lib/inventory/quantity";
import { useSession } from "@/lib/session/session-context";
import { ShipmentStatusBadge } from "./fulfilment-badges";

type Filter = "open" | "confirmed" | "any";
const STATUSES: Record<Filter, readonly string[]> = { open: SHIPMENT_OPEN_STATUSES, confirmed: ["confirmed"], any: [] };

type ListData =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; items: ShipmentSummary[]; nextCursor: string | null; hasMore: boolean };

type Keyed = { key: string; value: ListData };

/** /depo/sevkiyatlar: shipments created from completed picking tasks (staff: only their own tasks'). */
export function ShipmentList() {
  const { api, access } = useSession();
  const permissions = fulfilmentPermissions(access);
  const [filter, setFilter] = useState<Filter>("open");
  const [attempt, setAttempt] = useState(0);
  const [keyed, setKeyed] = useState<Keyed | null>(null);
  const currentKey = `${filter}|${attempt}`;
  const data: ListData = keyed?.key === currentKey ? keyed.value : { status: "loading" };
  const setData = (value: ListData) => setKeyed({ key: currentKey, value });
  const [more, setMore] = useState<{ loading: boolean; error: string | null }>({ loading: false, error: null });

  useEffect(() => {
    const key = `${filter}|${attempt}`;
    if (!permissions.readDetail) return;
    const controller = new AbortController();
    listShipments(api, shipmentListQuery({ statuses: STATUSES[filter] }), controller.signal)
      .then((page) => !controller.signal.aborted && setKeyed({ key, value: { status: "ready", items: page.items, nextCursor: page.page.nextCursor ?? null, hasMore: page.page.hasMore } }))
      .catch((error: unknown) => !controller.signal.aborted && setKeyed({ key, value: { status: "error", message: fulfilmentErrorMessage(error) } }));
    return () => controller.abort();
  }, [api, filter, attempt, permissions.readDetail]);

  if (!permissions.readDetail) {
    return (
      <>
        <PageHeader title="Sevkiyatlar" />
        <section aria-label="Sevkiyatlar" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Sevkiyatları görme yetkiniz yok" description="Rolünüz sevkiyatları görmeye izin vermiyor. Gerekirse işletme yöneticinizle görüşün." />
        </section>
      </>
    );
  }

  const loadMore = async () => {
    if (data.status !== "ready" || !data.nextCursor) return;
    setMore({ loading: true, error: null });
    try {
      const page = await listShipments(api, shipmentListQuery({ statuses: STATUSES[filter] }, data.nextCursor));
      setData({ status: "ready", items: [...data.items, ...page.items], nextCursor: page.page.nextCursor ?? null, hasMore: page.page.hasMore });
      setMore({ loading: false, error: null });
    } catch (error) {
      setMore({ loading: false, error: fulfilmentErrorMessage(error) });
    }
  };

  return (
    <>
      <PageHeader
        title="Sevkiyatlar"
        description="Tamamlanan toplamalardan oluşan sevkiyatlar: paket kontrolü ve sevk onayı."
        actions={
          <span className="flex flex-wrap gap-2">
            <Link href={TASKS_HREF} className={cn(buttonClasses("secondary", "lg"), "min-h-12")}>
              <ClipboardList className="size-5" aria-hidden="true" />
              Görevler
            </Link>
            <Button size="lg" variant="secondary" className="min-h-12" icon={<RefreshCw className="size-5" />} loading={data.status === "loading"} onClick={() => setAttempt((n) => n + 1)}>
              Yenile
            </Button>
          </span>
        }
      />
      <Select
        label="Durum"
        size="lg"
        className="sm:max-w-xs"
        value={filter}
        onChange={(e) => setFilter(e.target.value as Filter)}
        options={[
          { value: "open", label: "Sevk bekleyen (taslak + paketlenen)" },
          { value: "confirmed", label: "Sevk edilen" },
          { value: "any", label: "Tümü" },
        ]}
      />
      {data.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Sevkiyatlar yükleniyor…
        </p>
      ) : data.status === "error" ? (
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<CircleAlert className="size-6" />} title="Sevkiyatlar yüklenemedi" description={data.message} action={<Button size="lg" onClick={() => setAttempt((n) => n + 1)}>Tekrar dene</Button>} />
        </div>
      ) : data.items.length === 0 ? (
        <section aria-label="Sevkiyatlar" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Truck className="size-6" />} title="Bu filtrede sevkiyat yok" description="Bir toplama görevi tamamlandığında taslak sevkiyat burada görünür." />
        </section>
      ) : (
        <ul aria-label="Sevkiyatlar" className="flex flex-col gap-3">
          {data.items.map((s) => (
            <li key={s.id} className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <h2 className="text-h3 text-fg">
                  <Link href={shipmentHref(s.id)} className="underline-offset-2 hover:underline">
                    {s.number}
                  </Link>
                </h2>
                <ShipmentStatusBadge status={s.status} />
              </div>
              <p className="text-body text-fg-secondary">
                Sipariş {s.orderNumber} · {formatQuantity(s.lineCount)} satır · {formatQuantity(s.quantity)} adet
              </p>
              <p className="text-caption text-fg-muted">
                Oluşturma {formatDateTime(s.createdAt)}
                {s.shippedAt ? ` · Sevk ${formatDateTime(s.shippedAt)}` : ""}
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
        <Button size="lg" variant="secondary" className="min-h-12 self-center" loading={more.loading} onClick={() => void loadMore()}>
          Daha fazla yükle
        </Button>
      ) : null}
    </>
  );
}

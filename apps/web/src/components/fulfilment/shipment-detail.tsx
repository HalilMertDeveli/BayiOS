"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useId } from "react";
import Link from "next/link";
import { ArrowLeft, CircleAlert, Lock, PackagePlus, Truck, TriangleAlert, Undo2 } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { cn } from "@/lib/cn";
import {
  SHIPMENTS_HREF,
  cancelShipment,
  confirmShipment,
  getShipment,
  packShipment,
  reportShipmentShortage,
  type LoadedShipment,
  type ShipmentLine,
} from "@/lib/fulfilment/api";
import {
  BUSY_FINAL_MESSAGE,
  fulfilmentErrorMessage,
  isFulfilmentBusy,
  isStaleFulfilment,
  isVersionConflict,
  mismatchedLineIds,
} from "@/lib/fulfilment/errors";
import { shortageReasonLabels } from "@/lib/fulfilment/labels";
import { barcodeMap, emptyPack, newPackage, packProgress, packRequest, packScan, undoOne, type PackState } from "@/lib/fulfilment/pack-check";
import { fulfilmentPermissions, type FulfilmentPermissions } from "@/lib/fulfilment/permissions";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import { listMovements, movementQuery, type Movement } from "@/lib/inventory/api";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { formatDateTime, movementTypeLabel } from "@/lib/inventory/labels";
import { formatQuantity, formatSignedQuantity, toNumber } from "@/lib/inventory/quantity";
import { orderHref } from "@/lib/orders/api";
import { orderPermissions } from "@/lib/orders/permissions";
import { useSession } from "@/lib/session/session-context";
import { ShipmentStatusBadge } from "./fulfilment-badges";
import { ShortageDialog } from "./pick-screen";
import { ScanFeedback, ScanField, type Feedback } from "./scan-field";

type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; loaded: LoadedShipment };

function BackLink() {
  return (
    <Link href={SHIPMENTS_HREF} className="inline-flex min-h-12 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Sevkiyatlar
    </Link>
  );
}

/** /depo/sevkiyatlar/[id]: pack check (#38), packing shortage and shipment confirmation (#39 on the web). */
export function ShipmentDetailPage({ shipmentId }: { shipmentId: string }) {
  const { api, access } = useSession();
  const permissions = fulfilmentPermissions(access);
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!permissions.readDetail) return;
    const controller = new AbortController();
    getShipment(api, shipmentId, controller.signal)
      .then((loaded) => !controller.signal.aborted && setLoad({ status: "ready", loaded }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: fulfilmentErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, shipmentId, attempt, permissions.readDetail]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  if (!permissions.readDetail) {
    return (
      <>
        <BackLink />
        <PageHeader title="Sevkiyat" />
        <section aria-label="Sevkiyat" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Sevkiyatı görme yetkiniz yok" />
        </section>
      </>
    );
  }
  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Sevkiyat" />
        <p role="status" className="text-body text-fg-muted">
          Sevkiyat yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Sevkiyat" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Sevkiyat bulunamadı" : "Sevkiyat yüklenemedi"}
            description={load.notFound ? "Sevkiyat size ait bir göreve bağlı değil veya bağlantı hatalı." : load.message}
            action={load.notFound ? undefined : <Button size="lg" onClick={() => (setLoad({ status: "loading" }), reload())}>Tekrar dene</Button>}
          />
        </div>
      </>
    );
  }
  return <ShipmentView loaded={load.loaded} permissions={permissions} onLoaded={(loaded) => setLoad({ status: "ready", loaded })} onReload={reload} />;
}

/** Reason for cancelling an unconfirmed shipment (required by the API). Resolves with an error text, or null when done. */
function CancelShipmentDialog({
  open,
  number,
  onClose,
  onSubmit,
}: {
  open: boolean;
  number: string;
  onClose: () => void;
  onSubmit: (reason: string) => Promise<string | null>;
}) {
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    const text = reason.trim();
    if (!text) {
      setError("İptal sebebini yazın.");
      return;
    }
    setBusy(true);
    setError(await onSubmit(text));
    setBusy(false);
  };
  return (
    <Dialog
      open={open}
      onClose={() => !busy && onClose()}
      role="alertdialog"
      dismissOnBackdrop={false}
      title={`${number} iptal edilsin mi?`}
      description="Sevkiyat iptal edilir; stok hareketi olmaz ve sipariş yeniden toplanana kadar paketlendi durumunda kalır."
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" size="lg" loading={busy} onClick={() => void submit()}>
            Sevkiyatı iptal et
          </Button>
        </>
      }
    >
      <Textarea label="İptal sebebi" value={reason} maxLength={500} error={error ?? undefined} onChange={(e) => setReason(e.target.value)} />
    </Dialog>
  );
}

type Notice = { tone: "error" | "warning" | "info" | "success"; title: string; text: string } | null;

function ShipmentView({
  loaded,
  permissions,
  onLoaded,
  onReload,
}: {
  loaded: LoadedShipment;
  permissions: FulfilmentPermissions;
  onLoaded: (loaded: LoadedShipment) => void;
  onReload: () => void;
}) {
  const uid = useId();
  const { api, access } = useSession();
  const { toast } = useToast();
  const { shipment } = loaded;
  const canReadOrder = orderPermissions(access).read;
  const canReadMovements = inventoryPermissions(access).movements;
  const isDraft = shipment.status === "draft";
  const isOpen = shipment.status === "draft" || shipment.status === "packed";

  const [notice, setNotice] = useState<Notice>(null);
  const [shortageLine, setShortageLine] = useState<ShipmentLine | null>(null);
  const [cancelOpen, setCancelOpen] = useState(false);
  // Shipment lines carry the product barcode (#86): the pack check matches barcode or SKU without loading the task.
  const barcodes = useMemo(() => barcodeMap(shipment.lines), [shipment.lines]);

  const handleError = (error: unknown, title: string) => {
    if (isVersionConflict(error)) {
      setNotice({ tone: "warning", title: "Sevkiyat değişmiş", text: "Sevkiyat siz bakarken güncellendi; güncel hâli yüklendi. Kontrol edip tekrar deneyin." });
      onReload();
      return;
    }
    if (error instanceof ApiError && error.code === "shipment.already_confirmed") {
      setNotice({ tone: "info", title: "Sevkiyat zaten onaylanmış", text: fulfilmentErrorMessage(error) });
      onReload();
      return;
    }
    if (isStaleFulfilment(error)) onReload();
    setNotice({ tone: "error", title, text: isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error) });
  };

  return (
    <>
      <BackLink />
      <PageHeader
        title={`Sevkiyat ${shipment.number}`}
        description={`Sipariş ${shipment.orderNumber}`}
        actions={<ShipmentStatusBadge status={shipment.status} />}
      />
      {notice ? (
        <FormAlert tone={notice.tone} title={notice.title}>
          {notice.text}
        </FormAlert>
      ) : null}

      <section aria-labelledby={`${uid}-shipment-info`} className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
        <h2 id={`${uid}-shipment-info`} className="sr-only">
          Sevkiyat bilgileri
        </h2>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-2 text-body sm:grid-cols-2">
          <Info label="Sipariş" value={canReadOrder ? <Link href={orderHref(shipment.orderId)} className="text-brand-fg underline underline-offset-2">{shipment.orderNumber}</Link> : shipment.orderNumber} />
          <Info label="Oluşturma" value={formatDateTime(shipment.createdAt)} />
          {shipment.packedAt ? <Info label="Paket kontrolü" value={formatDateTime(shipment.packedAt)} /> : null}
          {shipment.packageCount !== null ? <Info label="Koli" value={formatQuantity(shipment.packageCount)} /> : null}
          {shipment.shippedAt ? <Info label="Sevk" value={formatDateTime(shipment.shippedAt)} /> : null}
          {shipment.carrier ? <Info label="Kargo" value={shipment.carrier} /> : null}
          {shipment.trackingNumber ? <Info label="Takip no" value={shipment.trackingNumber} /> : null}
        </dl>
        {isOpen && !permissions.assign ? (
          <p className="text-caption text-fg-muted">Onaylanmamış sevkiyatı görev atama yetkisi olan bir yönetici iptal edebilir.</p>
        ) : null}
      </section>

      {shipment.status === "confirmed" ? (
        <FormAlert tone="success" title="Sevk edildi">
          Stok bu sevkiyat için bir kez düşüldü. Aynı sevkiyat tekrar onaylanamaz.
        </FormAlert>
      ) : null}

      <ShipmentLines
        lines={shipment.lines}
        canShort={isDraft && permissions.reportShortage}
        onShort={setShortageLine}
      />

      {isDraft && permissions.pack ? (
        <PackCheck
          key={`${shipment.id}-${shipment.version}`}
          loaded={loaded}
          barcodes={barcodes}
          onPacked={(next) => {
            setNotice(null);
            toast({ tone: "success", title: `${next.shipment.number}: paket kontrolü kaydedildi` });
            onLoaded(next);
          }}
          onError={(error) => handleError(error, "Paket kontrolü kaydedilmedi")}
        />
      ) : null}

      {isOpen && permissions.ship ? (
        <ConfirmSection
          key={`confirm-${shipment.version}`}
          loaded={loaded}
          onConfirmed={(next) => {
            setNotice(null);
            toast({ tone: "success", title: `${next.shipment.number} sevk edildi` });
            onLoaded(next);
          }}
          onError={(error) => handleError(error, "Sevkiyat onaylanamadı")}
        />
      ) : null}

      {isOpen && permissions.assign ? (
        <section aria-labelledby={`${uid}-shipment-cancel`} className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
          <h2 id={`${uid}-shipment-cancel`} className="text-h3 text-fg">
            Sevkiyatı iptal et
          </h2>
          <p className="text-body text-fg-secondary">
            Stok hareketi olmaz; sipariş paketlendi durumunda kalır. Toplanan ürünler için yerine koyma görevi oluşturulur.
          </p>
          <div>
            <Button variant="danger" onClick={() => setCancelOpen(true)}>
              Sevkiyatı iptal et
            </Button>
          </div>
        </section>
      ) : null}

      <CancelShipmentDialog
        key={cancelOpen ? `cancel-${shipment.version}` : "cancel-closed"}
        open={cancelOpen}
        number={shipment.number}
        onClose={() => setCancelOpen(false)}
        onSubmit={async (reason) => {
          const key = crypto.randomUUID();
          try {
            const { loaded: next, putawayTaskId } = await withBusyRetry(
              () => cancelShipment(api, shipment.id, reason, { idempotencyKey: key, etag: loaded.etag }),
              { isBusy: isFulfilmentBusy },
            );
            setCancelOpen(false);
            toast({ tone: "success", title: `${next.shipment.number} iptal edildi` });
            setNotice({
              tone: "info",
              title: "Sevkiyat iptal edildi",
              text: `Sipariş ${shipment.orderNumber} yeniden toplanana kadar paketlendi durumunda kalır: Görevler ekranından yeni toplama görevi açın, kalan miktarı kapatın veya siparişi iptal edin.${putawayTaskId ? " Toplanan ürünler için yerine koyma görevi oluşturuldu." : ""}`,
            });
            onLoaded(next);
            return null;
          } catch (error) {
            if (isVersionConflict(error) || isStaleFulfilment(error)) {
              setCancelOpen(false);
              handleError(error, "Sevkiyat iptal edilemedi");
              return null;
            }
            return error instanceof ApiError && error.status === 422 ? "İptal sebebini yazın (en fazla 500 karakter)." : fulfilmentErrorMessage(error);
          }
        }}
      />

      {shipment.status === "confirmed" && canReadMovements ? <ShipmentMovements shipmentId={shipment.id} lines={shipment.lines} /> : null}

      <ShortageDialog
        key={shortageLine?.id ?? "closed"}
        line={shortageLine ? { id: shortageLine.id, sku: shortageLine.sku, unit: shortageLine.unit, quantityRemaining: shortageLine.quantity } : null}
        onClose={() => setShortageLine(null)}
        onSubmit={async (quantity, reason) => {
          if (!shortageLine) return null;
          const key = crypto.randomUUID();
          try {
            const next = await withBusyRetry(
              () => reportShipmentShortage(api, shipment.id, shortageLine.id, { quantity, reason }, { idempotencyKey: key, etag: loaded.etag }),
              { isBusy: isFulfilmentBusy },
            );
            toast({ tone: "success", title: `${shortageLine.sku}: eksik bildirildi; sipariş işaretlendi` });
            setShortageLine(null);
            onLoaded(next);
            return null;
          } catch (error) {
            if (isVersionConflict(error) || isStaleFulfilment(error)) {
              setShortageLine(null);
              handleError(error, "Eksik bildirilemedi");
              return null;
            }
            return isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error);
          }
        }}
      />
    </>
  );
}

function Info({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex flex-col">
      <dt className="text-caption text-fg-muted">{label}</dt>
      <dd className="text-fg">{value}</dd>
    </div>
  );
}

function ShipmentLines({ lines, canShort, onShort }: { lines: ShipmentLine[]; canShort: boolean; onShort: (line: ShipmentLine) => void }) {
  const uid = useId();
  return (
    <section aria-labelledby={`${uid}-shipment-lines`} className="flex flex-col gap-2">
      <h2 id={`${uid}-shipment-lines`} className="text-h3 text-fg">
        Satırlar
      </h2>
      <ul className="flex flex-col gap-2">
        {lines.map((line) => {
          const short = toNumber(line.shortQuantity);
          return (
            <li key={line.id} data-testid={`shipment-line-${line.sku}`} className="flex flex-wrap items-center gap-3 rounded-lg border border-line bg-card p-3">
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="font-mono text-body text-fg">{line.sku}</span>
                <span className="truncate text-caption text-fg-muted">
                  {line.name}
                  {line.locationCode ? ` · ${line.locationCode}` : ""}
                </span>
              </span>
              <span className="text-body text-fg">
                Sevk {formatQuantity(line.quantity)} {line.unit}
                <span className="text-fg-muted"> · toplanan {formatQuantity(line.pickedQuantity)}</span>
                {line.packedQuantity !== null ? <span className="text-fg-muted"> · paketlenen {formatQuantity(line.packedQuantity)}</span> : null}
              </span>
              {short > 0 ? (
                <Badge tone="danger">
                  {formatQuantity(short)} eksik{line.shortageReason ? ` · ${shortageReasonLabels[line.shortageReason] ?? line.shortageReason}` : ""}
                </Badge>
              ) : null}
              {canShort && toNumber(line.quantity) > 0 ? (
                <Button size="lg" variant="ghost" className="min-h-12" icon={<TriangleAlert className="size-5" />} onClick={() => onShort(line)}>
                  Eksik bildir
                </Button>
              ) : null}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

/** Scan every item into packages; the packed total per line must match what ships before it can be saved. */
function PackCheck({
  loaded,
  barcodes,
  onPacked,
  onError,
}: {
  loaded: LoadedShipment;
  barcodes: Map<string, string>;
  onPacked: (next: LoadedShipment) => void;
  onError: (error: unknown) => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const { shipment } = loaded;
  const [pack, setPack] = useState<PackState>(emptyPack);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const [saving, setSaving] = useState(false);
  const [mismatch, setMismatch] = useState<string[]>([]);
  const packRef = useRef(pack);
  const keyRef = useRef<{ body: string; key: string } | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const progress = useMemo(() => packProgress(pack, shipment.lines), [pack, shipment.lines]);
  // Scan-first: the field is ready for the scanner as soon as the pack check shows.
  useEffect(() => inputRef.current?.focus(), []);

  const update = (next: PackState) => {
    packRef.current = next;
    setPack(next);
  };

  const onScan = (code: string) => {
    const result = packScan(packRef.current, code, 1, shipment.lines, barcodes);
    if (!result.ok) {
      setFeedback(
        result.reason === "unknown"
          ? { tone: "error", title: "✗ Bu ürün sevkiyatta yok", text: `“${code}” bu sevkiyatın hiçbir satırıyla eşleşmedi. Ürünü koliye koymayın.` }
          : { tone: "error", title: "✗ Fazla paketleme", text: `${result.line?.sku}: sevk edilecek miktarın tamamı zaten okutuldu.` },
      );
      return;
    }
    update(result.state);
    setMismatch([]);
    setFeedback({ tone: "success", title: `✓ ${result.line.sku} → Koli ${result.state.current + 1}`, text: `${formatQuantity(result.packed)} / ${formatQuantity(result.line.quantity)} ${result.line.unit} paketlendi.` });
  };

  const save = async () => {
    const body = packRequest(packRef.current);
    const serialized = JSON.stringify(body);
    if (keyRef.current?.body !== serialized) keyRef.current = { body: serialized, key: crypto.randomUUID() };
    const key = keyRef.current.key;
    setSaving(true);
    try {
      const next = await withBusyRetry(() => packShipment(api, shipment.id, body, { idempotencyKey: key, etag: loaded.etag }), { isBusy: isFulfilmentBusy });
      keyRef.current = null;
      onPacked(next);
    } catch (error) {
      setMismatch(mismatchedLineIds(error));
      onError(error);
    } finally {
      setSaving(false);
    }
  };

  const current = pack.packages[pack.current] ?? {};

  return (
    <section aria-labelledby={`${uid}-pack-check`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
      <h2 id={`${uid}-pack-check`} className="text-h3 text-fg">
        Paket kontrolü (isteğe bağlı)
      </h2>
      <p className="text-body text-fg-secondary">Her ürünü koliye koyarken okutun. Tüm satırlar tamamlanınca kaydedin; isterseniz paket kontrolü olmadan da sevk onaylanabilir.</p>
      <ScanField id={`${uid}-pack-scan`} label="Ürün barkodu veya SKU" hint="Her okutma 1 adet ekler." inputRef={inputRef} onScan={onScan} />
      <ScanFeedback feedback={feedback} idleText={`Koli ${pack.current + 1} açık — okutmaya hazır.`} />
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone="info">Koli {pack.current + 1} / {pack.packages.length}</Badge>
        <Button
          size="lg"
          variant="secondary"
          className="min-h-12"
          icon={<PackagePlus className="size-5" />}
          disabled={Object.keys(current).length === 0}
          onClick={() => {
            update(newPackage(packRef.current));
            setFeedback({ tone: "info", title: `Koli ${packRef.current.current + 1} açıldı` });
            inputRef.current?.focus();
          }}
        >
          Yeni koli
        </Button>
      </div>
      <ul aria-label="Paket kontrolü satırları" className="flex flex-col gap-2">
        {progress.lines.map(({ line, packed, ships }) => {
          const done = packed === ships;
          const wrong = mismatch.includes(line.id);
          return (
            <li
              key={line.id}
              data-testid={`pack-line-${line.sku}`}
              className={cn("flex flex-wrap items-center gap-3 rounded-md border p-3", wrong ? "border-danger-border bg-danger-bg" : done ? "border-success-border bg-success-bg" : "border-line")}
            >
              <span className="flex-1 font-mono text-body text-fg">{line.sku}</span>
              <span className="text-body text-fg">
                {formatQuantity(packed)} / {formatQuantity(ships)} {line.unit}
              </span>
              {done ? <Badge tone="success">Tamam</Badge> : wrong ? <Badge tone="danger">Uyuşmuyor</Badge> : null}
              {(current[line.id] ?? 0) > 0 ? (
                <Button size="lg" variant="ghost" className="min-h-12" icon={<Undo2 className="size-5" />} aria-label={`${line.sku} için son okutmayı geri al`} onClick={() => update(undoOne(packRef.current, line.id))}>
                  −1
                </Button>
              ) : null}
            </li>
          );
        })}
      </ul>
      <Button size="lg" className="min-h-12 w-full" disabled={!progress.complete} loading={saving} onClick={() => void save()}>
        Paket kontrolünü kaydet ({progress.packageCount} koli)
      </Button>
      {!progress.complete ? <p className="text-caption text-fg-muted">Eksik ürün varsa önce satırda “Eksik bildir” ile bildirin.</p> : null}
    </section>
  );
}

/** Confirmation: stock leaves once. One key per attempt on this version, so a retry after a lost answer is replayed. */
function ConfirmSection({ loaded, onConfirmed, onError }: { loaded: LoadedShipment; onConfirmed: (next: LoadedShipment) => void; onError: (error: unknown) => void }) {
  const uid = useId();
  const { api } = useSession();
  const { shipment } = loaded;
  const [carrier, setCarrier] = useState(shipment.carrier ?? "");
  const [tracking, setTracking] = useState(shipment.trackingNumber ?? "");
  const [packages, setPackages] = useState(shipment.packageCount !== null ? String(shipment.packageCount) : "");
  const [packagesError, setPackagesError] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const keyRef = useRef<{ body: string; key: string } | null>(null);
  const shipsNothing = shipment.lines.every((l) => toNumber(l.quantity) <= 0);

  const ask = () => {
    const text = packages.trim();
    if (text && !/^\d{1,4}$/.test(text)) {
      setPackagesError("Koli sayısını tam sayı olarak girin (boş bırakılabilir).");
      return;
    }
    setPackagesError(null);
    setAsking(true);
  };

  const confirm = async () => {
    setAsking(false);
    const body = { carrier: carrier.trim() || null, trackingNumber: tracking.trim() || null, packageCount: packages.trim() ? Number(packages.trim()) : null };
    const serialized = JSON.stringify(body);
    if (keyRef.current?.body !== serialized) keyRef.current = { body: serialized, key: crypto.randomUUID() };
    const key = keyRef.current.key;
    setBusy(true);
    try {
      const { result, etag } = await withBusyRetry(() => confirmShipment(api, shipment.id, body, { idempotencyKey: key, etag: loaded.etag }), {
        isBusy: isFulfilmentBusy,
        onRetry: () => setRetrying(true),
      });
      keyRef.current = null;
      onConfirmed({ shipment: result.shipment, etag });
    } catch (error) {
      onError(error);
    } finally {
      setBusy(false);
      setRetrying(false);
    }
  };

  return (
    <section aria-labelledby={`${uid}-confirm-shipment`} className="flex flex-col gap-3 rounded-lg border-2 border-brand bg-card p-4 shadow-e1">
      <h2 id={`${uid}-confirm-shipment`} className="text-h3 text-fg">
        Sevkiyatı onayla
      </h2>
      <p className="text-body text-fg-secondary">Onayla ayrılan stok depodan düşer ve sipariş sevk edildi olarak işaretlenir. Bu işlem geri alınamaz.</p>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Input label="Kargo firması" hint="İsteğe bağlı" size="lg" value={carrier} maxLength={100} onChange={(e) => setCarrier(e.target.value)} />
        <Input label="Takip numarası" hint="İsteğe bağlı" size="lg" value={tracking} maxLength={100} onChange={(e) => setTracking(e.target.value)} autoComplete="off" spellCheck={false} />
        <Input label="Koli sayısı" hint="İsteğe bağlı" size="lg" inputMode="numeric" value={packages} error={packagesError ?? undefined} onChange={(e) => setPackages(e.target.value)} />
      </div>
      <Button size="lg" className="min-h-12 w-full" icon={<Truck className="size-5" />} loading={busy} disabled={shipsNothing} onClick={ask}>
        Sevkiyatı onayla
      </Button>
      {retrying ? (
        <p role="status" className="text-caption text-fg-muted">
          Kayıt meşgul; bir kez daha deneniyor…
        </p>
      ) : null}
      {shipsNothing ? <p className="text-caption text-fg-muted">Bu sevkiyatta gönderilecek ürün kalmadı.</p> : null}
      <Dialog
        open={asking}
        onClose={() => setAsking(false)}
        role="alertdialog"
        dismissOnBackdrop={false}
        title={`${shipment.number} sevk edilsin mi?`}
        description={`${shipment.lines.filter((l) => toNumber(l.quantity) > 0).length} satır depodan düşülecek. Bu işlem geri alınamaz.`}
        footer={
          <>
            <Button variant="secondary" size="lg" onClick={() => setAsking(false)}>
              Vazgeç
            </Button>
            <Button size="lg" onClick={() => void confirm()}>
              Sevk et
            </Button>
          </>
        }
      />
    </section>
  );
}

/** Stock movements of this shipment (`source_id` = shipment id). Needs inventory.movements.read. */
function ShipmentMovements({ shipmentId, lines }: { shipmentId: string; lines: ShipmentLine[] }) {
  const uid = useId();
  const { api } = useSession();
  const [data, setData] = useState<{ status: "loading" } | { status: "error"; message: string } | { status: "ready"; items: Movement[] }>({ status: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    listMovements(api, movementQuery({ sourceId: shipmentId }), controller.signal)
      .then((page) => !controller.signal.aborted && setData({ status: "ready", items: page.items }))
      .catch((error: unknown) => !controller.signal.aborted && setData({ status: "error", message: fulfilmentErrorMessage(error) }));
    return () => controller.abort();
  }, [api, shipmentId]);
  const skuOf = (variantId: string) => lines.find((l) => l.variantId === variantId)?.sku ?? variantId;
  return (
    <section aria-labelledby={`${uid}-shipment-movements`} className="flex flex-col gap-2">
      <h2 id={`${uid}-shipment-movements`} className="text-h3 text-fg">
        Stok hareketleri
      </h2>
      {data.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Hareketler yükleniyor…
        </p>
      ) : data.status === "error" ? (
        <p role="alert" className="text-body text-danger-fg">
          {data.message}
        </p>
      ) : data.items.length === 0 ? (
        <p className="text-body text-fg-muted">Bu sevkiyata ait hareket yok.</p>
      ) : (
        <ul className="flex flex-col gap-2">
          {data.items.map((m) => (
            <li key={m.id} className="flex flex-wrap items-center gap-3 rounded-md border border-line bg-card p-3 text-body">
              <Badge tone="teal">{movementTypeLabel(m.type)}</Badge>
              <span className="flex-1 font-mono text-fg">{skuOf(m.variantId)}</span>
              <span className="text-fg">{formatSignedQuantity(m.quantity)}</span>
              <span className="text-caption text-fg-muted">{formatDateTime(m.occurredAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

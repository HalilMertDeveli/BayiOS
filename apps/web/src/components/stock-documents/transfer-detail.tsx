"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useId, type FormEvent } from "react";
import Link from "next/link";
import { ArrowLeft, CircleAlert, Lock, PackageCheck, ScanBarcode, Send, XCircle } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { listMovements, movementQuery, type Movement } from "@/lib/inventory/api";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { readShortages, type Shortage } from "@/lib/inventory/errors";
import { formatDateTime, movementTypeLabel, movementTypeTone } from "@/lib/inventory/labels";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import { formatQuantity, formatSignedQuantity, parseQuantity, toNumber } from "@/lib/inventory/quantity";
import {
  TRANSFERS_HREF,
  cancelTransfer,
  closeTransfer,
  dispatchTransfer,
  getTransfer,
  receiveTransfer,
  type LoadedTransfer,
  type TransferLine,
  type TransferResolution,
} from "@/lib/stock-documents/api";
import { BUSY_FINAL_MESSAGE, documentErrorMessage, isDocumentBusy, isStaleDocument } from "@/lib/stock-documents/errors";
import { TRANSFER_OPEN_STATUSES, transferResolutionLabels } from "@/lib/stock-documents/labels";
import { transferPermissions, type TransferPermissions } from "@/lib/stock-documents/permissions";
import { buildReceive, initialReceiveRows, transferTotals, type ReceiveRow } from "@/lib/stock-documents/transfer-form";
import { useSession } from "@/lib/session/session-context";
import { useLocations, useWarehouseNames, useWarehouses } from "@/components/inventory/use-inventory-data";
import { DiscrepancyBadge, OverdueBadge, TransferStatusBadge } from "./badges";
import { ConfirmDialog, ReasonDialog } from "./dialogs";
import { MovementPlace } from "./movement-place";
import { ShortageList } from "./shortage-list";

type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; loaded: LoadedTransfer };

function BackLink() {
  return (
    <Link href={TRANSFERS_HREF} className="inline-flex min-h-11 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Transferler
    </Link>
  );
}

/** /depo/transferler/[id]: dispatch, (partial) receipt, closing the discrepancy, cancel; the fark raporu. */
export function TransferDetailPage({ transferId }: { transferId: string }) {
  const { api, access } = useSession();
  const permissions = transferPermissions(access);
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!permissions.read) return;
    const controller = new AbortController();
    getTransfer(api, transferId, controller.signal)
      .then((loaded) => !controller.signal.aborted && setLoad({ status: "ready", loaded }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: documentErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, transferId, attempt, permissions.read]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  if (!permissions.read) {
    return (
      <>
        <BackLink />
        <PageHeader title="Transfer" />
        <section aria-label="Transfer" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Transferi görme yetkiniz yok" />
        </section>
      </>
    );
  }
  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Transfer" />
        <p role="status" className="text-body text-fg-muted">
          Transfer yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Transfer" />
        <div role="alert" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Transfer bulunamadı" : "Transfer yüklenemedi"}
            description={load.notFound ? "Transfer silinmiş olabilir veya bağlantı hatalı." : load.message}
            action={load.notFound ? undefined : <Button size="lg" onClick={() => (setLoad({ status: "loading" }), reload())}>Tekrar dene</Button>}
          />
        </div>
      </>
    );
  }
  return <TransferView loaded={load.loaded} permissions={permissions} onLoaded={(loaded) => setLoad({ status: "ready", loaded })} onReload={reload} />;
}

type Notice = { tone: "error" | "warning" | "info" | "success"; title: string; text: string; shortages?: Shortage[] } | null;

function TransferView({
  loaded,
  permissions,
  onLoaded,
  onReload,
}: {
  loaded: LoadedTransfer;
  permissions: TransferPermissions;
  onLoaded: (loaded: LoadedTransfer) => void;
  onReload: () => void;
}) {
  const uid = useId();
  const { api, access } = useSession();
  const { toast } = useToast();
  const { transfer: doc } = loaded;
  const t = doc.transfer;
  const isDraft = t.status === "draft";
  const isOpen = (TRANSFER_OPEN_STATUSES as readonly string[]).includes(t.status);
  const dispatched = !isDraft && t.status !== "cancelled";
  const canMovements = inventoryPermissions(access).movements;
  const [notice, setNotice] = useState<Notice>(null);
  const [asking, setAsking] = useState<"dispatch" | "cancel" | null>(null);
  const [busy, setBusy] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const noticeRef = useRef<HTMLDivElement>(null);
  const { state: sourceLocations } = useLocations(t.sourceWarehouseId, { includeInactive: true });
  const shelfCode = (id: string) => (sourceLocations.status === "ready" ? (sourceLocations.data.find((l) => l.id === id)?.code ?? null) : null);

  useEffect(() => {
    if (notice && notice.tone !== "success") noticeRef.current?.focus();
  }, [notice]);

  const handleError = (error: unknown, title: string) => {
    if (isStaleDocument(error)) {
      setNotice({ tone: "warning", title: "Transfer değişmiş", text: `${documentErrorMessage(error)} Kontrol edip tekrar deneyin.` });
      onReload();
      return;
    }
    setNotice({ tone: "error", title, text: isDocumentBusy(error) ? BUSY_FINAL_MESSAGE : documentErrorMessage(error), shortages: readShortages(error) });
  };

  /** Runs a command with one Idempotency-Key per (command, version, body) and one automatic busy retry. */
  const run = async (name: string, body: unknown, send: (key: string) => Promise<LoadedTransfer>, success: (next: LoadedTransfer) => string, errorTitle: string) => {
    const key = keys.keyFor({ name, version: t.version, body });
    setBusy(true);
    try {
      const next = await withBusyRetry(() => send(key), { isBusy: isDocumentBusy });
      keys.reset();
      setNotice(null);
      toast({ tone: "success", title: success(next) });
      onLoaded(next);
      return true;
    } catch (error) {
      handleError(error, errorTitle);
      return false;
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <BackLink />
      <PageHeader
        title={`Transfer ${t.number}`}
        description={`${t.sourceWarehouseName ?? "Kaynak depo"} → ${t.destinationWarehouseName ?? "Hedef depo"}`}
        actions={
          <span className="flex flex-wrap gap-1">
            <TransferStatusBadge status={t.status} />
            {t.isOverdue ? <OverdueBadge /> : null}
            {t.hasDiscrepancy ? <DiscrepancyBadge /> : null}
          </span>
        }
      />
      {notice ? (
        <FormAlert ref={noticeRef} tone={notice.tone} title={notice.title}>
          <p>{notice.text}</p>
          {notice.shortages ? <ShortageList shortages={notice.shortages} shelfCode={shelfCode} /> : null}
        </FormAlert>
      ) : null}

      <section aria-labelledby={`${uid}-info`} className="flex flex-col gap-2 rounded-xl border border-line bg-card p-4 shadow-e1">
        <h2 id={`${uid}-info`} className="sr-only">
          Transfer bilgileri
        </h2>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-2 text-body sm:grid-cols-2 lg:grid-cols-3">
          <Info label="Kaynak depo" value={t.sourceWarehouseName ?? "—"} />
          <Info label="Hedef depo" value={t.destinationWarehouseName ?? "—"} />
          <Info label="Oluşturma" value={formatDateTime(t.createdAt)} />
          {t.dispatchedAt ? <Info label="Gönderim" value={formatDateTime(t.dispatchedAt)} /> : null}
          {t.lastReceivedAt ? <Info label="Son teslim alma" value={formatDateTime(t.lastReceivedAt)} /> : null}
          {t.completedAt ? <Info label="Tamamlanma" value={formatDateTime(t.completedAt)} /> : null}
          {t.note ? <Info label="Not" value={t.note} /> : null}
          {doc.closeResolution ? <Info label="Kalan miktar" value={transferResolutionLabels[doc.closeResolution] ?? doc.closeResolution} /> : null}
          {doc.closeReason ? <Info label="Kapatma sebebi" value={doc.closeReason} /> : null}
          {doc.cancelReason ? <Info label="İptal sebebi" value={doc.cancelReason} /> : null}
        </dl>
        {isOpen ? (
          <p className="text-caption text-fg-muted">Yoldaki mal hiçbir depoda satılabilir görünmez; teslim alınınca hedef depoya girer.</p>
        ) : null}
        {t.isOverdue ? <p className="text-body text-danger-fg">Bu transfer 7 günden uzun süredir yolda. Teslim alın veya farkı kapatın.</p> : null}
      </section>

      {dispatched ? <DiscrepancyReport lines={doc.lines} /> : <PlanLines lines={doc.lines} />}

      {isDraft && permissions.manage ? (
        <section aria-labelledby={`${uid}-draft`} className="flex flex-col gap-3 rounded-lg border-2 border-brand bg-card p-4 shadow-e1">
          <h2 id={`${uid}-draft`} className="text-h3 text-fg">
            Gönder
          </h2>
          <p className="text-body text-fg-secondary">
            Gönderince tüm miktar kaynak depodan çıkar ve teslim alınana kadar yolda görünür. Yalnızca satılabilir stok gönderilir; siparişlere ayrılmış stok taşınmaz.
          </p>
          <div className="flex flex-wrap gap-2">
            <Button size="lg" icon={<Send className="size-5" />} loading={busy && asking === "dispatch"} onClick={() => setAsking("dispatch")}>
              Gönder
            </Button>
            <Button size="lg" variant="danger" icon={<XCircle className="size-5" />} onClick={() => setAsking("cancel")}>
              Taslağı iptal et
            </Button>
          </div>
        </section>
      ) : null}

      {isOpen && permissions.receive ? (
        <ReceiveSection key={`receive-${t.version}`} loaded={loaded} onDone={(next, text) => (setNotice(null), toast({ tone: "success", title: text }), onLoaded(next))} onError={(e) => handleError(e, "Teslim alınamadı")} />
      ) : null}

      {isOpen && permissions.manage ? (
        <CloseSection
          key={`close-${t.version}`}
          inTransit={toNumber(t.inTransitQuantity)}
          busy={busy}
          onClose={(resolution, reason) =>
            run(
              "close",
              { resolution, reason },
              (key) => closeTransfer(api, t.id, { resolution, reason }, { idempotencyKey: key, etag: loaded.etag }),
              (next) => `${next.transfer.transfer.number} farkla kapatıldı`,
              "Transfer kapatılamadı",
            )
          }
        />
      ) : null}

      {dispatched && canMovements ? <TransferMovements transferId={t.id} lines={doc.lines} version={String(t.version)} /> : null}

      <ConfirmDialog
        open={asking === "dispatch"}
        busy={busy}
        title={`${t.number} gönderilsin mi?`}
        description={`${doc.lines.length} satır, toplam ${formatQuantity(t.quantity)} kaynak depodan çıkacak ve yolda görünecek.`}
        confirmLabel="Gönder"
        onClose={() => setAsking(null)}
        onConfirm={async () => {
          await run(
            "dispatch",
            null,
            (key) => dispatchTransfer(api, t.id, { idempotencyKey: key, etag: loaded.etag }),
            (next) => `${next.transfer.transfer.number} gönderildi — yolda`,
            "Transfer gönderilemedi",
          );
          setAsking(null);
        }}
      />
      <ReasonDialog
        key={asking === "cancel" ? `cancel-${t.version}` : "cancel-closed"}
        open={asking === "cancel"}
        title={`${t.number} iptal edilsin mi?`}
        description="Taslak iptal edilir; stok hareketi olmaz."
        label="İptal sebebi"
        confirmLabel="Taslağı iptal et"
        onClose={() => setAsking(null)}
        onSubmit={async (reason) => {
          const key = keys.keyFor({ name: "cancel", version: t.version, reason });
          try {
            const next = await withBusyRetry(() => cancelTransfer(api, t.id, reason, { idempotencyKey: key, etag: loaded.etag }), { isBusy: isDocumentBusy });
            keys.reset();
            setAsking(null);
            setNotice(null);
            toast({ tone: "success", title: `${next.transfer.transfer.number} iptal edildi` });
            onLoaded(next);
            return null;
          } catch (error) {
            if (isStaleDocument(error)) {
              setAsking(null);
              handleError(error, "Transfer iptal edilemedi");
              return null;
            }
            return documentErrorMessage(error);
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

const shelf = (code: string | null | undefined) => <span className="font-mono text-caption">{code ?? "—"}</span>;

function PlanLines({ lines }: { lines: TransferLine[] }) {
  const uid = useId();
  const columns: TableColumn<TransferLine>[] = [
    { key: "sku", header: "SKU", cell: (l) => <span className="font-mono">{l.sku}</span> },
    { key: "from", header: "Kaynak raf", cell: (l) => shelf(l.sourceLocationCode) },
    { key: "to", header: "Planlanan hedef raf", cell: (l) => shelf(l.destinationLocationCode) },
    { key: "qty", header: "Miktar", numeric: true, cell: (l) => formatQuantity(l.quantity) },
  ];
  return (
    <section aria-labelledby={`${uid}-plan`} className="flex flex-col gap-2">
      <h2 id={`${uid}-plan`} className="text-h3 text-fg">
        Satırlar
      </h2>
      <Table caption="Transfer satırları" columns={columns} rows={lines} getRowId={(l) => l.id} density="compact" />
    </section>
  );
}

/** Fark raporu: per line sent = received + in transit + returned + lost. */
function DiscrepancyReport({ lines }: { lines: TransferLine[] }) {
  const uid = useId();
  const totals = transferTotals(lines);
  const columns: TableColumn<TransferLine>[] = [
    { key: "sku", header: "SKU", cell: (l) => <span className="font-mono">{l.sku}</span> },
    { key: "from", header: "Kaynak raf", cell: (l) => shelf(l.sourceLocationCode) },
    { key: "sent", header: "Gönderilen", numeric: true, cell: (l) => formatQuantity(l.quantity) },
    { key: "received", header: "Teslim alınan", numeric: true, cell: (l) => formatQuantity(l.receivedQuantity) },
    { key: "transit", header: "Yolda", numeric: true, cell: (l) => formatQuantity(l.inTransitQuantity) },
    { key: "returned", header: "İade", numeric: true, cell: (l) => formatQuantity(l.returnedQuantity) },
    { key: "lost", header: "Kayıp", numeric: true, cell: (l) => formatQuantity(l.lostQuantity) },
    {
      key: "diff",
      header: "Fark",
      numeric: true,
      cell: (l) => (toNumber(l.discrepancyQuantity) > 0 ? <Badge tone="warning">{formatQuantity(l.discrepancyQuantity)}</Badge> : <span className="text-fg-muted">0</span>),
    },
  ];
  return (
    <section aria-labelledby={`${uid}-report`} className="flex flex-col gap-2">
      <h2 id={`${uid}-report`} className="text-h3 text-fg">
        Fark raporu
      </h2>
      <dl className="grid grid-cols-2 gap-2 sm:grid-cols-5" data-testid="transfer-totals">
        {[
          ["Gönderilen", totals.sent],
          ["Teslim alınan", totals.received],
          ["Yolda", totals.inTransit],
          ["İade", totals.returned],
          ["Kayıp", totals.lost],
        ].map(([label, value]) => (
          <div key={label} className="rounded-xl border border-line bg-card p-3 shadow-e1">
            <dt className="text-caption text-fg-muted">{label}</dt>
            <dd className="text-h3 tabular-nums text-fg">{formatQuantity(value)}</dd>
          </div>
        ))}
      </dl>
      <p className="text-caption text-fg-muted">Fark = gönderilen − teslim alınan (yolda kalan, iade edilen veya kayıp).</p>
      <Table caption="Transfer fark raporu" columns={columns} rows={lines} getRowId={(l) => l.id} density="compact" />
    </section>
  );
}

/** Receipt at the destination: per line (pre-filled with what is in transit) or by scanning product + shelf. */
function ReceiveSection({ loaded, onDone, onError }: { loaded: LoadedTransfer; onDone: (next: LoadedTransfer, text: string) => void; onError: (error: unknown) => void }) {
  const uid = useId();
  const { api } = useSession();
  const t = loaded.transfer.transfer;
  const open = useMemo(() => loaded.transfer.lines.filter((l) => toNumber(l.inTransitQuantity) > 0), [loaded.transfer.lines]);
  const { state: warehouses } = useWarehouses();
  const destination = useWarehouseNames(warehouses).get(t.destinationWarehouseId);
  const usesLocations = destination?.usesLocations ?? open.some((l) => l.destinationLocationId);
  const { state: locations } = useLocations(t.destinationWarehouseId, { enabled: usesLocations });
  const locationOptions = locations.status === "ready" ? locations.data.map((l) => ({ value: l.id, label: l.barcode ? `${l.code} · ${l.barcode}` : l.code })) : [];
  const [rows, setRows] = useState<Record<string, ReceiveRow>>(() => initialReceiveRows(loaded.transfer.lines));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const [scan, setScan] = useState({ code: "", shelf: "", quantity: "1" });
  const [scanError, setScanError] = useState<string | null>(null);
  const codeRef = useRef<HTMLInputElement>(null);

  const send = async (body: Parameters<typeof receiveTransfer>[2], ifMatch: string | null) => {
    const key = keys.keyFor({ version: t.version, body });
    setBusy(true);
    try {
      const next = await withBusyRetry(() => receiveTransfer(api, t.id, body, { idempotencyKey: key, etag: ifMatch }), { isBusy: isDocumentBusy });
      keys.reset();
      const received = body.reduce((s, l) => s + toNumber(l.quantity), 0);
      const left = toNumber(next.transfer.transfer.inTransitQuantity);
      onDone(next, left > 0 ? `${formatQuantity(received)} teslim alındı; yolda ${formatQuantity(left)} kaldı` : `${next.transfer.transfer.number} tamamen teslim alındı`);
      return null;
    } catch (error) {
      return error;
    } finally {
      setBusy(false);
    }
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const built = buildReceive(loaded.transfer.lines, rows, { destinationUsesLocations: usesLocations });
    if (!built.ok) {
      setErrors(built.errors);
      return;
    }
    setErrors({});
    const error = await send(built.lines, loaded.etag);
    if (error) onError(error);
  };

  const submitScan = async (event: FormEvent) => {
    event.preventDefault();
    const code = scan.code.trim();
    const quantity = parseQuantity(scan.quantity);
    if (!code) return setScanError("Ürün barkodu veya SKU okutun.");
    if (!quantity.ok) return setScanError(quantity.message);
    if (usesLocations && !scan.shelf.trim()) return setScanError("Hedef raf kodunu okutun.");
    setScanError(null);
    // Scans go without If-Match (like pick scans): the API checks every receipt under its lock.
    const error = await send([{ lineId: null, variantId: null, code, quantity: quantity.value, locationId: null, locationCode: usesLocations ? scan.shelf.trim() : null }], null);
    if (error) {
      setScanError(documentErrorMessage(error));
      if (isStaleDocument(error) || isDocumentBusy(error)) onError(error);
    } else {
      setScan((s) => ({ ...s, code: "", quantity: "1" }));
    }
    codeRef.current?.focus();
  };

  return (
    <section aria-labelledby={`${uid}-receive`} className="flex flex-col gap-3 rounded-lg border-2 border-brand bg-card p-4 shadow-e1">
      <h2 id={`${uid}-receive`} className="text-h3 text-fg">
        Teslim al
      </h2>
      <p className="text-body text-fg-secondary">
        Gelen miktarı girin; eksik geldiyse yalnızca geleni yazın, kalanı yolda kalır. Yolda olandan fazlası teslim alınamaz — fazla gelen ürünü mal kabul ile kaydedin.
      </p>
      <form aria-label="Satır satır teslim al" noValidate onSubmit={submit} className="flex flex-col gap-3">
        <ul className="flex flex-col gap-3">
          {open.map((line) => {
            const row = rows[line.id] ?? { quantity: "", locationId: "" };
            return (
              <li key={line.id} data-testid={`receive-line-${line.sku}`} className="flex flex-col gap-2 rounded-md border border-line p-3">
                <span className="font-mono text-body font-semibold text-fg">
                  {line.sku} <span className="font-sans font-normal text-fg-muted">· yolda {formatQuantity(line.inTransitQuantity)}</span>
                </span>
                <div className={usesLocations ? "grid grid-cols-1 gap-3 sm:grid-cols-2" : "grid grid-cols-1 gap-3 sm:max-w-xs"}>
                  <Input
                    label={`${line.sku} teslim alınan miktar`}
                    size="lg"
                    inputMode="decimal"
                    autoComplete="off"
                    value={row.quantity}
                    error={errors[line.id]}
                    hint="Boş bırakılan satır bu sefer teslim alınmaz."
                    onChange={(e) => setRows((r) => ({ ...r, [line.id]: { ...row, quantity: e.target.value } }))}
                  />
                  {usesLocations ? (
                    <Select
                      label={`${line.sku} hedef rafı`}
                      size="lg"
                      placeholder="Raf seçin"
                      options={locationOptions}
                      value={row.locationId}
                      onChange={(e) => setRows((r) => ({ ...r, [line.id]: { ...row, locationId: e.target.value } }))}
                    />
                  ) : null}
                </div>
              </li>
            );
          })}
        </ul>
        {errors._form ? (
          <p role="alert" className="text-body text-danger-fg">
            {errors._form}
          </p>
        ) : null}
        <Button type="submit" size="lg" className="w-full sm:w-auto sm:self-start" icon={<PackageCheck className="size-5" />} loading={busy}>
          Teslim al
        </Button>
      </form>

      <form aria-label="Okutarak teslim al" noValidate onSubmit={submitScan} className="flex flex-col gap-3 border-t border-line pt-3">
        <h3 className="text-body font-semibold text-fg">Okutarak teslim al</h3>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-[2fr_1fr_1fr]">
          <Input
            ref={codeRef}
            label="Ürün barkodu veya SKU"
            size="lg"
            prefix={<ScanBarcode className="size-5" />}
            autoComplete="off"
            spellCheck={false}
            value={scan.code}
            onChange={(e) => setScan((s) => ({ ...s, code: e.target.value }))}
          />
          {usesLocations ? (
            <Input label="Hedef raf kodu" size="lg" autoComplete="off" spellCheck={false} value={scan.shelf} onChange={(e) => setScan((s) => ({ ...s, shelf: e.target.value }))} />
          ) : null}
          <Input label="Miktar" size="lg" inputMode="decimal" autoComplete="off" value={scan.quantity} onChange={(e) => setScan((s) => ({ ...s, quantity: e.target.value }))} />
        </div>
        {scanError ? (
          <p role="alert" className="text-body text-danger-fg">
            {scanError}
          </p>
        ) : null}
        <Button type="submit" size="lg" variant="secondary" className="w-full sm:w-auto sm:self-start" loading={busy}>
          Okutulanı teslim al
        </Button>
      </form>
    </section>
  );
}

/** Farkı kapat: what is still in transit becomes lost or goes back to the source; a reason is required. */
function CloseSection({ inTransit, busy, onClose }: { inTransit: number; busy: boolean; onClose: (resolution: TransferResolution, reason: string) => Promise<boolean> }) {
  const uid = useId();
  const [resolution, setResolution] = useState<TransferResolution | "">("");
  const [reason, setReason] = useState("");
  const [errors, setErrors] = useState<{ resolution?: string; reason?: string }>({});
  const [asking, setAsking] = useState(false);

  const ask = () => {
    const next: typeof errors = {};
    if (!resolution) next.resolution = "Kalan miktar için bir seçim yapın.";
    if (!reason.trim()) next.reason = "Kapatma sebebini yazın.";
    setErrors(next);
    if (Object.keys(next).length === 0) setAsking(true);
  };

  return (
    <section aria-labelledby={`${uid}-close`} className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4 shadow-e1">
      <h2 id={`${uid}-close`} className="text-h3 text-fg">
        Farkı kapat
      </h2>
      <p className="text-body text-fg-secondary">
        Yolda kalan {formatQuantity(inTransit)} artık gelmeyecekse transferi kapatın. Kalan miktar kayıp olarak düşülür veya kaynak depoya geri alınır.
      </p>
      <fieldset className="flex flex-col gap-1" aria-describedby={errors.resolution ? `${uid}-resolution-error` : undefined}>
        <legend className="text-body font-medium text-fg">Kalan miktar</legend>
        {(["lost", "return_to_source"] as const).map((value) => (
          <label key={value} className="inline-flex min-h-11 items-center gap-3 text-body text-fg">
            <input type="radio" name={`${uid}-resolution`} className="size-5 accent-(--bos-brand-primary)" checked={resolution === value} onChange={() => setResolution(value)} />
            {value === "lost" ? "Kayıp olarak düş (stoktan çıkar)" : "Kaynak depoya iade et (kaynak raflara geri girer)"}
          </label>
        ))}
        {errors.resolution ? (
          <p id={`${uid}-resolution-error`} className="text-caption text-danger-fg">
            {errors.resolution}
          </p>
        ) : null}
      </fieldset>
      <Textarea label="Kapatma sebebi" value={reason} maxLength={500} error={errors.reason} onChange={(e) => setReason(e.target.value)} />
      <Button size="lg" variant="danger" className="w-full sm:w-auto sm:self-start" loading={busy && asking} onClick={ask}>
        Farkı kapat
      </Button>
      <ConfirmDialog
        open={asking}
        busy={busy}
        title="Transfer kapatılsın mı?"
        description={`Yolda kalan ${formatQuantity(inTransit)} ${resolution === "lost" ? "kayıp olarak düşülecek" : "kaynak depoya iade edilecek"}. Bu işlem geri alınamaz.`}
        confirmLabel="Transferi kapat"
        onClose={() => setAsking(false)}
        onConfirm={async () => {
          if (!resolution) return;
          await onClose(resolution, reason.trim());
          setAsking(false);
        }}
      />
    </section>
  );
}

/** Ledger movements of this transfer (`sourceId` = transfer id): source → Yolda → destination. */
function TransferMovements({ transferId, lines, version }: { transferId: string; lines: TransferLine[]; version: string }) {
  const uid = useId();
  const { api } = useSession();
  const { state: warehouses } = useWarehouses();
  const names = useWarehouseNames(warehouses);
  const [data, setData] = useState<{ key: string; value: { status: "error"; message: string } | { status: "ready"; items: Movement[] } } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    listMovements(api, movementQuery({ sourceId: transferId }, null, 200), controller.signal)
      .then((page) => !controller.signal.aborted && setData({ key: version, value: { status: "ready", items: page.items } }))
      .catch((error: unknown) => !controller.signal.aborted && setData({ key: version, value: { status: "error", message: documentErrorMessage(error) } }));
    return () => controller.abort();
  }, [api, transferId, version]);
  const current = data?.key === version ? data.value : null;
  const skuOf = (variantId: string) => lines.find((l) => l.variantId === variantId)?.sku ?? "—";
  return (
    <section aria-labelledby={`${uid}-movements`} className="flex flex-col gap-2">
      <h2 id={`${uid}-movements`} className="text-h3 text-fg">
        Stok hareketleri
      </h2>
      {current === null ? (
        <p role="status" className="text-body text-fg-muted">
          Hareketler yükleniyor…
        </p>
      ) : current.status === "error" ? (
        <p role="alert" className="text-body text-danger-fg">
          {current.message}
        </p>
      ) : current.items.length === 0 ? (
        <p className="text-body text-fg-muted">Bu transfere ait hareket yok.</p>
      ) : (
        <ul className="flex flex-col gap-2">
          {current.items.map((m) => (
            <li key={m.id} className="flex flex-wrap items-center gap-3 rounded-md border border-line bg-card p-3 text-body">
              <Badge tone={movementTypeTone(m.type)}>{movementTypeLabel(m.type)}</Badge>
              <span className="font-mono text-fg">{skuOf(m.variantId)}</span>
              <span className="flex-1 text-fg-secondary">
                <MovementPlace movement={m} warehouseLabel={(id) => names.get(id)?.name ?? "—"} />
              </span>
              <span className="text-fg">{formatSignedQuantity(m.quantity)}</span>
              <span className="text-caption text-fg-muted">{formatDateTime(m.occurredAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

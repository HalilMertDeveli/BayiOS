"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { ArrowLeft, CheckCheck, CircleAlert, Lock, MapPin, Minus, PackageCheck, Plus, RotateCcw, TriangleAlert } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { cn } from "@/lib/cn";
import {
  TASKS_HREF,
  etagOf,
  getTask,
  reportTaskShortage,
  scanPick,
  shipmentHref,
  taskCommand,
  type LoadedTask,
  type PickScanRequest,
  type TaskDetail,
  type TaskLine,
} from "@/lib/fulfilment/api";
import {
  BUSY_FINAL_MESSAGE,
  fulfilmentErrorMessage,
  isFulfilmentBusy,
  isScanRejection,
  isStaleFulfilment,
  isVersionConflict,
} from "@/lib/fulfilment/errors";
import { SHORTAGE_REASONS, shortageReasonLabels } from "@/lib/fulfilment/labels";
import { fulfilmentPermissions, type FulfilmentPermissions } from "@/lib/fulfilment/permissions";
import {
  applyScan,
  clampStep,
  currentLine,
  isOpenLine,
  keepLocation,
  locationAfterRejection,
  pickStep,
  readScan,
  remaining,
  sortLines,
  taskTotals,
  type ScannedLocation,
} from "@/lib/fulfilment/pick-session";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { formatQuantity, parseQuantity, quantityText, toNumber } from "@/lib/inventory/quantity";
import { useSession } from "@/lib/session/session-context";
import { ProgressBar, TaskStatusBadge } from "./fulfilment-badges";
import { ScanFeedback, ScanField, type Feedback } from "./scan-field";

type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; loaded: LoadedTask };

function BackLink() {
  return (
    <Link href={TASKS_HREF} className="inline-flex min-h-12 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Görevler
    </Link>
  );
}

/** /depo/gorevler/[id]: shelf-ordered pick list with the scan flow (#37) and shortage reporting (#38). */
export function PickScreen({ taskId }: { taskId: string }) {
  const { api, role } = useSession();
  const permissions = fulfilmentPermissions(role);
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!permissions.readOwn) return;
    const controller = new AbortController();
    getTask(api, taskId, controller.signal)
      .then((loaded) => !controller.signal.aborted && setLoad({ status: "ready", loaded }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: fulfilmentErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, taskId, attempt, permissions.readOwn]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  if (!permissions.readOwn) {
    return (
      <>
        <BackLink />
        <PageHeader title="Toplama görevi" />
        <section aria-label="Görev" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Görev ayrıntısını görme yetkiniz yok" description="Görev listesini görebilirsiniz; toplama ekranı depo personeli ve yöneticiler içindir." />
        </section>
      </>
    );
  }
  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Toplama görevi" />
        <p role="status" className="text-body text-fg-muted">
          Görev yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Toplama görevi" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Görev bulunamadı" : "Görev yüklenemedi"}
            description={load.notFound ? "Görev size atanmamış, başkasına atanmış veya bağlantı hatalı." : load.message}
            action={
              load.notFound ? undefined : (
                <Button size="lg" onClick={() => (setLoad({ status: "loading" }), reload())}>
                  Tekrar dene
                </Button>
              )
            }
          />
        </div>
      </>
    );
  }
  return <PickView loaded={load.loaded} permissions={permissions} onLoaded={(loaded) => setLoad({ status: "ready", loaded })} onReload={reload} />;
}

/** A scan that did not get an answer (network / persistent busy): resent with the same key, nothing is lost. */
type FailedScan = { body: PickScanRequest; key: string };

function PickView({
  loaded,
  permissions,
  onLoaded,
  onReload,
}: {
  loaded: LoadedTask;
  permissions: FulfilmentPermissions;
  onLoaded: (loaded: LoadedTask) => void;
  onReload: () => void;
}) {
  const { api, state } = useSession();
  const { toast } = useToast();
  const myId = state.status === "ready" ? state.user.id : null;
  const { task } = loaded.detail;
  const lines = sortLines(loaded.detail.lines);

  const [focusId, setFocusId] = useState<string | null>(null);
  const [location, setLocation] = useState<ScannedLocation | null>(null);
  const [step, setStep] = useState(1);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const [failed, setFailed] = useState<FailedScan | null>(null);
  const [scanning, setScanning] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [pending, setPending] = useState<"claim" | "complete" | null>(null);
  const [notice, setNotice] = useState<{ tone: "error" | "warning" | "info"; title: string; text: string } | null>(null);
  const [shortageLine, setShortageLine] = useState<TaskLine | null>(null);
  const [confirmComplete, setConfirmComplete] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  // Scans can arrive faster than answers: they are queued and read the latest state through these refs.
  const detailRef = useRef<TaskDetail>(loaded.detail);
  const locationRef = useRef<ScannedLocation | null>(null);
  const stepRef = useRef(1);
  const focusRef = useRef<string | null>(null);
  const queue = useRef<Promise<void>>(Promise.resolve());
  useEffect(() => {
    detailRef.current = loaded.detail;
  }, [loaded.detail]);

  const active = task.status === "open" || task.status === "in_progress";
  const mine = task.assignedTo !== null && task.assignedTo === myId;
  const canWork = active && permissions.pick && (mine || permissions.assign);
  const current = currentLine(lines, focusId);
  const totals = taskTotals(lines);
  const nextStep = pickStep(lines, current, location);

  const focusInput = () => requestAnimationFrame(() => inputRef.current?.focus());
  useEffect(() => {
    if (canWork) inputRef.current?.focus();
  }, [canWork]);

  const updateLocation = (next: ScannedLocation | null) => {
    locationRef.current = next;
    setLocation(next);
  };
  const updateStep = (value: number, line: TaskLine | null) => {
    const clamped = clampStep(value, line);
    stepRef.current = clamped;
    setStep(clamped);
  };
  const updateFocus = (id: string | null) => {
    focusRef.current = id;
    setFocusId(id);
  };

  const commit = (detail: TaskDetail, etag: string) => {
    detailRef.current = detail;
    onLoaded({ detail, etag });
  };

  const sendScan = async (body: PickScanRequest, key: string) => {
    setScanning(true);
    setRetrying(false);
    try {
      const response = await withBusyRetry(() => scanPick(api, task.id, body, key), { isBusy: isFulfilmentBusy, onRetry: () => setRetrying(true) });
      setFailed(null);
      const next = applyScan(detailRef.current, response);
      commit(next, etagOf(response.version));
      const changed = next.lines.find((l) => response.lines.some((p) => p.id === l.id));
      const following = currentLine(next.lines, changed && isOpenLine(changed) ? changed.id : null);
      updateFocus(following?.id ?? null);
      updateLocation(keepLocation(locationRef.current, following));
      updateStep(1, following);
      setFeedback({
        tone: "success",
        title: changed ? `✓ ${changed.sku} +${formatQuantity(toNumber(body.quantity))}` : "✓ Okutma kaydedildi",
        text: changed
          ? `${formatQuantity(changed.quantityPicked)} / ${formatQuantity(changed.quantityToPick)} ${changed.unit} toplandı.${isOpenLine(changed) ? "" : " Satır tamam."}${
              response.openLineCount === 0 ? " Tüm satırlar bitti; görevi tamamlayın." : ""
            }`
          : undefined,
      });
    } catch (error) {
      if (isScanRejection(error)) {
        const code = (error as ApiError).code;
        updateLocation(locationAfterRejection(code, locationRef.current));
        setFailed(null);
        setFeedback({ tone: "error", title: rejectionTitle(code), text: fulfilmentErrorMessage(error) });
      } else if (error instanceof ApiError && (error.isNetworkError || isFulfilmentBusy(error) || error.status >= 500)) {
        setFailed({ body, key });
        setFeedback({
          tone: "error",
          title: "Okutma kaydedilmedi",
          text: isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : `${error.userMessage} “Tekrar gönder” ile aynı okutma bir kez sayılır.`,
        });
      } else {
        setFailed(null);
        setFeedback({ tone: "error", title: "Okutma kaydedilmedi", text: fulfilmentErrorMessage(error) });
        if (isStaleFulfilment(error) || (error instanceof ApiError && (error.status === 403 || error.status === 404))) onReload();
      }
    } finally {
      setScanning(false);
      setRetrying(false);
      focusInput();
    }
  };

  const handleScan = (raw: string) => {
    queue.current = queue.current.then(async () => {
      const detail = detailRef.current;
      const reading = readScan(raw, detail.lines, locationRef.current);
      if (!reading) return;
      if (reading.kind === "location") {
        updateLocation(reading.location);
        const known = reading.location.locationId !== null;
        // Jump to the first open line on that shelf, so the card shows what to take there.
        const onShelf = known ? sortLines(detail.lines).find((l) => l.locationId === reading.location.locationId && isOpenLine(l)) : undefined;
        if (onShelf) updateFocus(onShelf.id);
        setFeedback({
          tone: "info",
          title: `Raf okutuldu: ${reading.location.label}`,
          text: known
            ? onShelf
              ? `Şimdi ürünü okutun: ${onShelf.sku}.`
              : "Bu rafta toplanacak ürün kalmadı."
            : "Bu raf görev listesinde yok; ürünü okutursanız sistem kontrol eder.",
        });
        return;
      }
      const body: PickScanRequest = {
        code: reading.code,
        location: locationRef.current?.code ?? null,
        quantity: stepRef.current,
        lineId: null,
      };
      await sendScan(body, crypto.randomUUID());
    });
  };

  const resend = () => {
    if (!failed) return;
    const { body, key } = failed;
    queue.current = queue.current.then(() => sendScan(body, key));
  };

  const claim = async () => {
    setPending("claim");
    setNotice(null);
    try {
      const next = await withBusyRetry(() => taskCommand(api, task.id, "claim", { idempotencyKey: crypto.randomUUID() }), { isBusy: isFulfilmentBusy });
      toast({ tone: "success", title: "Görev size atandı; okutmaya başlayın" });
      commit(next.detail, next.etag);
      focusInput();
    } catch (error) {
      setNotice({ tone: "warning", title: "Görev alınamadı", text: isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error) });
      onReload();
    } finally {
      setPending(null);
    }
  };

  const completeKey = useRef<{ version: number | string; key: string } | null>(null);
  const complete = async () => {
    setConfirmComplete(false);
    setPending("complete");
    setNotice(null);
    // One key per attempt on this version: a retry after a lost answer replays instead of failing on the new state.
    if (completeKey.current?.version !== task.version) completeKey.current = { version: task.version, key: crypto.randomUUID() };
    const key = completeKey.current.key;
    try {
      const next = await withBusyRetry(() => taskCommand(api, task.id, "complete", { idempotencyKey: key, etag: etagOf(task.version) }), {
        isBusy: isFulfilmentBusy,
        onRetry: () => setRetrying(true),
      });
      completeKey.current = null;
      toast({ tone: "success", title: `Sipariş ${task.orderNumber}: toplama tamamlandı` });
      commit(next.detail, next.etag);
    } catch (error) {
      if (isVersionConflict(error) || isStaleFulfilment(error)) {
        setNotice({ tone: "warning", title: "Görev değişmiş", text: "Görev siz bakarken güncellendi; güncel hâli yüklendi. Kontrol edip yeniden tamamlayın." });
        onReload();
      } else {
        setNotice({ tone: "error", title: "Görev tamamlanamadı", text: isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error) });
      }
    } finally {
      setPending(null);
      setRetrying(false);
    }
  };

  const onComplete = () => (totals.short > 0 ? setConfirmComplete(true) : void complete());

  return (
    <>
      <BackLink />
      <PageHeader
        title={`Sipariş ${task.orderNumber}`}
        description={`${lines.length} satır · raf sırasıyla`}
        actions={<TaskStatusBadge status={task.status} />}
      />
      <ProgressBar
        value={totals.picked + totals.short}
        max={totals.toPick}
        label={`${formatQuantity(totals.picked)} / ${formatQuantity(totals.toPick)} toplandı${totals.short > 0 ? ` · ${formatQuantity(totals.short)} eksik` : ""} · ${totals.openLines} satır kaldı`}
      />

      {notice ? (
        <FormAlert tone={notice.tone} title={notice.title}>
          {notice.text}
        </FormAlert>
      ) : null}

      {!active ? (
        <FormAlert tone={task.status === "cancelled" ? "warning" : "success"} title={task.status === "cancelled" ? "Görev iptal edildi" : "Toplama tamamlandı"}>
          {task.status === "cancelled"
            ? "Sipariş iptal edildiği veya kapatıldığı için görev iptal edildi; okutma yapılamaz."
            : loaded.detail.shipmentId
              ? "Toplananlardan taslak sevkiyat oluşturuldu. Paket kontrolü ve sevk onayı için sevkiyata gidin."
              : "Hiç ürün toplanmadığı için sevkiyat oluşmadı."}
          {loaded.detail.shipmentId ? (
            <div className="mt-2">
              <Link href={shipmentHref(loaded.detail.shipmentId)} className={cn(buttonClasses("primary", "lg"), "min-h-12")}>
                <PackageCheck className="size-5" aria-hidden="true" />
                Sevkiyata git
              </Link>
            </div>
          ) : null}
        </FormAlert>
      ) : !canWork ? (
        <FormAlert
          tone="info"
          title={task.assignedTo === null ? "Görev havuzda" : "Görev başka personele atanmış"}
          actions={
            task.assignedTo === null && permissions.claim ? (
              <Button size="lg" className="min-h-12" loading={pending === "claim"} onClick={() => void claim()}>
                Üstlen ve başla
              </Button>
            ) : null
          }
        >
          {task.assignedTo === null
            ? permissions.claim
              ? "Toplamaya başlamak için görevi üstlenin."
              : "Görevi bir personele atayın; liste salt okunur."
            : "Liste salt okunur; yalnızca atanan kişi toplayabilir."}
        </FormAlert>
      ) : null}

      {canWork && current ? (
        <section aria-labelledby="current-line" className="flex flex-col gap-4 rounded-lg border-2 border-brand bg-card p-4 shadow-e2">
          <h2 id="current-line" className="sr-only">
            Şu anki satır
          </h2>
          {current.locationCode ? (
            <p className="flex items-center gap-2 text-display text-fg" data-testid="current-location">
              <MapPin className="size-7 shrink-0 text-brand-fg" aria-hidden="true" />
              <span className="font-mono">{current.locationCode}</span>
            </p>
          ) : null}
          <div className="flex flex-col gap-1">
            <p className="font-mono text-h3 text-fg">{current.sku}</p>
            <p className="text-body-lg text-fg-secondary">{current.name}</p>
            {current.barcode ? <p className="font-mono text-caption text-fg-muted">Barkod {current.barcode}</p> : null}
          </div>
          <p className="text-body-lg text-fg" data-testid="current-remaining">
            Kalan <strong>{formatQuantity(current.quantityRemaining)}</strong> {current.unit} · {formatQuantity(current.quantityPicked)} /{" "}
            {formatQuantity(current.quantityToPick)} toplandı
          </p>
          <p className="rounded-md bg-sunken px-3 py-2 text-body font-semibold text-fg" data-testid="pick-step">
            {nextStep === "location" ? `1. Rafı okutun: ${current.locationCode ?? ""}` : current.locationId ? `2. Ürünü okutun: ${current.sku}` : `Ürünü okutun: ${current.sku}`}
          </p>
          {location ? (
            <p className="flex flex-wrap items-center gap-2 text-body text-fg-secondary">
              <Badge tone={location.locationId ? "success" : "warning"}>Raf: {location.label}</Badge>
              <Button size="lg" variant="ghost" className="min-h-12" onClick={() => (updateLocation(null), focusInput())}>
                Rafı değiştir
              </Button>
            </p>
          ) : null}
        </section>
      ) : null}

      {canWork ? (
        <section aria-label="Okutma" className="flex flex-col gap-3">
          <ScanField
            id="pick-scan"
            label={nextStep === "location" ? "Raf barkodu" : "Ürün barkodu veya SKU"}
            hint="Okuyucu ile okutun veya yazıp Enter'a basın. Aynı ürünü tekrar okutmak 1 adet ekler."
            inputRef={inputRef}
            onScan={handleScan}
          />
          <div className="flex flex-wrap items-end gap-2">
            <div className="flex flex-col gap-1">
              <span id="step-label" className="text-body font-medium text-fg">
                Okutma başına adet
              </span>
              <div role="group" aria-labelledby="step-label" className="flex items-center gap-2">
                <Button size="lg" variant="secondary" className="min-h-12 min-w-12 px-0" aria-label="Adet azalt" disabled={step <= 1} onClick={() => (updateStep(step - 1, current), focusInput())}>
                  <Minus className="size-5" aria-hidden="true" />
                </Button>
                <output aria-live="polite" className="min-w-12 text-center text-h3 text-fg" data-testid="step-value">
                  {step}
                </output>
                <Button size="lg" variant="secondary" className="min-h-12 min-w-12 px-0" aria-label="Adet artır" onClick={() => (updateStep(step + 1, current), focusInput())}>
                  <Plus className="size-5" aria-hidden="true" />
                </Button>
              </div>
            </div>
            {current && permissions.reportShortage ? (
              <Button size="lg" variant="secondary" className="ml-auto min-h-12" icon={<TriangleAlert className="size-5" />} onClick={() => setShortageLine(current)}>
                Eksik bildir
              </Button>
            ) : null}
          </div>
          <ScanFeedback
            feedback={scanning ? { tone: "info", title: retrying ? "Görev meşgul; bir kez daha deneniyor…" : "Gönderiliyor…" } : feedback}
            idleText={current ? "Okutmaya hazır." : "Tüm satırlar bitti."}
            actions={
              failed && !scanning ? (
                <Button size="lg" className="min-h-12" icon={<RotateCcw className="size-5" />} onClick={resend}>
                  Tekrar gönder
                </Button>
              ) : null
            }
          />
          <Button
            size="lg"
            className="min-h-12 w-full"
            icon={<CheckCheck className="size-5" />}
            disabled={totals.openLines > 0}
            loading={pending === "complete"}
            onClick={onComplete}
          >
            Görevi tamamla
          </Button>
          {totals.openLines > 0 ? (
            <p className="text-caption text-fg-muted">Tamamlamak için her satırı toplayın veya eksik bildirin ({totals.openLines} satır açık).</p>
          ) : null}
        </section>
      ) : null}

      <section aria-labelledby="pick-lines" className="flex flex-col gap-2">
        <h2 id="pick-lines" className="text-h3 text-fg">
          Toplama listesi (raf sırası)
        </h2>
        <ol className="flex flex-col gap-2">
          {lines.map((line) => {
            const isCurrent = canWork && current?.id === line.id;
            const open = isOpenLine(line);
            const short = toNumber(line.quantityShort);
            return (
              <li
                key={line.id}
                data-testid={`line-${line.sku}`}
                aria-current={isCurrent ? "step" : undefined}
                className={cn(
                  "flex flex-wrap items-center gap-3 rounded-lg border bg-card p-3",
                  isCurrent ? "border-brand ring-2 ring-brand" : "border-line",
                )}
              >
                <span className="min-w-16 font-mono text-body font-semibold text-fg">{line.locationCode ?? "—"}</span>
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="font-mono text-body text-fg">{line.sku}</span>
                  <span className="truncate text-caption text-fg-muted">{line.name}</span>
                </span>
                <span className="text-body text-fg">
                  {formatQuantity(line.quantityPicked)} / {formatQuantity(line.quantityToPick)} {line.unit}
                </span>
                {short > 0 ? (
                  <Badge tone="danger">
                    {formatQuantity(short)} eksik{line.shortageReason ? ` · ${shortageReasonLabels[line.shortageReason] ?? line.shortageReason}` : ""}
                  </Badge>
                ) : !open ? (
                  <Badge tone="success">Tamam</Badge>
                ) : null}
                {canWork && open && !isCurrent ? (
                  <Button size="lg" variant="ghost" className="min-h-12" onClick={() => (updateFocus(line.id), updateLocation(keepLocation(locationRef.current, line)), focusInput())}>
                    Bu satıra geç
                  </Button>
                ) : null}
              </li>
            );
          })}
        </ol>
      </section>

      <ShortageDialog
        key={shortageLine?.id ?? "closed"}
        line={shortageLine}
        onClose={() => {
          setShortageLine(null);
          focusInput();
        }}
        onSubmit={async (quantity, reason) => {
          if (!shortageLine) return null;
          const key = crypto.randomUUID();
          try {
            const next = await withBusyRetry(() => reportTaskShortage(api, task.id, shortageLine.id, { quantity, reason }, key), { isBusy: isFulfilmentBusy });
            commit(next.detail, next.etag);
            const following = currentLine(next.detail.lines, null);
            updateFocus(following?.id ?? null);
            updateLocation(keepLocation(locationRef.current, following));
            setFeedback({ tone: "info", title: `${shortageLine.sku}: ${formatQuantity(quantity ?? remaining(shortageLine))} eksik bildirildi`, text: "Sipariş işaretlendi; yönetici bilgilendirildi." });
            setShortageLine(null);
            focusInput();
            return null;
          } catch (error) {
            if (isStaleFulfilment(error)) onReload();
            return isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error);
          }
        }}
      />

      <Dialog
        open={confirmComplete}
        onClose={() => setConfirmComplete(false)}
        role="alertdialog"
        dismissOnBackdrop={false}
        title="Eksikle tamamlansın mı?"
        description={`${totals.shortLines} satırda toplam ${formatQuantity(totals.short)} eksik var. Toplananlardan taslak sevkiyat oluşur; eksikler için karar yöneticinizde.`}
        footer={
          <>
            <Button variant="secondary" size="lg" onClick={() => setConfirmComplete(false)}>
              Vazgeç
            </Button>
            <Button size="lg" onClick={() => void complete()}>
              Eksikle tamamla
            </Button>
          </>
        }
      />
    </>
  );
}

function rejectionTitle(code: string | null): string {
  switch (code) {
    case "picking.wrong_item":
      return "✗ Yanlış ürün";
    case "picking.wrong_location":
      return "✗ Yanlış raf";
    case "picking.unknown_location":
      return "✗ Raf bulunamadı";
    case "picking.location_required":
      return "✗ Önce rafı okutun";
    case "picking.over_pick":
      return "✗ Fazla toplama";
    default:
      return "✗ Barkod tanınmadı";
  }
}

/** Shortage for one line: reason (required) and quantity (defaults to what is still open). */
export function ShortageDialog({
  line,
  onClose,
  onSubmit,
}: {
  line: { id: string; sku: string; unit: string; quantityRemaining: number | string } | null;
  onClose: () => void;
  /** Resolves with an error message to show, or null on success. */
  onSubmit: (quantity: number | null, reason: string) => Promise<string | null>;
}) {
  const open = remaining(line ?? { quantityRemaining: 0 });
  const [reason, setReason] = useState("");
  const [quantity, setQuantity] = useState(open > 0 ? quantityText(open) : "");
  const [error, setError] = useState<string | null>(null);
  const [quantityError, setQuantityError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!reason) {
      setError("Bir eksik sebebi seçin.");
      return;
    }
    const parsed = parseQuantity(quantity);
    if (!parsed.ok) {
      setQuantityError(parsed.message);
      return;
    }
    if (parsed.value > open) {
      setQuantityError(`En fazla ${formatQuantity(open)} girilebilir (satırda açık kalan).`);
      return;
    }
    setError(null);
    setQuantityError(null);
    setBusy(true);
    const message = await onSubmit(parsed.value, reason);
    setBusy(false);
    if (message) setError(message);
  };

  return (
    <Dialog
      open={line !== null}
      onClose={() => !busy && onClose()}
      title={line ? `${line.sku} için eksik bildir` : "Eksik bildir"}
      description="Ürün rafta yoksa, hasarlıysa veya yetmiyorsa bildirin. Sipariş işaretlenir ve yöneticiye bildirim gider."
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" size="lg" loading={busy} onClick={() => void submit()}>
            Eksik bildir
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Select
          label="Sebep"
          size="lg"
          placeholder="Sebep seçin"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          options={SHORTAGE_REASONS.map((r) => ({ value: r, label: shortageReasonLabels[r] }))}
        />
        <Input
          label={`Eksik miktar${line ? ` (${line.unit})` : ""}`}
          hint={`Açık kalan: ${formatQuantity(open)}`}
          size="lg"
          inputMode="decimal"
          value={quantity}
          error={quantityError ?? undefined}
          onChange={(e) => setQuantity(e.target.value)}
        />
        {error ? (
          <p role="alert" className="text-body text-danger-fg">
            {error}
          </p>
        ) : null}
      </div>
    </Dialog>
  );
}

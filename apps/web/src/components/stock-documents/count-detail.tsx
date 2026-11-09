"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useId, type FormEvent } from "react";
import Link from "next/link";
import { ArrowLeft, CheckCheck, CircleAlert, EyeOff, Lock, RotateCcw, ScanBarcode, Send, XCircle } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { readShortages, type Shortage } from "@/lib/inventory/errors";
import { formatDateTime } from "@/lib/inventory/labels";
import { formatQuantity, formatSignedQuantity, toNumber } from "@/lib/inventory/quantity";
import {
  COUNTS_HREF,
  applyCount,
  cancelCount,
  enterCounts,
  getCount,
  recountLines,
  submitCount,
  type CountLine,
  type LoadedCount,
} from "@/lib/stock-documents/api";
import {
  countReportTotals,
  filterCountLines,
  lineEntry,
  parseCountQuantity,
  scanEntry,
  type CountLineFilter,
  type EntryMode,
} from "@/lib/stock-documents/count-form";
import { BUSY_FINAL_MESSAGE, documentErrorMessage, isDocumentBusy, isStaleDocument, movedLineIds } from "@/lib/stock-documents/errors";
import { countScopeLabels } from "@/lib/stock-documents/labels";
import { countPermissions, type CountPermissions } from "@/lib/stock-documents/permissions";
import { useSession } from "@/lib/session/session-context";
import { useWarehouseNames, useWarehouses } from "@/components/inventory/use-inventory-data";
import { CountLineStatusBadge, CountStatusBadge } from "./badges";
import { ConfirmDialog, ReasonDialog } from "./dialogs";
import { ShortageList } from "./shortage-list";

type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; loaded: LoadedCount };

/** Lines rendered at once (a count can have up to 5000); filter or show more. */
const PAGE = 100;

function BackLink() {
  return (
    <Link href={COUNTS_HREF} className="inline-flex min-h-11 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Sayımlar
    </Link>
  );
}

/** /depo/sayimlar/[id]: entry (by line or by scanning), submit, recount, apply and the difference report. */
export function CountDetailPage({ countId }: { countId: string }) {
  const { api, access } = useSession();
  const permissions = countPermissions(access);
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!permissions.read) return;
    const controller = new AbortController();
    getCount(api, countId, controller.signal)
      .then((loaded) => !controller.signal.aborted && setLoad({ status: "ready", loaded }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: documentErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, countId, attempt, permissions.read]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  if (!permissions.read) {
    return (
      <>
        <BackLink />
        <PageHeader title="Sayım" />
        <section aria-label="Sayım" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Sayımı görme yetkiniz yok" />
        </section>
      </>
    );
  }
  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Sayım" />
        <p role="status" className="text-body text-fg-muted">
          Sayım yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Sayım" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Sayım bulunamadı" : "Sayım yüklenemedi"}
            description={load.notFound ? "Sayım silinmiş olabilir veya bağlantı hatalı." : load.message}
            action={load.notFound ? undefined : <Button size="lg" onClick={() => (setLoad({ status: "loading" }), reload())}>Tekrar dene</Button>}
          />
        </div>
      </>
    );
  }
  return <CountView loaded={load.loaded} permissions={permissions} onLoaded={(loaded) => setLoad({ status: "ready", loaded })} onReload={reload} />;
}

type Notice = { tone: "error" | "warning" | "info" | "success"; title: string; text: string; shortages?: Shortage[] } | null;

function CountView({
  loaded,
  permissions,
  onLoaded,
  onReload,
}: {
  loaded: LoadedCount;
  permissions: CountPermissions;
  onLoaded: (loaded: LoadedCount) => void;
  onReload: () => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const doc = loaded.count;
  const c = doc.count;
  const isOpen = c.status === "open";
  const isSubmitted = c.status === "submitted";
  const isActive = isOpen || isSubmitted;
  const canEnter = isOpen && permissions.perform;
  const hidden = c.quantitiesHidden;
  const { state: warehouses } = useWarehouses();
  const warehouse = useWarehouseNames(warehouses).get(c.warehouseId);
  const usesLocations = warehouse?.usesLocations ?? doc.lines.some((l) => l.locationId);

  const [notice, setNotice] = useState<Notice>(null);
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [editErrors, setEditErrors] = useState<Record<string, string>>({});
  const [selected, setSelected] = useState<string[]>([]);
  const [text, setText] = useState("");
  const [filter, setFilter] = useState<CountLineFilter>("all");
  const [shown, setShown] = useState(PAGE);
  const [busy, setBusy] = useState<string | null>(null);
  const [asking, setAsking] = useState<"submit" | "apply" | "moved" | "cancel" | null>(null);
  const [moved, setMoved] = useState<string[]>([]);
  const [keys] = useState(() => new IdempotencyKeys());
  const noticeRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (notice && notice.tone !== "success") noticeRef.current?.focus();
  }, [notice]);

  const lines = useMemo(() => filterCountLines(doc.lines, text, filter), [doc.lines, text, filter]);
  const totals = useMemo(() => countReportTotals(doc.lines), [doc.lines]);
  const pendingEdits = Object.keys(edits).length;

  const handleError = (error: unknown, title: string) => {
    if (isStaleDocument(error)) {
      setNotice({ tone: "warning", title: "Sayım değişmiş", text: `${documentErrorMessage(error)} Kontrol edip tekrar deneyin.` });
      onReload();
      return;
    }
    const shortages = error instanceof ApiError && error.code === "count.below_reserved" ? readShortages(error) : undefined;
    setNotice({ tone: "error", title, text: isDocumentBusy(error) ? BUSY_FINAL_MESSAGE : documentErrorMessage(error), shortages });
  };

  /** One Idempotency-Key per (command, version, body); one automatic busy retry. Returns the error, or null when done. */
  const run = async (name: string, body: unknown, send: (key: string) => Promise<LoadedCount>, success: string): Promise<unknown> => {
    const key = keys.keyFor({ name, version: c.version, body });
    setBusy(name);
    try {
      const next = await withBusyRetry(() => send(key), { isBusy: isDocumentBusy });
      keys.reset();
      setNotice(null);
      toast({ tone: "success", title: success });
      onLoaded(next);
      return null;
    } catch (error) {
      return error;
    } finally {
      setBusy(null);
    }
  };

  const saveEdits = async () => {
    const errors: Record<string, string> = {};
    const entries = Object.entries(edits).flatMap(([lineId, value]) => {
      const parsed = parseCountQuantity(value, "set");
      if (!parsed.ok) {
        errors[lineId] = parsed.message;
        return [];
      }
      return [lineEntry(lineId, parsed.value)];
    });
    setEditErrors(errors);
    if (Object.keys(errors).length > 0 || entries.length === 0) return;
    const key = keys.keyFor({ name: "entries", body: entries });
    setBusy("entries");
    try {
      const next = await withBusyRetry(() => enterCounts(api, c.id, entries, key), { isBusy: isDocumentBusy });
      keys.reset();
      setEdits({});
      toast({ tone: "success", title: `${entries.length} satır kaydedildi` });
      onLoaded(next);
    } catch (error) {
      handleError(error, "Sayılan miktarlar kaydedilemedi");
    } finally {
      setBusy(null);
    }
  };

  const apply = async (acceptMovedLines: boolean) => {
    const error = await run(
      "apply",
      { acceptMovedLines },
      (key) => applyCount(api, c.id, acceptMovedLines, { idempotencyKey: key, etag: loaded.etag }),
      `${c.number} uygulandı; farklar stoğa işlendi`,
    );
    setAsking(null);
    if (!error) return;
    const ids = movedLineIds(error);
    if (ids.length > 0) {
      setMoved(ids);
      setAsking("moved");
      return;
    }
    handleError(error, "Sayım uygulanamadı");
  };

  const showSystem = !hidden;
  const columns: TableColumn<CountLine>[] = [
    ...(permissions.manage && isActive
      ? [
          {
            key: "select",
            header: <span className="sr-only">Yeniden sayım için seç</span>,
            width: "3rem",
            cell: (l: CountLine) => (
              <input
                type="checkbox"
                className="size-5 accent-(--bos-brand-primary)"
                aria-label={`${l.sku}${l.locationCode ? ` (${l.locationCode})` : ""} yeniden sayım için seç`}
                checked={selected.includes(l.id)}
                onChange={(e) => setSelected((ids) => (e.target.checked ? [...ids, l.id] : ids.filter((x) => x !== l.id)))}
              />
            ),
          },
        ]
      : []),
    { key: "shelf", header: "Raf", cell: (l) => <span className="font-mono text-caption">{l.locationCode ?? "—"}</span> },
    { key: "sku", header: "SKU", cell: (l) => <span className="font-mono">{l.sku}</span> },
    ...(showSystem ? [{ key: "expected", header: "Sistem", numeric: true, cell: (l: CountLine) => formatQuantity(l.expectedQuantity) }] : []),
    {
      key: "counted",
      header: "Sayılan",
      numeric: true,
      width: canEnter ? "9rem" : undefined,
      cell: (l) =>
        canEnter ? (
          <Input
            label={`${l.sku}${l.locationCode ? ` ${l.locationCode}` : ""} sayılan miktar`}
            hideLabel
            inputMode="decimal"
            autoComplete="off"
            className="min-w-24"
            value={edits[l.id] ?? (l.countedQuantity === null || l.countedQuantity === undefined ? "" : String(l.countedQuantity).replace(".", ","))}
            error={editErrors[l.id]}
            onChange={(e) => setEdits((m) => ({ ...m, [l.id]: e.target.value }))}
          />
        ) : (
          formatQuantity(l.countedQuantity)
        ),
    },
    ...(showSystem
      ? [
          {
            key: "difference",
            header: "Fark",
            numeric: true,
            cell: (l: CountLine) => {
              if (l.difference === null || l.difference === undefined) return <span className="text-fg-muted">—</span>;
              const d = toNumber(l.difference);
              return d === 0 ? <span className="text-fg-muted">0</span> : <Badge tone={d > 0 ? "info" : "danger"}>{formatSignedQuantity(d)}</Badge>;
            },
          },
        ]
      : []),
    {
      key: "status",
      header: "Durum",
      cell: (l) => (
        <span className="flex flex-wrap gap-1">
          <CountLineStatusBadge status={l.status} />
          {l.movedSinceSnapshot ? <Badge tone="warning">Sayımdan sonra hareket</Badge> : null}
          {l.appliedDelta !== null && l.appliedDelta !== undefined && toNumber(l.appliedDelta) !== 0 ? (
            <Badge tone="purple">Düzeltme {formatSignedQuantity(l.appliedDelta)}</Badge>
          ) : null}
        </span>
      ),
    },
  ];

  return (
    <>
      <BackLink />
      <PageHeader
        title={`Sayım ${c.number}`}
        description={`${c.warehouseName ?? "Depo"} · ${countScopeLabels[c.scope] ?? c.scope}`}
        actions={
          <span className="flex flex-wrap gap-1">
            <CountStatusBadge status={c.status} />
            {c.isBlind ? (
              <Badge tone="purple" icon={<EyeOff className="size-3.5" />}>
                Kör sayım
              </Badge>
            ) : null}
          </span>
        }
      />
      {notice ? (
        <FormAlert ref={noticeRef} tone={notice.tone} title={notice.title}>
          <p>{notice.text}</p>
          {notice.shortages ? <ShortageList shortages={notice.shortages} requestedLabel="ayrılmış" availableLabel="sayım sonrası rafta" /> : null}
        </FormAlert>
      ) : null}
      {hidden ? (
        <FormAlert tone="info" title="Kör sayım">
          Sistemdeki miktarlar gizli. Rafta gördüğünüzü sayıp girin; farkı yönetici inceler.
        </FormAlert>
      ) : null}

      <section aria-labelledby={`${uid}-info`} className="rounded-lg border border-line bg-card p-4 shadow-e1">
        <h2 id={`${uid}-info`} className="sr-only">
          Sayım bilgileri
        </h2>
        <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-body sm:grid-cols-4">
          <Info label="Sayılan satır" value={`${formatQuantity(c.countedLineCount)} / ${formatQuantity(c.lineCount)}`} />
          <Info label="Anlık görüntü" value={formatDateTime(c.snapshotAt)} />
          {c.submittedAt ? <Info label="Bitirildi" value={formatDateTime(c.submittedAt)} /> : null}
          {c.appliedAt ? <Info label="Uygulandı" value={formatDateTime(c.appliedAt)} /> : null}
          {doc.differenceCount !== null && doc.differenceCount !== undefined ? <Info label="Farklı satır" value={formatQuantity(doc.differenceCount)} /> : null}
          {doc.movedCount !== null && doc.movedCount !== undefined ? <Info label="Sonradan hareket gören" value={formatQuantity(doc.movedCount)} /> : null}
          {c.note ? <Info label="Not" value={c.note} /> : null}
          {doc.cancelReason ? <Info label="İptal sebebi" value={doc.cancelReason} /> : null}
        </dl>
      </section>

      {canEnter ? <ScanEntry key={c.id} countId={c.id} usesLocations={usesLocations} onLoaded={onLoaded} onError={(e) => handleError(e, "Sayım girilemedi")} /> : null}

      <section aria-labelledby={`${uid}-lines`} className="flex flex-col gap-3">
        <h2 id={`${uid}-lines`} className="text-h3 text-fg">
          {showSystem ? "Fark raporu" : "Sayım satırları"}
        </h2>
        {showSystem && doc.lines.some((l) => l.countedQuantity !== null && l.countedQuantity !== undefined) ? (
          <dl className="grid grid-cols-2 gap-2 sm:grid-cols-4" data-testid="count-totals">
            {[
              ["Sistem (sayılan satırlar)", formatQuantity(totals.expected)],
              ["Sayılan", formatQuantity(totals.counted)],
              ["Fazla", formatSignedQuantity(totals.plus)],
              ["Eksik", formatSignedQuantity(totals.minus)],
            ].map(([label, value]) => (
              <div key={label} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                <dt className="text-caption text-fg-muted">{label}</dt>
                <dd className="text-h3 tabular-nums text-fg">{value}</dd>
              </div>
            ))}
          </dl>
        ) : null}
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:max-w-2xl">
          <Input label="Satırlarda ara" hint="SKU veya raf kodu" value={text} onChange={(e) => (setText(e.target.value), setShown(PAGE))} autoComplete="off" />
          <Select
            label="Göster"
            value={filter}
            onChange={(e) => (setFilter(e.target.value as CountLineFilter), setShown(PAGE))}
            options={[
              { value: "all", label: "Tüm satırlar" },
              { value: "pending", label: "Sayılmayanlar" },
              { value: "counted", label: "Sayılanlar" },
              ...(showSystem ? [{ value: "difference", label: "Farkı olanlar" }] : []),
              ...(permissions.manage ? [{ value: "moved", label: "Sayımdan sonra hareket görenler" }] : []),
            ]}
          />
        </div>
        <Table
          caption={showSystem ? "Sayım fark raporu" : "Sayım satırları"}
          columns={columns}
          rows={lines.slice(0, shown)}
          getRowId={(l) => l.id}
          density="compact"
          maxHeight="70vh"
          empty={<EmptyState headingLevel="h3" title="Satır yok" description={doc.lines.length === 0 ? "Kapsamda stok yok. Bulduğunuz ürünleri okutarak ekleyin." : "Bu filtrede satır yok."} />}
        />
        {lines.length > shown ? (
          <Button variant="secondary" size="lg" className="self-center" onClick={() => setShown((n) => n + PAGE)}>
            Daha fazla göster ({formatQuantity(lines.length - shown)} satır daha)
          </Button>
        ) : null}
        {canEnter ? (
          <div className="flex flex-wrap items-center gap-2">
            <Button size="lg" variant="secondary" disabled={pendingEdits === 0} loading={busy === "entries"} onClick={() => void saveEdits()}>
              Girilenleri kaydet ({pendingEdits})
            </Button>
            {pendingEdits > 0 ? (
              <Button size="lg" variant="ghost" onClick={() => (setEdits({}), setEditErrors({}))}>
                Değişiklikleri at
              </Button>
            ) : null}
          </div>
        ) : null}
      </section>

      {isActive && (permissions.perform || permissions.manage) ? (
        <section aria-labelledby={`${uid}-actions`} className="flex flex-col gap-3 rounded-lg border-2 border-brand bg-card p-4 shadow-e1">
          <h2 id={`${uid}-actions`} className="text-h3 text-fg">
            İşlemler
          </h2>
          <div className="flex flex-wrap gap-2">
            {isOpen && permissions.perform ? (
              <Button size="lg" icon={<Send className="size-5" />} loading={busy === "submit"} disabled={pendingEdits > 0} onClick={() => setAsking("submit")}>
                Sayımı bitir
              </Button>
            ) : null}
            {isSubmitted && permissions.manage ? (
              <Button size="lg" icon={<CheckCheck className="size-5" />} loading={busy === "apply"} onClick={() => setAsking("apply")}>
                Onayla ve stoğa işle
              </Button>
            ) : null}
            {permissions.manage ? (
              <Button
                size="lg"
                variant="secondary"
                icon={<RotateCcw className="size-5" />}
                disabled={selected.length === 0}
                loading={busy === "recount"}
                onClick={async () => {
                  const ids = [...selected];
                  const error = await run("recount", ids, (key) => recountLines(api, c.id, ids, { idempotencyKey: key, etag: loaded.etag }), `${ids.length} satır yeniden sayılacak`);
                  if (error) handleError(error, "Yeniden sayım açılamadı");
                  else setSelected([]);
                }}
              >
                Seçilenleri yeniden saydır ({selected.length})
              </Button>
            ) : null}
            {permissions.manage ? (
              <Button size="lg" variant="danger" icon={<XCircle className="size-5" />} onClick={() => setAsking("cancel")}>
                Sayımı iptal et
              </Button>
            ) : null}
          </div>
          {pendingEdits > 0 && isOpen ? <p className="text-caption text-fg-muted">Önce girilen miktarları kaydedin.</p> : null}
          {isSubmitted && !permissions.manage ? <p className="text-body text-fg-secondary">Sayım bitirildi; yönetici onayı bekleniyor.</p> : null}
        </section>
      ) : null}

      <ConfirmDialog
        open={asking === "submit"}
        busy={busy === "submit"}
        title={`${c.number} bitirilsin mi?`}
        description={`${formatQuantity(c.countedLineCount)} / ${formatQuantity(c.lineCount)} satır sayıldı. Sayılmayan satırlar stoğu değiştirmez. Fark raporu yönetici onayına gider.`}
        confirmLabel="Sayımı bitir"
        onClose={() => setAsking(null)}
        onConfirm={async () => {
          const error = await run("submit", null, (key) => submitCount(api, c.id, { idempotencyKey: key, etag: loaded.etag }), `${c.number} bitirildi; onay bekliyor`);
          setAsking(null);
          if (error) handleError(error, "Sayım bitirilemedi");
        }}
      />
      <ConfirmDialog
        open={asking === "apply"}
        busy={busy === "apply"}
        title={`${c.number} stoğa işlensin mi?`}
        description="Her fark için sayım anındaki sistem bakiyesine göre sayım düzeltmesi hareketi yazılır. Bu işlem geri alınamaz."
        confirmLabel="Onayla"
        onClose={() => setAsking(null)}
        onConfirm={() => void apply(false)}
      />
      <ConfirmDialog
        open={asking === "moved"}
        busy={busy === "apply"}
        title="Bazı satırların stoğu sayımdan sonra değişti"
        description="Aşağıdaki satırlarda sayım anlık görüntüsünden sonra stok hareketi oldu. Düzeltme yine sayım anındaki bakiyeye göre hesaplanır. Kontrol ettiyseniz yine de uygulayın."
        confirmLabel="Yine de uygula"
        onClose={() => setAsking(null)}
        onConfirm={() => void apply(true)}
      >
        <ul className="list-disc pl-5 text-body" aria-label="Hareket gören satırlar">
          {moved.map((id) => {
            const line = doc.lines.find((l) => l.id === id);
            return <li key={id}>{line ? `${line.sku}${line.locationCode ? ` · ${line.locationCode}` : ""}` : "Satır"}</li>;
          })}
        </ul>
      </ConfirmDialog>
      <ReasonDialog
        key={asking === "cancel" ? `cancel-${c.version}` : "cancel-closed"}
        open={asking === "cancel"}
        title={`${c.number} iptal edilsin mi?`}
        description="Sayım iptal edilir; stok hareketi olmaz ve depoda yeni sayım başlatılabilir."
        label="İptal sebebi"
        confirmLabel="Sayımı iptal et"
        onClose={() => setAsking(null)}
        onSubmit={async (reason) => {
          const error = await run("cancel", reason, (key) => cancelCount(api, c.id, reason, { idempotencyKey: key, etag: loaded.etag }), `${c.number} iptal edildi`);
          if (!error) {
            setAsking(null);
            return null;
          }
          if (isStaleDocument(error)) {
            setAsking(null);
            handleError(error, "Sayım iptal edilemedi");
            return null;
          }
          return documentErrorMessage(error);
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

type Feedback = { tone: "success" | "error"; text: string } | null;

/**
 * Scan-first entry: shelf code (located warehouses), product code, quantity and set/add. `add` + quantity 1 counts one
 * item per scan; an item found outside the snapshot becomes a new line (within the scope).
 */
function ScanEntry({ countId, usesLocations, onLoaded, onError }: { countId: string; usesLocations: boolean; onLoaded: (next: LoadedCount) => void; onError: (error: unknown) => void }) {
  const uid = useId();
  const { api } = useSession();
  const [shelf, setShelf] = useState("");
  const [code, setCode] = useState("");
  const [quantity, setQuantity] = useState("1");
  const [mode, setMode] = useState<EntryMode>("add");
  const [feedback, setFeedback] = useState<Feedback>(null);
  const [busy, setBusy] = useState(false);
  const codeRef = useRef<HTMLInputElement>(null);
  const [keys] = useState(() => new IdempotencyKeys());

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const product = code.trim();
    if (usesLocations && !shelf.trim()) return setFeedback({ tone: "error", text: "Önce raf kodunu okutun." });
    if (!product) return setFeedback({ tone: "error", text: "Ürün barkodu veya SKU okutun." });
    const parsed = parseCountQuantity(quantity, mode);
    if (!parsed.ok) return setFeedback({ tone: "error", text: parsed.message });
    const entry = scanEntry(product, usesLocations ? shelf : null, parsed.value, mode);
    const key = keys.keyFor(entry);
    setBusy(true);
    try {
      const next = await withBusyRetry(() => enterCounts(api, countId, [entry], key), { isBusy: isDocumentBusy });
      keys.reset();
      const line = [...next.count.lines]
        .filter((l) => l.countedAt)
        .sort((a, b) => (b.countedAt ?? "").localeCompare(a.countedAt ?? ""))[0];
      setFeedback({
        tone: "success",
        text: line ? `✓ ${line.sku}${line.locationCode ? ` · ${line.locationCode}` : ""}: sayılan ${formatQuantity(line.countedQuantity)}` : "✓ Kaydedildi",
      });
      setCode("");
      onLoaded(next);
    } catch (error) {
      if (isStaleDocument(error)) onError(error);
      setFeedback({ tone: "error", text: isDocumentBusy(error) ? BUSY_FINAL_MESSAGE : documentErrorMessage(error) });
    } finally {
      setBusy(false);
      codeRef.current?.focus();
    }
  };

  return (
    <section aria-labelledby={`${uid}-scan`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
      <h2 id={`${uid}-scan`} className="text-h3 text-fg">
        Okutarak say
      </h2>
      <form aria-label="Okutarak say" noValidate onSubmit={submit} className="flex flex-col gap-3">
        <div className={usesLocations ? "grid grid-cols-1 gap-3 sm:grid-cols-[1fr_2fr_1fr]" : "grid grid-cols-1 gap-3 sm:grid-cols-[2fr_1fr]"}>
          {usesLocations ? (
            <Input
              label="Raf kodu"
              size="lg"
              autoComplete="off"
              spellCheck={false}
              value={shelf}
              hint="Raf değişene kadar aynı kalır."
              onChange={(e) => setShelf(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  codeRef.current?.focus();
                }
              }}
            />
          ) : null}
          <Input
            ref={codeRef}
            label="Ürün barkodu veya SKU"
            size="lg"
            prefix={<ScanBarcode className="size-5" />}
            autoComplete="off"
            spellCheck={false}
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
          <Input label="Miktar" size="lg" inputMode="decimal" autoComplete="off" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </div>
        <fieldset className="flex flex-wrap gap-x-6">
          <legend className="sr-only">Giriş türü</legend>
          {(["add", "set"] as const).map((value) => (
            <label key={value} className="inline-flex min-h-11 items-center gap-3 text-body text-fg">
              <input type="radio" name={`${uid}-mode`} className="size-5 accent-(--bos-brand-primary)" checked={mode === value} onChange={() => setMode(value)} />
              {value === "add" ? "Ekle (her okutma miktar kadar ekler)" : "Yaz (satırın miktarını bununla değiştir)"}
            </label>
          ))}
        </fieldset>
        <p role="status" data-testid="count-feedback" data-tone={feedback?.tone ?? "idle"} className={feedback?.tone === "error" ? "text-body text-danger-fg" : "text-body text-fg-secondary"}>
          {feedback?.text ?? "Okutmaya hazır."}
        </p>
        <Button type="submit" size="lg" className="w-full sm:w-auto sm:self-start" loading={busy}>
          Kaydet
        </Button>
      </form>
    </section>
  );
}

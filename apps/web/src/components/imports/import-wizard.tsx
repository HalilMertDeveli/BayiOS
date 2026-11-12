"use client";

import { useEffect, useRef, useState, type ReactNode, useId } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { CircleAlert, FileDown, LoaderCircle, Play, RotateCcw, Undo2 } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import {
  NEW_IMPORT_HREF,
  cancelImport,
  downloadErrorReport,
  num,
  rollbackImport,
  startImport,
  type ImportJob,
} from "@/lib/imports/api";
import { failureMessage, importErrorMessage } from "@/lib/imports/errors";
import { formatCount, formatDateTime, importTypeLabels, matchKeyLabel, modeLabel, statusLabel, statusTone } from "@/lib/imports/labels";
import { canRollbackJob, canRunType, importPermissions, type ImportPermissions } from "@/lib/imports/permissions";
import { canCancel, canRemap, canStart, importableRows, isResume, rollbackState, visibleStep, type WizardStep } from "@/lib/imports/steps";
import { priceListHref } from "@/lib/pricing/api";
import { filenameFromContentDisposition, saveBlob } from "@/lib/reports/download";
import { useSession } from "@/lib/session/session-context";
import { ImportsLocked } from "./import-history";
import { ImportRows, type RowFilterOption } from "./import-rows";
import { ImportStepper } from "./import-stepper";
import { MappingStep } from "./mapping-step";
import { BackToHistory, PRICE_LIST_PARAM } from "./new-import";
import { useImportJob } from "./use-import-job";

/** /ice-aktarma/[id]: the wizard from the mapping on, driven by the job's status (refresh-safe, resumable from history). */
export function ImportWizard({ id }: { id: string }) {
  const { access } = useSession();
  const permissions = importPermissions(access);
  const search = useSearchParams();
  const { load, setJob, reload, pollError } = useImportJob(id);
  const [requested, setRequested] = useState<WizardStep | null>(null);
  const [cancelOpen, setCancelOpen] = useState(false);

  if (!permissions.run) {
    return (
      <>
        <BackToHistory />
        <PageHeader title="İçe aktarma" />
        <ImportsLocked permissions={permissions} />
      </>
    );
  }

  if (load.status === "loading") {
    return (
      <>
        <BackToHistory />
        <PageHeader title="İçe aktarma" />
        <p role="status" className="rounded-lg border border-line bg-card p-6 text-body text-fg-muted">
          Aktarma yükleniyor…
        </p>
      </>
    );
  }

  if (load.status === "error") {
    return (
      <>
        <BackToHistory />
        <PageHeader title="İçe aktarma" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Aktarma bulunamadı" : "Aktarma yüklenemedi"}
            description={load.notFound ? "Bu aktarma silinmiş olabilir veya başka bir işletmeye ait." : load.message}
            action={load.notFound ? undefined : <Button onClick={reload}>Tekrar dene</Button>}
          />
        </div>
      </>
    );
  }

  const job = load.job;
  const step = visibleStep(job, requested);
  const writable = canRunType(permissions, job.type);
  const onJob = (next: ImportJob) => {
    setRequested(null);
    setJob(next);
  };

  return (
    <>
      <BackToHistory />
      <PageHeader
        title="İçe aktarma"
        description={
          <span className="flex flex-wrap items-center gap-2">
            <span className="break-all">{job.fileName}</span>
            <span aria-hidden="true">·</span>
            <span>{importTypeLabels[job.type] ?? job.type}</span>
            <Badge tone={statusTone(job.status)}>{statusLabel(job.status)}</Badge>
          </span>
        }
        actions={
          <Link href={NEW_IMPORT_HREF} className={buttonClasses("secondary")}>
            Yeni içe aktarma
          </Link>
        }
      />
      <ImportStepper current={step} />

      {pollError ? (
        <FormAlert tone="warning" title="Güncel durum alınamadı">
          Bağlantı sorunu nedeniyle durum yenilenemedi; otomatik olarak yeniden deneniyor.
        </FormAlert>
      ) : null}
      {!writable ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          {job.type === "prices" ? "Fiyat yazma yetkiniz olmadığı için" : "Katalog yazma yetkiniz olmadığı için"} bu aktarmada işlem yapamazsınız; yalnızca
          durumunu görebilirsiniz.
        </FormAlert>
      ) : null}

      {step === "mapping" ? (
        writable ? (
          <MappingStep job={job} permissions={permissions} initialPriceListId={search.get(PRICE_LIST_PARAM)} onJob={onJob} onCancel={() => setCancelOpen(true)} />
        ) : (
          <FormAlert tone="info" title="Eşleme bekleniyor">
            Bu dosyanın kolonları henüz eşlenmedi.
          </FormAlert>
        )
      ) : null}
      {step === "preview" ? (
        <PreviewStep job={job} writable={writable} onJob={onJob} onBack={() => setRequested("mapping")} onCancel={() => setCancelOpen(true)} onReload={reload} />
      ) : null}
      {step === "progress" ? <ProgressStep job={job} writable={writable} onCancel={() => setCancelOpen(true)} /> : null}
      {step === "result" ? (
        <ResultStep job={job} permissions={permissions} writable={writable} onJob={onJob} onBack={() => setRequested("mapping")} onReload={reload} />
      ) : null}

      {cancelOpen ? <CancelDialog job={job} onClose={() => setCancelOpen(false)} onJob={onJob} onReload={reload} /> : null}
    </>
  );
}

// ---- Shared pieces ----

function Counts({ items, label }: { items: { label: string; value: number; tone?: "success" | "info" | "neutral" | "danger" | "warning" }[]; label: string }) {
  const toneClass = { success: "text-success-fg", info: "text-info-fg", neutral: "text-fg", danger: "text-danger-fg", warning: "text-warning-fg" } as const;
  return (
    <div role="group" aria-label={label}>
      <dl className="grid grid-cols-2 gap-2 md:grid-cols-4">
        {items.map((i) => (
          <div key={i.label} className="flex flex-col gap-1 rounded-md border border-line bg-card p-3">
            <dt className="text-caption text-fg-muted">{i.label}</dt>
            <dd className={`text-h2 tabular-nums ${toneClass[i.tone ?? "neutral"]}`}>{formatCount(i.value)}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  const uid = useId();
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    headingRef.current?.focus();
  }, [title]);
  return (
    <section aria-labelledby={`${uid}-import-step-title`} className="flex flex-col gap-5 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      <h2 id={`${uid}-import-step-title`} ref={headingRef} tabIndex={-1} className="text-h2 text-fg focus:outline-none">
        {title}
      </h2>
      {children}
    </section>
  );
}

/** CSV of invalid + failed rows (UTF-8 BOM, `;`) — opens directly in Turkish Excel. */
function ErrorReportButton({ job }: { job: ImportJob }) {
  const { api } = useSession();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "success" | "error"; text: string } | null>(null);
  const run = async () => {
    setBusy(true);
    setMessage(null);
    try {
      const { blob, contentDisposition } = await downloadErrorReport(api, job.id);
      const name = filenameFromContentDisposition(contentDisposition) ?? `ice-aktarma-hatalari-${job.id.slice(0, 8)}.csv`;
      saveBlob(blob, name);
      setMessage({ tone: "success", text: `Hata raporu indirildi: ${name}` });
    } catch (error) {
      setMessage({ tone: "error", text: importErrorMessage(error) });
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="flex flex-col gap-1">
      <Button variant="secondary" icon={<FileDown className="size-4" />} loading={busy} onClick={() => void run()}>
        Hata raporunu indir (CSV)
      </Button>
      <p role={message?.tone === "error" ? "alert" : "status"} className={message ? (message.tone === "error" ? "text-caption text-danger-fg" : "text-caption text-success-fg") : "sr-only"}>
        {message?.text ?? ""}
      </p>
    </div>
  );
}

/** Start / resume with one Idempotency-Key per (job, version): a double click or a retry does not start twice. */
function useJobAction(action: (id: string, key: string) => Promise<ImportJob>, onJob: (job: ImportJob) => void, onReload: () => void) {
  const [keys] = useState(() => new IdempotencyKeys());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async (job: ImportJob) => {
    setBusy(true);
    setError(null);
    try {
      const next = await action(job.id, keys.keyFor({ id: job.id, version: job.version }));
      keys.reset();
      onJob(next);
    } catch (e) {
      setError(importErrorMessage(e));
      // The status changed under us (409 invalid_state / 412): show the current state.
      if (e instanceof ApiError && (e.status === 409 || e.status === 412)) onReload();
    } finally {
      setBusy(false);
    }
  };
  return { run, busy, error };
}

// ---- Step 4: preview ----

function PreviewStep({
  job,
  writable,
  onJob,
  onBack,
  onCancel,
  onReload,
}: {
  job: ImportJob;
  writable: boolean;
  onJob: (job: ImportJob) => void;
  onBack: () => void;
  onCancel: () => void;
  onReload: () => void;
}) {
  const { api } = useSession();
  const start = useJobAction((id, key) => startImport(api, id, key), onJob, onReload);

  if (job.status === "validating") {
    return (
      <Panel title="Önizleme hazırlanıyor">
        <p role="status" className="flex items-center gap-2 text-body text-fg-secondary">
          <LoaderCircle className="size-5 animate-spin" aria-hidden="true" />
          {formatCount(num(job.totalRows))} satır kontrol ediliyor: eşleşen kayıtlar, dosya içi tekrarlar ve hatalı değerler. Henüz hiçbir kayıt yazılmadı.
        </p>
        {writable ? (
          <div>
            <Button variant="ghost" onClick={onCancel}>
              Aktarmayı iptal et
            </Button>
          </div>
        ) : null}
      </Panel>
    );
  }

  const p = job.preview;
  const errors = num(p.error);
  const importable = importableRows(job);
  const options: RowFilterOption[] = [
    { id: "all", label: "Tümü", filter: {}, count: num(job.totalRows) },
    { id: "error", label: "Hatalı", filter: { status: "error" }, count: errors },
    { id: "create", label: "Eklenecek", filter: { action: "create" }, count: num(p.create) },
    { id: "update", label: "Güncellenecek", filter: { action: "update" }, count: num(p.update) },
    { id: "skip", label: "Atlanacak", filter: { action: "skip" }, count: num(p.skip) },
  ];

  return (
    <Panel title="Önizleme">
      <p className="text-body text-fg-secondary">
        Deneme çalıştırması tamamlandı; henüz hiçbir kayıt yazılmadı. Eşleşme: {matchKeyLabel(job.matchKey)} · Mod: {modeLabel(job.mode)}.
      </p>
      <Counts
        label="Önizleme özeti"
        items={[
          { label: "Eklenecek", value: num(p.create), tone: "success" },
          { label: "Güncellenecek", value: num(p.update), tone: "info" },
          { label: "Atlanacak", value: num(p.skip) },
          { label: "Hatalı", value: errors, tone: errors > 0 ? "danger" : "neutral" },
        ]}
      />
      {errors > 0 ? (
        <FormAlert tone="warning" title={`${formatCount(errors)} satır hatalı`}>
          Hatalı satırlar aktarılmaz. Hataları dosyada düzeltip yeniden yükleyebilir ya da hatasız satırlarla devam edebilirsiniz.
        </FormAlert>
      ) : null}
      {importable === 0 ? (
        <FormAlert tone="info" title="Aktarılacak satır yok">
          Eklenecek veya güncellenecek satır bulunmadı. Eşlemeyi veya modu değiştirip önizlemeyi yenileyin.
        </FormAlert>
      ) : null}
      <div className="flex flex-wrap gap-2">{errors > 0 ? <ErrorReportButton job={job} /> : null}</div>
      <ImportRows jobId={job.id} version={`${job.version}`} options={options} initial={errors > 0 ? "error" : "all"} caption="Önizleme satırları" />
      {start.error ? (
        <FormAlert tone="error" title="Aktarma başlatılamadı">
          {start.error}
        </FormAlert>
      ) : null}
      {writable ? (
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-between">
          <div className="flex flex-col gap-2 sm:flex-row">
            <Button variant="ghost" onClick={onCancel} disabled={start.busy}>
              Aktarmayı iptal et
            </Button>
            {canRemap(job) ? (
              <Button variant="secondary" onClick={onBack} disabled={start.busy}>
                Eşlemeye dön
              </Button>
            ) : null}
          </div>
          <Button icon={<Play className="size-4" />} loading={start.busy} disabled={!canStart(job)} onClick={() => void start.run(job)}>
            {errors > 0 && importable > 0 ? `${formatCount(importable)} satırı aktar` : "Aktarmayı başlat"}
          </Button>
        </div>
      ) : null}
    </Panel>
  );
}

// ---- Step 5: progress ----

function ProgressBar({ percent, label }: { percent: number; label: string }) {
  const value = Math.max(0, Math.min(100, Math.round(percent)));
  return (
    <div className="flex flex-col gap-1">
      <div
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={value}
        aria-valuetext={`%${value}`}
        className="h-3 w-full overflow-hidden rounded-full bg-sunken"
      >
        <div className="h-full rounded-full bg-brand transition-[width]" style={{ width: `${value}%` }} />
      </div>
      <span className="text-caption text-fg-muted tabular-nums">%{value}</span>
    </div>
  );
}

function ProgressStep({ job, writable, onCancel }: { job: ImportJob; writable: boolean; onCancel: () => void }) {
  const p = job.progress;
  return (
    <Panel title="Aktarılıyor">
      <p className="text-body text-fg-secondary">
        Aktarma arka planda çalışıyor; bu sayfadan ayrılabilirsiniz. Durumu daha sonra İçe aktarma geçmişinden görebilirsiniz.
      </p>
      <ProgressBar percent={num(p.percent)} label="Aktarma ilerlemesi" />
      <p className="text-body text-fg tabular-nums">
        {formatCount(num(p.processedRows))} / {formatCount(num(p.plannedRows))} satır işlendi
      </p>
      <Counts
        label="Aktarma sayıları"
        items={[
          { label: "Eklendi", value: num(p.created), tone: "success" },
          { label: "Güncellendi", value: num(p.updated), tone: "info" },
          { label: "Atlandı", value: num(p.skipped) },
          { label: "Başarısız", value: num(p.failed), tone: num(p.failed) > 0 ? "danger" : "neutral" },
        ]}
      />
      {writable ? (
        <div>
          <Button variant="secondary" onClick={onCancel}>
            Aktarmayı iptal et
          </Button>
        </div>
      ) : null}
    </Panel>
  );
}

// ---- Step 6: result + rollback ----

function ResultStep({
  job,
  permissions,
  writable,
  onJob,
  onBack,
  onReload,
}: {
  job: ImportJob;
  permissions: ImportPermissions;
  writable: boolean;
  onJob: (job: ImportJob) => void;
  onBack: () => void;
  onReload: () => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const start = useJobAction((id, key) => startImport(api, id, key), onJob, onReload);
  const [rollbackOpen, setRollbackOpen] = useState(false);
  // Read once per render pass for the deadline check; the server re-checks the window.
  const [now] = useState(() => Date.now());
  const p = job.progress;
  const failed = num(p.failed) + (job.startedAt === null ? num(job.preview.error) : 0);
  const rollback = rollbackState(job, now);
  const mayRollback = canRollbackJob(permissions, job);
  const r = job.rollback;
  const rollbackHandled = r ? num(r.rolledBack) + num(r.conflicts) + num(r.failed) + num(r.skipped) : 0;
  const message = failureMessage(job.failure);

  const title =
    {
      completed: "Aktarma tamamlandı",
      failed: "Aktarma durdu",
      cancelled: "Aktarma iptal edildi",
      rolling_back: "Geri alınıyor",
      rolled_back: "Aktarma geri alındı",
      partially_rolled_back: "Aktarma kısmen geri alındı",
      rollback_failed: "Geri alma durdu",
    }[job.status] ?? statusLabel(job.status);

  const options: RowFilterOption[] = [
    { id: "all", label: "Tümü", filter: {} },
    { id: "error", label: "Hatalı", filter: { status: "error" } },
  ];
  if (r) {
    options.push({ id: "conflict", label: "Çakışmalar", filter: { rollbackStatus: "conflict" }, count: num(r.conflicts) });
    options.push({ id: "rolled_back", label: "Geri alınanlar", filter: { rollbackStatus: "rolled_back" }, count: num(r.rolledBack) });
  }

  return (
    <Panel title={title}>
      {job.status === "completed" ? (
        <FormAlert tone="success" title="Kayıtlar yazıldı">
          {formatDateTime(job.finishedAt)} tarihinde tamamlandı.{" "}
          {job.type === "products" ? (
            <Link href="/urunler" className="font-medium underline underline-offset-2">
              Ürünlere git
            </Link>
          ) : job.priceListId ? (
            <Link href={priceListHref(job.priceListId)} className="font-medium underline underline-offset-2">
              Fiyat listesine git
            </Link>
          ) : null}
        </FormAlert>
      ) : null}
      {job.status === "cancelled" ? (
        <FormAlert tone="warning" title="İptal edildi">
          {job.startedAt ? "İşlenmiş satırlar kaldı; kalan satırlar işlenmedi." : "Hiçbir kayıt yazılmadı."}
        </FormAlert>
      ) : null}
      {message && ["failed", "rollback_failed", "cancelled"].includes(job.status) ? (
        <FormAlert tone="error" title="Ne oldu?">
          {message}
        </FormAlert>
      ) : null}

      {job.startedAt ? (
        <Counts
          label="Aktarma sonucu"
          items={[
            { label: "Eklendi", value: num(p.created), tone: "success" },
            { label: "Güncellendi", value: num(p.updated), tone: "info" },
            { label: "Atlandı", value: num(p.skipped) },
            { label: "Başarısız", value: num(p.failed), tone: num(p.failed) > 0 ? "danger" : "neutral" },
          ]}
        />
      ) : null}

      {r ? (
        <section aria-labelledby={`${uid}-rollback-summary`} className="flex flex-col gap-3 rounded-md border border-line p-3">
          <h3 id={`${uid}-rollback-summary`} className="text-h3 text-fg">
            Geri alma
          </h3>
          {job.status === "rolling_back" ? (
            <>
              <ProgressBar percent={num(r.planned) === 0 ? 0 : (rollbackHandled * 100) / num(r.planned)} label="Geri alma ilerlemesi" />
              <p className="text-body text-fg tabular-nums">
                {formatCount(rollbackHandled)} / {formatCount(num(r.planned))} satır işlendi
              </p>
            </>
          ) : null}
          <Counts
            label="Geri alma sonucu"
            items={[
              { label: "Geri alındı", value: num(r.rolledBack), tone: "success" },
              { label: "Çakışma (dokunulmadı)", value: num(r.conflicts), tone: num(r.conflicts) > 0 ? "warning" : "neutral" },
              { label: "Geri alınamadı", value: num(r.failed), tone: num(r.failed) > 0 ? "danger" : "neutral" },
              { label: "Yapılacak bir şey yok", value: num(r.skipped) },
            ]}
          />
          {num(r.conflicts) > 0 ? (
            <p className="text-body text-fg-secondary">
              Çakışan satırlar aktarmadan sonra elle değiştirildiği için olduğu gibi bırakıldı. Aşağıdaki “Çakışmalar” filtresiyle görüp gerekirse elle
              düzeltin.
            </p>
          ) : null}
          <p className="text-caption text-fg-muted">
            İstendi: {formatDateTime(r.requestedAt)}
            {r.finishedAt ? ` · Bitti: ${formatDateTime(r.finishedAt)}` : ""}
          </p>
        </section>
      ) : null}

      {rollback === "available" && job.rollbackDeadline ? (
        <p className="text-body text-fg-secondary">
          Bu aktarmayı <strong className="font-semibold text-fg">{formatDateTime(job.rollbackDeadline)}</strong> tarihine kadar bir kez geri alabilirsiniz.
          {!mayRollback ? " Geri alma yetkiniz yok; işletme sahibi veya yönetici geri alabilir." : ""}
        </p>
      ) : null}
      {rollback === "expired" ? <p className="text-caption text-fg-muted">Geri alma süresi (7 gün) doldu.</p> : null}

      {start.error ? (
        <FormAlert tone="error" title="Devam ettirilemedi">
          {start.error}
        </FormAlert>
      ) : null}

      <div className="flex flex-wrap gap-2">
        {failed > 0 ? <ErrorReportButton job={job} /> : null}
        {writable && canRemap(job) ? (
          <Button variant="secondary" onClick={onBack}>
            Eşlemeye dön
          </Button>
        ) : null}
        {writable && canStart(job) ? (
          <Button icon={<Play className="size-4" />} loading={start.busy} onClick={() => void start.run(job)}>
            {isResume(job) ? "Kaldığı yerden devam et" : "Aktarmayı başlat"}
          </Button>
        ) : null}
        {mayRollback && (rollback === "available" || rollback === "resume") ? (
          <Button variant="danger" icon={rollback === "resume" ? <RotateCcw className="size-4" /> : <Undo2 className="size-4" />} onClick={() => setRollbackOpen(true)}>
            {rollback === "resume" ? "Geri almayı sürdür" : "Geri al"}
          </Button>
        ) : null}
      </div>

      {job.startedAt || job.validatedAt ? <ImportRows jobId={job.id} version={`${job.version}`} options={options} caption="Aktarma satırları" /> : null}

      {rollbackOpen ? <RollbackDialog job={job} onClose={() => setRollbackOpen(false)} onJob={onJob} onReload={onReload} /> : null}
    </Panel>
  );
}

function RollbackDialog({ job, onClose, onJob, onReload }: { job: ImportJob; onClose: () => void; onJob: (job: ImportJob) => void; onReload: () => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [keys] = useState(() => new IdempotencyKeys());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const created = num(job.progress.created);
  const updated = num(job.progress.updated);
  const resume = job.status === "rollback_failed";

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      const next = await rollbackImport(api, job, keys.keyFor({ id: job.id, version: job.version }));
      keys.reset();
      toast({ tone: "info", title: "Geri alma başladı", description: "İlerlemeyi bu sayfada görebilirsiniz." });
      onJob(next);
      onClose();
    } catch (e) {
      setError(importErrorMessage(e));
      setBusy(false);
      if (e instanceof ApiError && (e.status === 409 || e.status === 412)) onReload();
    }
  };

  return (
    <Dialog
      open
      role="alertdialog"
      dismissOnBackdrop={false}
      onClose={onClose}
      title={resume ? "Geri almayı sürdür" : "Aktarmayı geri al"}
      description={resume ? "Geri alma, kaldığı satırdan devam eder; işlenmiş satırlara tekrar dokunulmaz." : "Bu işlem yalnızca bir kez yapılabilir."}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" loading={busy} onClick={() => void confirm()}>
            {resume ? "Geri almayı sürdür" : "Geri al"}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {error ? (
          <FormAlert tone="error" title="Geri alınamadı">
            {error}
          </FormAlert>
        ) : null}
        <ul className="list-disc space-y-1 pl-5">
          {job.type === "products" ? (
            <li>
              Bu aktarmayla eklenen <strong className="font-semibold text-fg">{formatCount(created)} ürün silinmez, arşivlenir</strong>: listelerden kalkar,
              geçmiş kayıtları (sipariş, stok hareketi) korunur.
            </li>
          ) : (
            <li>
              Bu aktarmayla eklenen <strong className="font-semibold text-fg">{formatCount(created)} fiyat</strong> listeden kaldırılır.
            </li>
          )}
          <li>
            Güncellenen <strong className="font-semibold text-fg">{formatCount(updated)} kaydın</strong> değişen alanları
            {job.priceListId ? " ve fiyatları" : ""} aktarmadan önceki değerine döner.
          </li>
          <li>
            Aktarmadan sonra elle değiştirilmiş kayıtlara <strong className="font-semibold text-fg">dokunulmaz</strong>; bu satırlar “çakışma” olarak
            işaretlenir ve sonuçta listelenir.
          </li>
          <li>Stok miktarları değişmez.</li>
        </ul>
        {job.rollbackDeadline && !resume ? <p className="text-caption text-fg-muted">Son geri alma tarihi: {formatDateTime(job.rollbackDeadline)}</p> : null}
      </div>
    </Dialog>
  );
}

function CancelDialog({ job, onClose, onJob, onReload }: { job: ImportJob; onClose: () => void; onJob: (job: ImportJob) => void; onReload: () => void }) {
  const { api } = useSession();
  const [keys] = useState(() => new IdempotencyKeys());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const running = job.status === "running";

  const confirm = async () => {
    if (!canCancel(job)) return onClose();
    setBusy(true);
    setError(null);
    try {
      const next = await cancelImport(api, job.id, keys.keyFor({ id: job.id, version: job.version }));
      keys.reset();
      onJob(next);
      onClose();
    } catch (e) {
      setError(importErrorMessage(e));
      setBusy(false);
      if (e instanceof ApiError && (e.status === 409 || e.status === 412)) onReload();
    }
  };

  return (
    <Dialog
      open
      role="alertdialog"
      dismissOnBackdrop={false}
      onClose={onClose}
      title="Aktarmayı iptal et"
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" loading={busy} onClick={() => void confirm()}>
            İptal et
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {error ? (
          <FormAlert tone="error" title="İptal edilemedi">
            {error}
          </FormAlert>
        ) : null}
        <p>
          {running
            ? "İşlenmiş satırlar kalır, kalan satırlar işlenmez. İsterseniz daha sonra kaldığı yerden devam ettirebilirsiniz."
            : "Henüz hiçbir kayıt yazılmadı. Aktarma iptal edilir; dosya geçmişte kalır."}
        </p>
      </div>
    </Dialog>
  );
}

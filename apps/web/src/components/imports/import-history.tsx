"use client";

import { useCallback, useEffect, useMemo, useState, useId } from "react";
import Link from "next/link";
import { CircleAlert, FileUp, Lock, Plus } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Table, type TableColumn } from "@/components/ui/table";
import { NEW_IMPORT_HREF, importHref, listImportJobs, num, type ImportJob } from "@/lib/imports/api";
import { importErrorMessage } from "@/lib/imports/errors";
import { formatCount, formatDateTime, importTypeLabels, statusLabel, statusTone } from "@/lib/imports/labels";
import { importPermissions, runnableTypes, type ImportPermissions } from "@/lib/imports/permissions";
import { listMembers } from "@/lib/members/api";
import { useSession } from "@/lib/session/session-context";

type HistoryState =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; items: ImportJob[]; nextCursor: string | null; loadingMore: boolean; moreError: string | null };

/** Result counts of a job in one line: what was written, or what the preview plans. */
export function resultSummary(job: ImportJob): string {
  const p = job.progress;
  if (job.startedAt !== null || ["running", "completed", "rolling_back", "rolled_back", "partially_rolled_back", "rollback_failed"].includes(job.status)) {
    const parts = [`${formatCount(num(p.created))} eklendi`, `${formatCount(num(p.updated))} güncellendi`];
    if (num(p.skipped) > 0) parts.push(`${formatCount(num(p.skipped))} atlandı`);
    if (num(p.failed) > 0) parts.push(`${formatCount(num(p.failed))} başarısız`);
    if (job.rollback) parts.push(`${formatCount(num(job.rollback.rolledBack))} geri alındı`);
    return parts.join(" · ");
  }
  if (job.validatedAt !== null) {
    const v = job.preview;
    return `Önizleme: ${formatCount(num(v.create))} eklenecek · ${formatCount(num(v.update))} güncellenecek · ${formatCount(num(v.error))} hatalı`;
  }
  return `${formatCount(num(job.totalRows))} satır`;
}

/** Locked screen for roles without imports.run (e.g. the operations manager, who only holds imports.run.stock). */
export function ImportsLocked({ permissions }: { permissions: ImportPermissions }) {
  return (
    <section aria-label="İçe aktarma erişimi" className="rounded-lg border border-line bg-card shadow-e1">
      <EmptyState
        icon={<Lock className="size-6" />}
        title="İçe aktarma için yetkiniz yok"
        description={
          permissions.stockOnly
            ? "Rolünüz yalnızca stok içe aktarabilir; stok içe aktarma henüz kullanıma açılmadı. Ürün ve fiyat aktarmayı işletme sahibi veya yönetici yapar."
            : "Excel/CSV ile ürün ve fiyat aktarmayı işletme sahibi ve yöneticiler yapar. Gerekirse işletme yöneticinizle görüşün."
        }
      />
    </section>
  );
}

/** Member names for the "Kullanıcı" column (members.read); without it only "Siz" is resolved. */
function useMemberNames(enabled: boolean) {
  const { api } = useSession();
  const [names, setNames] = useState<Map<string, string>>(new Map());
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    listMembers(api, { limit: 200 }, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        setNames(new Map(page.items.map((m) => [m.userId, m.displayName || m.fullName || m.email || "—"])));
      })
      .catch(() => {
        // Names are a convenience; the history still works without them.
      });
    return () => controller.abort();
  }, [api, enabled]);
  return names;
}

/** /ice-aktarma (W06): import history (newest first, keyset "Daha fazla") and the entry to the wizard. */
export function ImportHistory() {
  const uid = useId();
  const { api, access, state: session } = useSession();
  const permissions = importPermissions(access);
  const canStartAny = runnableTypes(permissions).length > 0;
  const [state, setState] = useState<HistoryState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const names = useMemberNames(permissions.run && access.canSome("members.read"));
  const userId = session.status === "ready" ? session.user.id : null;

  useEffect(() => {
    if (!permissions.run) return;
    const controller = new AbortController();
    listImportJobs(api, { signal: controller.signal })
      .then((page) => {
        if (!controller.signal.aborted)
          setState({ status: "ready", items: page.items, nextCursor: page.page.hasMore ? page.page.nextCursor : null, loadingMore: false, moreError: null });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", message: importErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, permissions.run, attempt]);

  const reload = useCallback(() => {
    setState({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  const loadMore = async () => {
    if (state.status !== "ready" || !state.nextCursor || state.loadingMore) return;
    setState({ ...state, loadingMore: true, moreError: null });
    try {
      const page = await listImportJobs(api, { cursor: state.nextCursor });
      setState({
        status: "ready",
        items: [...state.items, ...page.items.filter((j) => !state.items.some((x) => x.id === j.id))],
        nextCursor: page.page.hasMore ? page.page.nextCursor : null,
        loadingMore: false,
        moreError: null,
      });
    } catch (error) {
      setState({ ...state, loadingMore: false, moreError: importErrorMessage(error) });
    }
  };

  const userName = useCallback(
    (id: string | null) => (id === null ? "—" : id === userId ? "Siz" : (names.get(id) ?? "Ekip üyesi")),
    [names, userId],
  );

  const columns: TableColumn<ImportJob>[] = useMemo(
    () => [
      {
        key: "file",
        header: "Dosya",
        cell: (j) => (
          <Link href={importHref(j.id)} className="font-medium break-all text-brand-fg underline-offset-2 hover:underline">
            {j.fileName}
          </Link>
        ),
      },
      { key: "type", header: "Tür", width: "11rem", cell: (j) => importTypeLabels[j.type] ?? j.type },
      { key: "status", header: "Durum", width: "11rem", cell: (j) => <Badge tone={statusTone(j.status)}>{statusLabel(j.status)}</Badge> },
      { key: "user", header: "Kullanıcı", width: "10rem", cell: (j) => userName(j.createdBy) },
      { key: "date", header: "Tarih", width: "10rem", cell: (j) => formatDateTime(j.createdAt) },
      { key: "result", header: "Sonuç", cell: (j) => <span className="text-caption text-fg-secondary">{resultSummary(j)}</span> },
    ],
    [userName],
  );

  const items = state.status === "ready" ? state.items : [];

  return (
    <>
      <PageHeader
        title="İçe aktarma"
        description="Excel (.xlsx) veya CSV dosyasından ürünleri ve fiyat listesi kalemlerini toplu aktarın. Önce önizleme yapılır; onaylamadan hiçbir kayıt yazılmaz."
        actions={
          canStartAny ? (
            <Link href={NEW_IMPORT_HREF} className={buttonClasses("primary")}>
              <Plus className="size-4" aria-hidden="true" />
              Yeni içe aktarma
            </Link>
          ) : null
        }
      />

      {!permissions.run ? (
        <ImportsLocked permissions={permissions} />
      ) : (
        <section aria-labelledby={`${uid}-import-history`} className="flex flex-col gap-3">
          <h2 id={`${uid}-import-history`} className="text-h3 text-fg">
            İçe aktarma geçmişi
          </h2>
          {state.status === "error" ? (
            <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
              <EmptyState
                headingLevel="h3"
                icon={<CircleAlert className="size-6" />}
                title="İçe aktarma geçmişi yüklenemedi"
                description={state.message}
                action={<Button onClick={reload}>Tekrar dene</Button>}
              />
            </div>
          ) : (
            <>
              <div className="hidden md:block">
                <Table
                  caption="İçe aktarma geçmişi"
                  columns={columns}
                  rows={items}
                  getRowId={(j) => j.id}
                  loading={state.status === "loading"}
                  loadingRows={3}
                  empty={<HistoryEmpty canStart={canStartAny} />}
                />
              </div>
              <ul aria-label="İçe aktarma geçmişi" className="flex flex-col gap-2 md:hidden">
                {state.status === "loading" ? <li className="py-6 text-center text-body text-fg-muted">Yükleniyor…</li> : null}
                {state.status === "ready" && items.length === 0 ? (
                  <li className="rounded-lg border border-line bg-card">
                    <HistoryEmpty canStart={canStartAny} />
                  </li>
                ) : null}
                {items.map((j) => (
                  <li key={j.id} className="flex flex-col gap-1 rounded-lg border border-line bg-card p-4 shadow-e1">
                    <div className="flex items-start justify-between gap-2">
                      <Link href={importHref(j.id)} className="min-h-11 py-2 font-medium break-all text-brand-fg underline-offset-2 hover:underline">
                        {j.fileName}
                      </Link>
                      <Badge tone={statusTone(j.status)}>{statusLabel(j.status)}</Badge>
                    </div>
                    <p className="text-caption text-fg-muted">
                      {importTypeLabels[j.type] ?? j.type} · {userName(j.createdBy)} · {formatDateTime(j.createdAt)}
                    </p>
                    <p className="text-caption text-fg-secondary">{resultSummary(j)}</p>
                  </li>
                ))}
              </ul>
              {state.status === "ready" && state.moreError ? (
                <p role="alert" className="text-body text-danger-fg">
                  {state.moreError}
                </p>
              ) : null}
              {state.status === "ready" && state.nextCursor ? (
                <div>
                  <Button variant="secondary" loading={state.loadingMore} onClick={() => void loadMore()}>
                    Daha fazla göster
                  </Button>
                </div>
              ) : null}
            </>
          )}
        </section>
      )}
    </>
  );
}

function HistoryEmpty({ canStart }: { canStart: boolean }) {
  return (
    <EmptyState
      headingLevel="h3"
      icon={<FileUp className="size-6" />}
      title="Henüz içe aktarma yapılmadı"
      description="Ürün listenizi veya fiyat listenizi Excel/CSV dosyasından aktarın; aktarmalar burada listelenir."
      action={
        canStart ? (
          <Link href={NEW_IMPORT_HREF} className={buttonClasses("secondary")}>
            Yeni içe aktarma
          </Link>
        ) : undefined
      }
    />
  );
}

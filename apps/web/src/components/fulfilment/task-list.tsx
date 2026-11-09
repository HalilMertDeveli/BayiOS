"use client";

import { useCallback, useEffect, useState, useId } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ChevronRight, CircleAlert, ClipboardList, Lock, RefreshCw, Truck } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { cn } from "@/lib/cn";
import {
  SHIPMENTS_HREF,
  assignTask,
  etagOf,
  listMyTasks,
  listTasks,
  setTaskPriority,
  taskCommand,
  taskHref,
  taskListQuery,
  type TaskListFilter,
  type TaskSummary,
} from "@/lib/fulfilment/api";
import { BUSY_FINAL_MESSAGE, fulfilmentErrorMessage, isFulfilmentBusy, isVersionConflict } from "@/lib/fulfilment/errors";
import { TASK_ACTIVE_STATUSES, TASK_DONE_STATUSES, formatDeliveryDate, taskPriorityMeta, taskPriorityOptions, taskTypeLabels } from "@/lib/fulfilment/labels";
import { fulfilmentPermissions, type FulfilmentPermissions } from "@/lib/fulfilment/permissions";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { formatDateTime } from "@/lib/inventory/labels";
import { formatQuantity, toNumber } from "@/lib/inventory/quantity";
import { listActiveMembers, memberLabel, type Member } from "@/lib/api/identity";
import { roleLabels } from "@/lib/navigation";
import { toRole } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";
import { ProgressBar, TaskPriorityBadge, TaskStatusBadge } from "./fulfilment-badges";
import { PriorityDialog } from "./task-supervisor";

type Tab = "mine" | "pool" | "all";
type AllFilter = "active" | "done" | "any";

const TAB_LABELS: Record<Tab, string> = { mine: "Görevlerim", pool: "Havuz", all: "Tüm görevler" };
const FILTER_STATUSES: Record<AllFilter, readonly string[]> = { active: TASK_ACTIVE_STATUSES, done: TASK_DONE_STATUSES, any: [] };

type ListData =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; items: TaskSummary[]; nextCursor: string | null; hasMore: boolean };

type Keyed = { key: string; value: ListData };


/**
 * Tabs from the effective permissions, in order: tasks.read.own → Görevlerim + Havuz; tasks.read.all (any scope; sales
 * see the tasks of their own orders) → "Tüm görevler".
 */
export function tabsFor(permissions: FulfilmentPermissions): Tab[] {
  const tabs: Tab[] = [];
  if (permissions.readOwn) tabs.push("mine", "pool");
  if (permissions.readAll) tabs.push("all");
  return tabs;
}

/** /depo/gorevler: the warehouse work list (W12 / mobile M02 on the web). */
export function TaskList() {
  const { access } = useSession();
  const permissions = fulfilmentPermissions(access);
  const tabs = tabsFor(permissions);
  if (tabs.length === 0) {
    return (
      <>
        <PageHeader title="Depo görevleri" />
        <section aria-label="Görevler" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Depo görevlerini görme yetkiniz yok" description="Rolünüz depo görevlerini görmeye izin vermiyor. Gerekirse işletme yöneticinizle görüşün." />
        </section>
      </>
    );
  }
  return <TaskListView tabs={tabs} permissions={permissions} />;
}

function TaskListView({ tabs, permissions }: { tabs: Tab[]; permissions: FulfilmentPermissions }) {
  const uid = useId();
  const { api, state } = useSession();
  const { toast } = useToast();
  const router = useRouter();
  const myId = state.status === "ready" ? state.user.id : null;
  const [tab, setTab] = useState<Tab>(tabs[0]);
  const [filter, setFilter] = useState<AllFilter>("active");
  const [attempt, setAttempt] = useState(0);
  const [keyed, setKeyed] = useState<Keyed | null>(null);
  const [priorityFilter, setPriorityFilter] = useState("");
  const currentKey = `${tab}|${filter}|${priorityFilter}|${attempt}`;
  const data: ListData = keyed?.key === currentKey ? keyed.value : { status: "loading" };
  const setData = (value: ListData) => setKeyed({ key: currentKey, value });
  const [more, setMore] = useState(false);
  const [pending, setPending] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ tone: "error" | "warning"; title: string; text: string } | null>(null);
  const [assignFor, setAssignFor] = useState<TaskSummary | null>(null);
  const [priorityFor, setPriorityFor] = useState<TaskSummary | null>(null);
  const [priorityError, setPriorityError] = useState<string | null>(null);
  const [savingPriority, setSavingPriority] = useState(false);
  const allFilter: TaskListFilter = { statuses: FILTER_STATUSES[filter], priority: priorityFilter || null };

  const refresh = useCallback(() => setAttempt((n) => n + 1), []);

  useEffect(() => {
    const key = `${tab}|${filter}|${priorityFilter}|${attempt}`;
    const controller = new AbortController();
    const load =
      tab === "all"
        ? listTasks(api, taskListQuery({ statuses: FILTER_STATUSES[filter], priority: priorityFilter || null }), controller.signal).then((page) => ({
            items: page.items,
            nextCursor: page.page.nextCursor ?? null,
            hasMore: page.page.hasMore,
          }))
        : listMyTasks(api, tab === "mine" ? "assigned" : "pool", controller.signal).then((items) => ({ items, nextCursor: null, hasMore: false }));
    load
      .then((result) => {
        if (!controller.signal.aborted) setKeyed({ key, value: { status: "ready", ...result } });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setKeyed({ key, value: { status: "error", message: fulfilmentErrorMessage(error) } });
      });
    return () => controller.abort();
  }, [api, tab, filter, priorityFilter, attempt]);

  const loadMore = async () => {
    if (data.status !== "ready" || !data.nextCursor || more) return;
    setMore(true);
    try {
      const page = await listTasks(api, taskListQuery(allFilter, data.nextCursor));
      setData({ status: "ready", items: [...data.items, ...page.items], nextCursor: page.page.nextCursor ?? null, hasMore: page.page.hasMore });
    } catch (error) {
      setNotice({ tone: "error", title: "Devamı yüklenemedi", text: fulfilmentErrorMessage(error) });
    } finally {
      setMore(false);
    }
  };

  /** Runs one task command; busy → a single retry after Retry-After with the same Idempotency-Key. */
  const run = async (task: TaskSummary, action: "claim" | "unassign" | "assign", userId?: string): Promise<boolean> => {
    if (pending) return false;
    setPending(`${task.id}:${action}`);
    setNotice(null);
    const key = crypto.randomUUID();
    // Claim is decided by the API's row lock; assign/unassign are supervisor edits checked against the shown version.
    const etag = action === "claim" ? null : etagOf(task.version);
    try {
      await withBusyRetry(
        () =>
          action === "assign"
            ? assignTask(api, task.id, userId!, { idempotencyKey: key, etag })
            : taskCommand(api, task.id, action, { idempotencyKey: key, etag }),
        { isBusy: isFulfilmentBusy },
      );
      if (action === "claim") {
        toast({ tone: "success", title: `${task.orderNumber} görevi size atandı` });
        router.push(taskHref(task.id));
      } else {
        toast({ tone: "success", title: action === "unassign" ? `${task.orderNumber} havuza bırakıldı` : `${task.orderNumber} atandı` });
        refresh();
      }
      return true;
    } catch (error) {
      if (error instanceof ApiError && error.code === "picking.already_assigned") {
        setNotice({ tone: "warning", title: "Görev başkası tarafından alındı", text: fulfilmentErrorMessage(error) });
      } else if (isVersionConflict(error)) {
        setNotice({ tone: "warning", title: "Görev değişmiş", text: "Görev siz bakarken güncellendi; liste yenilendi. Tekrar deneyin." });
      } else {
        setNotice({ tone: "error", title: "İşlem tamamlanamadı", text: isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error) });
        return false;
      }
      refresh();
      return false;
    } finally {
      setPending(null);
    }
  };

  /** tasks.assign: POST /picking-tasks/{id}/priority with If-Match on the shown version (412 → list reloads). */
  const savePriority = async (value: string) => {
    const task = priorityFor;
    if (!task || savingPriority) return;
    setSavingPriority(true);
    setPriorityError(null);
    const key = crypto.randomUUID();
    try {
      await withBusyRetry(() => setTaskPriority(api, task.id, value, { idempotencyKey: key, etag: etagOf(task.version) }), { isBusy: isFulfilmentBusy });
      toast({ tone: "success", title: `${task.orderNumber}: öncelik ${taskPriorityMeta[value]?.label ?? value}` });
      setPriorityFor(null);
      refresh();
    } catch (error) {
      if (isVersionConflict(error)) {
        setPriorityFor(null);
        setNotice({ tone: "warning", title: "Görev değişmiş", text: "Görev siz bakarken güncellendi; liste yenilendi. Tekrar deneyin." });
        refresh();
      } else {
        setPriorityError(isFulfilmentBusy(error) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(error));
      }
    } finally {
      setSavingPriority(false);
    }
  };

  const assigneeText = (task: TaskSummary) =>
    task.assignedTo === null ? "Havuzda (atanmamış)" : task.assignedTo === myId ? "Bana atandı" : task.assigneeName ? `Atanan: ${task.assigneeName}` : "Başka personele atandı";

  return (
    <>
      <PageHeader
        title="Depo görevleri"
        description="Toplama görevleri raf sırasıyla. Görevi açın, önce rafı sonra ürünü okutun."
        actions={
          <span className="flex flex-wrap gap-2">
            {permissions.readDetail ? (
              <Link href={SHIPMENTS_HREF} className={cn(buttonClasses("secondary", "lg"), "min-h-12")}>
                <Truck className="size-5" aria-hidden="true" />
                Sevkiyatlar
              </Link>
            ) : null}
            <Button size="lg" variant="secondary" className="min-h-12" icon={<RefreshCw className="size-5" />} onClick={refresh} loading={data.status === "loading"}>
              Yenile
            </Button>
          </span>
        }
      />

      {tabs.length > 1 ? (
        <div role="tablist" aria-label="Görev listeleri" className="flex gap-1 overflow-x-auto rounded-lg bg-sunken p-1">
          {tabs.map((t) => (
            <button
              key={t}
              type="button"
              role="tab"
              id={`${uid}-tab-${t}`}
              aria-selected={tab === t}
              aria-controls={`${uid}-task-panel`}
              onClick={() => {
                setTab(t);
                setNotice(null);
              }}
              className={cn(
                "min-h-12 flex-1 rounded-md px-4 text-body font-semibold whitespace-nowrap",
                tab === t ? "bg-card text-fg shadow-e1" : "text-fg-secondary hover:text-fg",
              )}
            >
              {TAB_LABELS[t]}
            </button>
          ))}
        </div>
      ) : null}

      <div id={`${uid}-task-panel`} role={tabs.length > 1 ? "tabpanel" : undefined} aria-labelledby={tabs.length > 1 ? `${uid}-tab-${tab}` : undefined} className="flex flex-col gap-3">
        {tab === "all" ? (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:max-w-2xl">
            <Select
              label="Durum"
              size="lg"
              value={filter}
              onChange={(e) => setFilter(e.target.value as AllFilter)}
              options={[
                { value: "active", label: "Aktif (bekleyen + toplanan)" },
                { value: "done", label: "Biten (tamamlanan + iptal)" },
                { value: "any", label: "Tümü" },
              ]}
            />
            <Select
              label="Öncelik"
              size="lg"
              value={priorityFilter}
              onChange={(e) => setPriorityFilter(e.target.value)}
              options={[{ value: "", label: "Tüm öncelikler" }, ...taskPriorityOptions]}
            />
          </div>
        ) : null}

        {notice ? (
          <FormAlert tone={notice.tone} title={notice.title}>
            {notice.text}
          </FormAlert>
        ) : null}

        {data.status === "loading" ? (
          <p role="status" className="text-body text-fg-muted">
            Görevler yükleniyor…
          </p>
        ) : data.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState icon={<CircleAlert className="size-6" />} title="Görevler yüklenemedi" description={data.message} action={<Button size="lg" onClick={refresh}>Tekrar dene</Button>} />
          </div>
        ) : data.items.length === 0 ? (
          <section aria-label={TAB_LABELS[tab]} className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState
              icon={<ClipboardList className="size-6" />}
              title={tab === "mine" ? "Size atanmış görev yok" : tab === "pool" ? "Havuzda bekleyen görev yok" : "Bu filtrede görev yok"}
              description={tab === "mine" && permissions.claim ? "Havuz sekmesinden bir görev üstlenin." : "Yeni onaylanan siparişlerin görevleri burada görünür."}
            />
          </section>
        ) : (
          <ul aria-label={TAB_LABELS[tab]} className="flex flex-col gap-3">
            {data.items.map((task) => {
              const toPick = toNumber(task.quantityToPick);
              const picked = toNumber(task.quantityPicked);
              const short = toNumber(task.quantityShort);
              const active = task.status === "open" || task.status === "in_progress";
              const mine = task.assignedTo !== null && task.assignedTo === myId;
              // Detail: tasks.read.all (any task the list showed) or tasks.read.own (own and pool tasks).
              const canOpen = permissions.readAll || permissions.assign || (permissions.readOwn && (mine || task.assignedTo === null));
              return (
                <li key={task.id} data-testid={`task-${task.id}`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="flex min-w-0 flex-col">
                      <h2 className="text-h3 text-fg">
                        {canOpen ? (
                          <Link href={taskHref(task.id)} className="underline-offset-2 hover:underline">
                            Sipariş {task.orderNumber}
                          </Link>
                        ) : (
                          `Sipariş ${task.orderNumber}`
                        )}
                      </h2>
                      {task.customerName ? <span className="text-body text-fg-secondary">{task.customerName}</span> : null}
                      <span className="text-caption text-fg-muted">
                        {taskTypeLabels[task.type] ?? task.type} · {formatQuantity(task.lineCount)} satır · {formatDateTime(task.createdAt)}
                      </span>
                      {formatDeliveryDate(task.requestedDeliveryDate) ? (
                        <span className="text-caption text-fg-secondary">İstenen teslim: {formatDeliveryDate(task.requestedDeliveryDate)}</span>
                      ) : null}
                    </div>
                    <span className="flex flex-wrap items-center gap-2">
                      <TaskPriorityBadge priority={task.priority} />
                      <TaskStatusBadge status={task.status} />
                    </span>
                  </div>
                  <ProgressBar
                    value={picked + short}
                    max={toPick}
                    label={`${formatQuantity(picked)} / ${formatQuantity(toPick)} toplandı${short > 0 ? ` · ${formatQuantity(short)} eksik` : ""}`}
                  />
                  <p className="text-caption text-fg-secondary">{assigneeText(task)}</p>
                  {active ? (
                    <div className="flex flex-wrap gap-2">
                      {mine && permissions.pick ? (
                        <Link href={taskHref(task.id)} className={cn(buttonClasses("primary", "lg"), "min-h-12 flex-1 sm:flex-none")}>
                          {task.status === "open" ? "Toplamaya başla" : "Toplamaya devam et"}
                          <ChevronRight className="size-5" aria-hidden="true" />
                        </Link>
                      ) : null}
                      {task.assignedTo === null && permissions.claim ? (
                        <Button size="lg" className="min-h-12 flex-1 sm:flex-none" loading={pending === `${task.id}:claim`} disabled={pending !== null} onClick={() => void run(task, "claim")}>
                          Üstlen
                        </Button>
                      ) : null}
                      {permissions.assign && task.assignedTo === null && !permissions.claim && myId ? (
                        <Button size="lg" className="min-h-12" loading={pending === `${task.id}:assign`} disabled={pending !== null} onClick={() => void run(task, "assign", myId)}>
                          Bana ata
                        </Button>
                      ) : null}
                      {permissions.assign ? (
                        <Button size="lg" variant="secondary" className="min-h-12" disabled={pending !== null} onClick={() => setAssignFor(task)}>
                          {task.assignedTo === null ? "Personele ata" : "Başkasına ata"}
                        </Button>
                      ) : null}
                      {permissions.assign && task.assignedTo !== null ? (
                        <Button size="lg" variant="secondary" className="min-h-12" loading={pending === `${task.id}:unassign`} disabled={pending !== null} onClick={() => void run(task, "unassign")}>
                          Havuza bırak
                        </Button>
                      ) : null}
                      {permissions.assign && task.type === "pick" ? (
                        <Button
                          size="lg"
                          variant="secondary"
                          className="min-h-12"
                          aria-label={`Önceliği değiştir: Sipariş ${task.orderNumber}`}
                          disabled={pending !== null}
                          onClick={() => {
                            setPriorityError(null);
                            setPriorityFor(task);
                          }}
                        >
                          Önceliği değiştir
                        </Button>
                      ) : null}
                    </div>
                  ) : null}
                </li>
              );
            })}
          </ul>
        )}
        {data.status === "ready" && data.hasMore ? (
          <Button size="lg" variant="secondary" className="min-h-12 self-center" loading={more} onClick={() => void loadMore()}>
            Daha fazla yükle
          </Button>
        ) : null}
      </div>

      <PriorityDialog
        key={priorityFor ? `${priorityFor.id}:${priorityFor.version}` : "closed"}
        task={priorityFor}
        busy={savingPriority}
        error={priorityError}
        onClose={() => setPriorityFor(null)}
        onSave={(value) => void savePriority(value)}
      />

      <AssignDialog
        key={assignFor?.id ?? "closed"}
        task={assignFor}
        myId={myId}
        busy={assignFor !== null && pending === `${assignFor.id}:assign`}
        onClose={() => setAssignFor(null)}
        onAssign={async (userId) => {
          if (!assignFor) return;
          const ok = await run(assignFor, "assign", userId);
          if (ok) setAssignFor(null);
        }}
      />
    </>
  );
}

/**
 * Supervisor assignment: "me" or an active member from GET /members (members.read), sorted by name. The API checks
 * that the chosen member's role can pick (422 otherwise, shown in the dialog via the toast/notice).
 */
function AssignDialog({
  task,
  myId,
  busy,
  onClose,
  onAssign,
}: {
  task: TaskSummary | null;
  myId: string | null;
  busy: boolean;
  onClose: () => void;
  onAssign: (userId: string) => void;
}) {
  const { api, access } = useSession();
  const canListMembers = access.canSome("members.read");
  const [members, setMembers] = useState<{ status: "loading" | "error" | "ready"; items: Member[] }>({ status: canListMembers ? "loading" : "ready", items: [] });
  const [target, setTarget] = useState(myId ?? "");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!task || !canListMembers) return;
    const controller = new AbortController();
    // Only people whose role may pick (tasks.pick): the API refuses any other assignee (422).
    listActiveMembers(api, controller.signal, { permissions: ["tasks.pick"] })
      .then((items) => !controller.signal.aborted && setMembers({ status: "ready", items }))
      .catch(() => !controller.signal.aborted && setMembers({ status: "error", items: [] }));
    return () => controller.abort();
  }, [api, task, canListMembers]);

  const options = [
    ...(myId ? [{ value: myId, label: "Bana" }] : []),
    ...members.items.filter((m) => m.userId !== myId).map((m) => ({ value: m.userId, label: `${memberLabel(m)} · ${roleLabels[toRole(m.role)]}` })),
  ];
  const submit = () => {
    if (!target) {
      setError("Görevi atayacağınız kişiyi seçin.");
      return;
    }
    setError(null);
    onAssign(target);
  };
  return (
    <Dialog
      open={task !== null}
      onClose={() => !busy && onClose()}
      title={task ? `Sipariş ${task.orderNumber} görevini ata` : "Görevi ata"}
      description="Görev atanan kişinin listesinde görünür; yalnızca o kişi toplayabilir."
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button size="lg" loading={busy} onClick={submit}>
            Ata
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Select
          label="Kime"
          size="lg"
          value={target}
          placeholder={members.status === "loading" ? "Ekip yükleniyor…" : "Personel seçin"}
          hint="Yalnızca toplama yetkisi olan personel listelenir."
          error={error ?? (members.status === "error" ? "Ekip listesi yüklenemedi; görevi kendinize atayabilirsiniz." : undefined)}
          onChange={(e) => setTarget(e.target.value)}
          options={options}
        />
      </div>
    </Dialog>
  );
}

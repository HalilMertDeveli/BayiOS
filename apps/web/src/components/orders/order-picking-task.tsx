"use client";

import { useCallback, useEffect, useRef, useState, useId } from "react";
import Link from "next/link";
import { ChevronRight, ClipboardList, Plus } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { TaskPriorityBadge, TaskStatusBadge } from "@/components/fulfilment/fulfilment-badges";
import { Button, buttonClasses } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { cn } from "@/lib/cn";
import { createPickingTask, listTasks, taskHref, taskListQuery, type TaskSummary } from "@/lib/fulfilment/api";
import { BUSY_FINAL_MESSAGE, fulfilmentErrorMessage, isFulfilmentBusy } from "@/lib/fulfilment/errors";
import { TASK_ACTIVE_STATUSES, taskPriorityMeta, taskPriorityOptions } from "@/lib/fulfilment/labels";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";

type State = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; task: TaskSummary | null };

/**
 * Approved order → its pick task (#98). With the tenant setting "Sipariş onaylanınca toplama görevi otomatik
 * oluşturulsun" off, approval reserves stock but creates no task: a supervisor (tasks.assign) picks a priority and
 * creates it here (POST /picking-tasks). When the order already has an active task, it is shown with a link.
 */
export function OrderPickingTask({ orderId, orderNumber }: { orderId: string; orderNumber: string }) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [state, setState] = useState<State>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [priority, setPriority] = useState("normal");
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const errorRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  useEffect(() => {
    const controller = new AbortController();
    listTasks(api, taskListQuery({ orderId, type: "pick", statuses: TASK_ACTIVE_STATUSES }), controller.signal)
      .then((page) => !controller.signal.aborted && setState({ status: "ready", task: page.items[0] ?? null }))
      .catch((e: unknown) => !controller.signal.aborted && setState({ status: "error", message: fulfilmentErrorMessage(e) }));
    return () => controller.abort();
  }, [api, orderId, attempt]);

  const create = async () => {
    if (creating) return;
    setCreating(true);
    setError(null);
    const key = keys.keyFor({ orderId, priority });
    try {
      const created = await withBusyRetry(() => createPickingTask(api, { orderId, priority }, key), { isBusy: isFulfilmentBusy });
      keys.reset();
      toast({ tone: "success", title: `${orderNumber}: toplama görevi oluşturuldu`, description: `Öncelik ${taskPriorityMeta[priority]?.label ?? priority}; görev depo havuzunda.` });
      setState({ status: "ready", task: created.detail.task });
    } catch (e) {
      if (e instanceof ApiError && e.code === "picking.fulfillment_in_progress") reload();
      setError(isFulfilmentBusy(e) ? BUSY_FINAL_MESSAGE : fulfilmentErrorMessage(e));
      focusAfterCommit(() => errorRef.current);
    } finally {
      setCreating(false);
    }
  };

  return (
    <section aria-labelledby={`${uid}-picking`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
      <h2 id={`${uid}-picking`} className="flex items-center gap-2 text-h3 text-fg">
        <ClipboardList className="size-5" aria-hidden="true" />
        Toplama görevi
      </h2>
      {error ? (
        <FormAlert ref={errorRef} tone="error" title="Görev oluşturulamadı">
          {error}
        </FormAlert>
      ) : null}
      {state.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Toplama görevi yükleniyor…
        </p>
      ) : state.status === "error" ? (
        <div role="alert" className="flex flex-wrap items-center gap-3">
          <p className="text-body text-danger-fg">Toplama görevi yüklenemedi: {state.message}</p>
          <Button variant="secondary" onClick={() => (setState({ status: "loading" }), reload())}>
            Tekrar dene
          </Button>
        </div>
      ) : state.task ? (
        <div className="flex flex-wrap items-center gap-3">
          <TaskPriorityBadge priority={state.task.priority} />
          <TaskStatusBadge status={state.task.status} />
          <span className="text-body text-fg-secondary">{state.task.assigneeName ? `Atanan: ${state.task.assigneeName}` : state.task.assignedTo ? "Personele atandı" : "Havuzda"}</span>
          <Link href={taskHref(state.task.id)} className={cn(buttonClasses("secondary"), "ml-auto")}>
            Görevi aç
            <ChevronRight className="size-4" aria-hidden="true" />
          </Link>
        </div>
      ) : (
        <>
          <p className="text-body text-fg-secondary">
            Bu sipariş için aktif toplama görevi yok; stok ayrıldı. Önceliği seçip görevi oluşturun, görev depo havuzuna düşer.
          </p>
          <div className="flex flex-wrap items-end gap-2">
            <Select label="Öncelik" className="sm:max-w-xs" value={priority} options={taskPriorityOptions} onChange={(e) => setPriority(e.target.value)} />
            <Button icon={<Plus className="size-4" />} loading={creating} onClick={() => void create()}>
              Toplama görevi oluştur
            </Button>
          </div>
        </>
      )}
    </section>
  );
}

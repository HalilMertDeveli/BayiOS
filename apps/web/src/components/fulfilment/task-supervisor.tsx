"use client";

import { useRef, useState, useId } from "react";
import { Shuffle, Save } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { etagOf, reallocateTask, setTaskPriority, type LoadedTask, type TaskLine } from "@/lib/fulfilment/api";
import { BUSY_FINAL_MESSAGE, fulfilmentErrorMessage, isFulfilmentBusy, isStaleFulfilment, isVersionConflict } from "@/lib/fulfilment/errors";
import { isTaskPriority, taskPriorityMeta, taskPriorityOptions } from "@/lib/fulfilment/labels";
import { inactiveShelfLines, isOpenLine } from "@/lib/fulfilment/pick-session";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { formatQuantity } from "@/lib/inventory/quantity";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";

/** Message for a failed supervisor command; a stale view also reloads the task. */
function failure(error: unknown): { text: string; stale: boolean } {
  if (isVersionConflict(error)) return { text: "Görev siz bakarken güncellendi; güncel hâli yüklendi. Kontrol edip tekrar deneyin.", stale: true };
  if (isFulfilmentBusy(error)) return { text: BUSY_FINAL_MESSAGE, stale: false };
  return { text: fulfilmentErrorMessage(error), stale: isStaleFulfilment(error) };
}

/** "Kaynak raf" choices for a re-allocation: inactive shelves (the default) or one shelf with open lines. */
export function reallocationSources(lines: readonly TaskLine[]): { value: string; label: string }[] {
  const seen = new Map<string, string>();
  for (const line of lines) {
    if (!line.locationId || !isOpenLine(line) || seen.has(line.locationId)) continue;
    seen.set(line.locationId, `${line.locationCode ?? "Raf"}${line.locationIsActive === false ? " (pasif)" : ""}`);
  }
  return [{ value: "", label: "Pasif raflardaki tüm açık miktar" }, ...[...seen].map(([value, label]) => ({ value, label }))];
}

/**
 * Supervisor tools on an active pick task (tasks.assign, #98): change the priority (If-Match) and "Yeniden dağıt" —
 * move the open quantity off deactivated shelves (or off one shelf) onto active shelves with free stock. The answer says
 * how much moved and how much could not (no free stock elsewhere); lines come back in the API's walking order.
 */
export function TaskSupervisorPanel({ loaded, onLoaded, onReload }: { loaded: LoadedTask; onLoaded: (loaded: LoadedTask) => void; onReload: () => void }) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const { task, lines } = loaded.detail;
  const [priority, setPriority] = useState(task.priority);
  const [from, setFrom] = useState("");
  const [pending, setPending] = useState<"priority" | "reallocate" | null>(null);
  const [notice, setNotice] = useState<{ tone: "success" | "warning" | "error"; title: string; text: string } | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const noticeRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const sources = reallocationSources(lines);
  const inactive = inactiveShelfLines(lines);
  const show = (next: typeof notice) => {
    setNotice(next);
    if (next) focusAfterCommit(() => noticeRef.current);
  };

  // A newer version from elsewhere (scan, reload) resets the choice to what the task now has.
  const [seenVersion, setSeenVersion] = useState(task.version);
  if (seenVersion !== task.version) {
    setSeenVersion(task.version);
    setPriority(task.priority);
    if (from && !sources.some((s) => s.value === from)) setFrom("");
  }

  const savePriority = async () => {
    if (pending || priority === task.priority || !isTaskPriority(priority)) return;
    setPending("priority");
    setNotice(null);
    const key = keys.keyFor({ action: "priority", id: task.id, version: task.version, priority });
    try {
      const next = await withBusyRetry(() => setTaskPriority(api, task.id, priority, { idempotencyKey: key, etag: etagOf(task.version) }), {
        isBusy: isFulfilmentBusy,
      });
      keys.reset();
      onLoaded(next);
      toast({ tone: "success", title: `Öncelik: ${taskPriorityMeta[priority].label}` });
    } catch (error) {
      const { text, stale } = failure(error);
      show({ tone: stale ? "warning" : "error", title: "Öncelik değiştirilemedi", text });
      if (stale) onReload();
    } finally {
      setPending(null);
    }
  };

  const reallocate = async () => {
    if (pending) return;
    setPending("reallocate");
    setNotice(null);
    const fromLocationId = from || null;
    const key = keys.keyFor({ action: "reallocate", id: task.id, version: task.version, fromLocationId });
    try {
      const result = await withBusyRetry(() => reallocateTask(api, task.id, fromLocationId, { idempotencyKey: key, etag: etagOf(task.version) }), {
        isBusy: isFulfilmentBusy,
      });
      keys.reset();
      onLoaded(result.loaded);
      setFrom("");
      if (result.moved === 0 && result.unmoved === 0) {
        show({ tone: "warning", title: "Taşınacak açık miktar yok", text: "Seçilen raflarda toplanmayı bekleyen ürün kalmamış; görev değişmedi." });
      } else {
        show({
          tone: result.unmoved > 0 ? "warning" : "success",
          title: result.unmoved > 0 ? "Kısmen yeniden dağıtıldı" : "Yeniden dağıtıldı",
          text: `${formatQuantity(result.moved)} birim boş stoğu olan aktif raflara taşındı (rezervasyonuyla birlikte).${
            result.unmoved > 0
              ? ` ${formatQuantity(result.unmoved)} birim taşınamadı: başka aktif rafta yeterli satılabilir stok yok. Stok gelince tekrar deneyin veya eksik bildirin.`
              : ""
          }`,
        });
      }
    } catch (error) {
      const { text, stale } = failure(error);
      show({ tone: stale ? "warning" : "error", title: "Yeniden dağıtılamadı", text });
      if (stale) onReload();
    } finally {
      setPending(null);
    }
  };

  return (
    <section aria-labelledby={`${uid}-supervisor`} className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1">
      <h2 id={`${uid}-supervisor`} className="text-h3 text-fg">
        Yönetici işlemleri
      </h2>
      {notice ? (
        <FormAlert ref={noticeRef} tone={notice.tone} title={notice.title}>
          {notice.text}
        </FormAlert>
      ) : null}
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap items-end gap-2">
          <Select
            label="Öncelik"
            size="lg"
            className="sm:max-w-xs"
            value={priority}
            options={taskPriorityOptions}
            onChange={(e) => setPriority(e.target.value)}
          />
          <Button
            size="lg"
            variant="secondary"
            className="min-h-12"
            icon={<Save className="size-5" />}
            loading={pending === "priority"}
            disabled={pending !== null || priority === task.priority}
            onClick={() => void savePriority()}
          >
            Önceliği kaydet
          </Button>
        </div>
        <p className="text-caption text-fg-muted">Mobil iş listesi önceliğe göre sıralanır; Acil görevler en üstte görünür.</p>
      </div>
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap items-end gap-2">
          <Select
            label="Yeniden dağıtılacak raf"
            size="lg"
            className="sm:max-w-xs"
            value={from}
            options={sources}
            onChange={(e) => setFrom(e.target.value)}
          />
          <Button
            size="lg"
            variant="secondary"
            className="min-h-12"
            icon={<Shuffle className="size-5" />}
            loading={pending === "reallocate"}
            disabled={pending !== null}
            onClick={() => void reallocate()}
          >
            Yeniden dağıt
          </Button>
        </div>
        <p className="text-caption text-fg-muted">
          {inactive.length > 0
            ? `${inactive.length} satır pasif bir rafta. “Yeniden dağıt” açık miktarı boş stoğu olan aktif raflara taşır; ayrılmış stok da birlikte taşınır.`
            : "Açık miktarı seçilen raftan boş stoğu olan aktif raflara taşır; ayrılmış stok da birlikte taşınır. Toplanan ve eksik bildirilen miktar yerinde kalır."}
        </p>
      </div>
    </section>
  );
}

/** Priority dialog for the task list (tasks.assign): one select, saved with If-Match on the shown version. */
export function PriorityDialog({
  task,
  busy,
  error,
  onClose,
  onSave,
}: {
  task: { orderNumber: string; priority: string } | null;
  busy: boolean;
  error: string | null;
  onClose: () => void;
  onSave: (priority: string) => void;
}) {
  const [value, setValue] = useState(task?.priority ?? "normal");
  return (
    <Dialog
      open={task !== null}
      onClose={() => !busy && onClose()}
      title={task ? `Sipariş ${task.orderNumber}: öncelik` : "Öncelik"}
      description="Mobil iş listesi önceliğe göre sıralanır; Acil görevler en üstte görünür."
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button size="lg" loading={busy} disabled={!task || value === task.priority} onClick={() => onSave(value)}>
            Kaydet
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Select label="Öncelik" size="lg" value={value} options={taskPriorityOptions} onChange={(e) => setValue(e.target.value)} />
        {error ? (
          <p role="alert" className="text-body text-danger-fg">
            {error}
          </p>
        ) : null}
      </div>
    </Dialog>
  );
}

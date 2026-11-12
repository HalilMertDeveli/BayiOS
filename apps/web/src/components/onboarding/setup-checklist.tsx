"use client";

import { useCallback, useEffect, useId, useState } from "react";
import Link from "next/link";
import { CircleCheck, Circle, ListChecks, X } from "@/components/ui/icons";
import { Button } from "@/components/ui/button";
import { loadSetupFacts } from "@/lib/onboarding/facts";
import { SETUP_HREF, canRunSetup, checklistDismissKey, checklistItems, checklistProgress, type SetupFacts } from "@/lib/onboarding/setup";
import { useSession } from "@/lib/session/session-context";
import { usePersistentBoolean } from "@/lib/use-persistent-boolean";

export type FactsState = { status: "loading" } | { status: "error" } | { status: "ready"; facts: SetupFacts };

/** Setup progress computed from the tenant's data (GET warehouses / products / balances / members / orders). */
export function useSetupFacts(enabled = true): { state: FactsState; refresh: () => void } {
  const { api, access } = useSession();
  const [state, setState] = useState<FactsState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    loadSetupFacts(api, access, controller.signal)
      .then((facts) => {
        if (!controller.signal.aborted) setState({ status: "ready", facts });
      })
      .catch(() => {
        if (!controller.signal.aborted) setState({ status: "error" });
      });
    return () => controller.abort();
  }, [api, access, enabled, attempt]);
  const refresh = useCallback(() => setAttempt((n) => n + 1), []);
  return { state, refresh };
}

/** The rows of the checklist; shared by the dashboard card and the wizard's last step. */
export function ChecklistRows({ facts, headingLevel = 3 }: { facts: SetupFacts; headingLevel?: 3 | 4 }) {
  const Heading = headingLevel === 3 ? "h3" : "h4";
  return (
    <ol className="flex flex-col divide-y divide-line">
      {checklistItems(facts).map(({ item, done }) => (
        <li key={item.key} className="flex flex-col gap-2 py-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex min-w-0 items-start gap-3">
            {done ? (
              <CircleCheck className="mt-0.5 size-5 shrink-0 text-success-fg" aria-hidden="true" />
            ) : (
              <Circle className="mt-0.5 size-5 shrink-0 text-fg-muted" aria-hidden="true" />
            )}
            <div className="flex min-w-0 flex-col gap-0.5">
              <Heading className="text-body font-medium text-fg">
                {item.title}{" "}
                <span className="sr-only">{done ? "(tamamlandı)" : "(yapılmadı)"}</span>
              </Heading>
              <p className="text-caption text-fg-muted">{done ? item.doneText : item.todoText}</p>
            </div>
          </div>
          {done ? null : (
            <Link
              href={item.href}
              className="ml-8 inline-flex min-h-11 w-fit shrink-0 items-center rounded-md px-1 text-body font-medium text-brand-fg underline-offset-2 hover:underline sm:ml-0"
            >
              {item.actionLabel}
            </Link>
          )}
        </li>
      ))}
    </ol>
  );
}

/**
 * Dashboard "İlk adımlar" (#83): for owner/admin (tenant.settings.manage), progress from real data, dismissible per user
 * and tenant (a local preference). Hidden while loading or when nothing could be read, so it never flashes.
 */
export function SetupChecklist() {
  const { access, state: session } = useSession();
  const headingId = useId();
  const userId = session.status === "ready" ? session.user.id : "";
  const tenantId = session.status === "ready" ? (session.active?.tenantId ?? "") : "";
  const allowed = canRunSetup(access) && !!userId && !!tenantId;
  const [dismissed, setDismissed] = usePersistentBoolean(checklistDismissKey(userId, tenantId), false);
  const { state } = useSetupFacts(allowed && !dismissed);

  if (!allowed || dismissed || state.status !== "ready") return null;
  const { done, total } = checklistProgress(state.facts);
  if (total === 0) return null;
  const complete = done === total;

  // Everything done: a one-line confirmation instead of the full list, so the dashboard leads with today's work.
  if (complete) {
    return (
      <section aria-labelledby={headingId} className="flex items-center gap-3 rounded-xl border border-success-border bg-success-bg px-4 py-3 text-success-fg">
        <CircleCheck className="size-5 shrink-0" aria-hidden="true" />
        <div className="flex min-w-0 flex-1 flex-col gap-0.5 sm:flex-row sm:items-baseline sm:gap-2">
          <h2 id={headingId} className="text-body font-semibold">
            İlk adımlar
          </h2>
          <p className="text-body">Kurulum tamamlandı; {total} adımın hepsi bitti. Bu bildirimi kapatabilirsiniz.</p>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => setDismissed(true)}
          icon={<X className="size-4" />}
          aria-label="İlk adımlar listesini kapat"
        >
          <span className="hidden sm:inline">Kapat</span>
        </Button>
      </section>
    );
  }

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-3 rounded-lg border border-brand-border bg-card p-4 shadow-e1 sm:p-6">
      <div className="flex items-start justify-between gap-3">
        <div className="flex flex-col gap-1">
          <h2 id={headingId} className="flex items-center gap-2 text-h3 text-fg">
            <ListChecks className="size-5 shrink-0 text-brand-fg" aria-hidden="true" />
            İlk adımlar
          </h2>
          <p className="text-body text-fg-secondary">
            {complete ? "Kurulum tamamlandı. Bu listeyi artık kapatabilirsiniz." : `${total} adımdan ${done} tanesi tamamlandı.`}
          </p>
        </div>
        <Button variant="ghost" size="sm" onClick={() => setDismissed(true)} icon={<X className="size-4" />} aria-label="İlk adımlar listesini kapat">
          <span className="hidden sm:inline">Kapat</span>
        </Button>
      </div>
      <div
        role="progressbar"
        aria-label="Kurulum ilerlemesi"
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={done}
        aria-valuetext={`${total} adımdan ${done} tamamlandı`}
        className="h-2 overflow-hidden rounded-full bg-sunken"
      >
        <div className="h-full rounded-full bg-brand" style={{ width: `${Math.round((done / total) * 100)}%` }} />
      </div>
      <ChecklistRows facts={state.facts} />
      {complete ? null : (
        <Link href={SETUP_HREF} className="w-fit text-body font-medium text-brand-fg underline-offset-2 hover:underline">
          Kurulum sihirbazını aç
        </Link>
      )}
    </section>
  );
}

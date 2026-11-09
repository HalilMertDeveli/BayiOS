"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/lib/api/client";
import { getImportJob, num, type ImportJob } from "@/lib/imports/api";
import { importErrorMessage } from "@/lib/imports/errors";
import { startPolling } from "@/lib/imports/polling";
import { isActive } from "@/lib/imports/steps";
import { useSession } from "@/lib/session/session-context";

export type JobLoad = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; job: ImportJob };

/** What the user sees change: status + every counter (a change resets the poll backoff). */
export function jobFingerprint(job: ImportJob): string {
  const p = job.progress;
  const r = job.rollback;
  return [job.status, job.version, num(p.processedRows), num(p.failed), r ? `${num(r.rolledBack)}/${num(r.conflicts)}/${num(r.failed)}/${num(r.skipped)}` : ""].join("|");
}

/**
 * One import job: loaded once, then polled while a background job owns it (validating / running / rolling_back).
 * Polling backs off while nothing changes, pauses in a hidden tab and stops when the job settles.
 */
export function useImportJob(id: string) {
  const { api } = useSession();
  const [load, setLoad] = useState<JobLoad>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [pollError, setPollError] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    getImportJob(api, id, controller.signal)
      .then((job) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", job });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: importErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, id, attempt]);

  const active = load.status === "ready" && isActive(load.job.status);
  const status = load.status === "ready" ? load.job.status : null;

  useEffect(() => {
    if (!active) return;
    return startPolling<ImportJob>({
      fetch: (signal) => getImportJob(api, id, signal),
      onUpdate: (job) => {
        setPollError(false);
        setLoad({ status: "ready", job });
      },
      onError: () => setPollError(true),
      shouldContinue: (job) => isActive(job.status),
      fingerprint: jobFingerprint,
    });
    // Restart when the active status changes (e.g. validating → running after start).
  }, [api, id, active, status]);

  /** Replace the job with an action's response (PUT mapping, validate, start, cancel, rollback). */
  const setJob = useCallback((job: ImportJob) => setLoad({ status: "ready", job }), []);
  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  return { load, setJob, reload, pollError };
}

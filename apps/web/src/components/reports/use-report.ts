"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useUrlQuery } from "@/lib/use-url-query";
import { istanbulToday, rangeError, rangeFromParams, type DateRange } from "@/lib/reports/date-range";
import { reportErrorMessage } from "@/lib/reports/errors";

export type ReportState<T> =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; data: T };

type Settled<T> = { key: string; status: "ready"; data: T } | { key: string; status: "error"; message: string };

/**
 * Loads a report for `key` (the serialised filters; null = do not request, e.g. an invalid range). A new key shows
 * "loading" straight away and aborts the previous request; `reload` repeats the same key.
 */
export function useReport<T>(key: string | null, load: (signal: AbortSignal) => Promise<T>) {
  const [settled, setSettled] = useState<Settled<T> | null>(null);
  const [attempt, setAttempt] = useState(0);
  const loadRef = useRef(load);
  useEffect(() => {
    loadRef.current = load;
  });
  const fullKey = key === null ? null : `${key}#${attempt}`;

  useEffect(() => {
    if (fullKey === null) return;
    const controller = new AbortController();
    loadRef.current(controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setSettled({ key: fullKey, status: "ready", data });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setSettled({ key: fullKey, status: "error", message: reportErrorMessage(error) });
      });
    return () => controller.abort();
  }, [fullKey]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  let state: ReportState<T>;
  if (fullKey === null) state = { status: "idle" };
  else if (settled?.key === fullKey) state = settled.status === "ready" ? { status: "ready", data: settled.data } : { status: "error", message: settled.message };
  else state = { status: "loading" };
  return { state, reload };
}

const copyParams = (params: { toString(): string }) => new URLSearchParams(params.toString());
const paramsToString = (params: URLSearchParams) => params.toString();

/**
 * Report filters in the URL (shareable, back button): `baslangic` / `bitis` (yyyy-MM-dd, default last 30 days) plus
 * report-specific parameters. "Today" is the Istanbul calendar day, fixed for the page's lifetime.
 */
export function useReportUrl() {
  // Patches build on the URL just requested, not on the stale search params (#152): see useUrlQuery.
  const { searchParams: params, latest, navigate } = useUrlQuery(copyParams, paramsToString);
  const [today] = useState(() => istanbulToday());
  const range = rangeFromParams(params, today);

  const update = useCallback(
    (patch: Record<string, string | null>) => {
      const next = new URLSearchParams(latest());
      for (const [name, value] of Object.entries(patch)) {
        if (value === null || value === "") next.delete(name);
        else next.set(name, value);
      }
      navigate(next);
    },
    [latest, navigate],
  );

  const setRange = useCallback((next: DateRange) => update({ baslangic: next.from, bitis: next.to }), [update]);
  return { today, range, rangeError: rangeError(range), params, update, setRange };
}

"use client";

import { useCallback, useEffect, useState } from "react";
import { documentErrorMessage } from "@/lib/stock-documents/errors";

type Page<T> = { items: T[]; page: { nextCursor: string | null; hasMore: boolean } };

export type ListData<T> =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; items: T[]; nextCursor: string | null; hasMore: boolean };

/**
 * Keyset list (server-side paging) keyed by the filter: a changed `key` shows "loading" until its own first page has
 * arrived, so an answer for an older filter never shows under the new one. `enabled = false` (no permission) fetches nothing.
 */
export function useDocumentList<T extends { id: string }>(
  key: string,
  fetchPage: (cursor: string | null, signal?: AbortSignal) => Promise<Page<T>>,
  enabled: boolean,
) {
  const [attempt, setAttempt] = useState(0);
  const currentKey = `${key}#${attempt}`;
  const [keyed, setKeyed] = useState<{ key: string; value: ListData<T> } | null>(null);
  const [more, setMore] = useState<{ loading: boolean; error: string | null }>({ loading: false, error: null });

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    fetchPage(null, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        setKeyed({ key: currentKey, value: { status: "ready", items: page.items, nextCursor: page.page.nextCursor ?? null, hasMore: page.page.hasMore } });
        setMore({ loading: false, error: null });
      })
      .catch((error: unknown) => !controller.signal.aborted && setKeyed({ key: currentKey, value: { status: "error", message: documentErrorMessage(error) } }));
    return () => controller.abort();
    // fetchPage is rebuilt every render; its inputs are encoded in `key`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentKey, enabled]);

  const data: ListData<T> = keyed?.key === currentKey ? keyed.value : { status: "loading" };

  const loadMore = async () => {
    if (data.status !== "ready" || !data.nextCursor) return;
    setMore({ loading: true, error: null });
    try {
      const page = await fetchPage(data.nextCursor);
      setKeyed((prev) => {
        if (!prev || prev.key !== currentKey || prev.value.status !== "ready") return prev;
        const items = prev.value.items;
        return {
          key: currentKey,
          value: {
            status: "ready",
            items: [...items, ...page.items.filter((x) => !items.some((y) => y.id === x.id))],
            nextCursor: page.page.nextCursor ?? null,
            hasMore: page.page.hasMore,
          },
        };
      });
      setMore({ loading: false, error: null });
    } catch (error) {
      setMore({ loading: false, error: documentErrorMessage(error) });
    }
  };

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  return { data, more, loadMore, reload };
}

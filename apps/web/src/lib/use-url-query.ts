"use client";

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

type Params = { get(name: string): string | null; forEach(cb: (value: string, key: string) => void): void; toString(): string };

/**
 * List filters kept in the URL (shareable, back button), safe against the stale-query race (#136, #152).
 *
 * `router.replace` runs as a transition: until it commits, `useSearchParams` (so `query`) still holds the previous URL.
 * A second change in that window ("clear" and then a date, a filter and then the debounced search…) must build on the
 * URL just requested, not on the stale query; otherwise it silently brings back what the first change removed. So every
 * change goes through `navigate`, and `latest()` is the query to patch: the pending one until the URL has caught up,
 * then the URL again (back/forward included — a history navigation also drops the pending one).
 *
 * `parse` and `serialize` must be stable (module-level functions).
 */
export function useUrlQuery<Q>(parse: (params: Params) => Q, serialize: (query: Q) => string) {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const query = useMemo(() => parse(searchParams), [parse, searchParams]);
  const currentSearch = serialize(query);

  const pending = useRef<{ search: string; query: Q } | null>(null);
  const latestRef = useRef({ query, currentSearch });
  // Layout effect: up to date before any event handler or timer of this commit can call navigate / latest.
  useLayoutEffect(() => {
    latestRef.current = { query, currentSearch };
    if (pending.current?.search === currentSearch) pending.current = null;
  }, [query, currentSearch]);
  useEffect(() => {
    const drop = () => {
      pending.current = null;
    };
    window.addEventListener("popstate", drop);
    return () => window.removeEventListener("popstate", drop);
  }, []);

  /** The query a change should patch: the one just requested if the URL has not caught up yet. */
  const latest = useCallback(() => pending.current?.query ?? latestRef.current.query, []);

  const navigate = useCallback(
    (next: Q) => {
      const search = serialize(next);
      // Compared in the form the URL will parse back to (e.g. an invalid date is dropped), so it always catches up.
      const expected = parse(new URLSearchParams(search));
      pending.current = search === latestRef.current.currentSearch ? null : { search: serialize(expected), query: expected };
      router.replace(search ? `${pathname}?${search}` : pathname, { scroll: false });
    },
    [parse, serialize, pathname, router],
  );

  return { query, searchParams, latest, navigate };
}

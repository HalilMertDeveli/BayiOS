"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { getCustomer } from "@/lib/customers/api";
import { listWarehouses, type Warehouse } from "@/lib/inventory/api";
import { useSession } from "@/lib/session/session-context";

export type CustomerName = { name: string; code: string };

/**
 * Orders carry only `customerId` / `warehouseId`. Names are looked up once per session view: warehouses with one list
 * call, customers one by one (deduplicated; the API has no batch lookup — reported as a contract gap). A failed lookup
 * leaves the id unresolved and the UI falls back to a neutral label.
 */
export function useCustomerNames(ids: readonly string[]): ReadonlyMap<string, CustomerName> {
  const { api } = useSession();
  const [names, setNames] = useState<ReadonlyMap<string, CustomerName>>(() => new Map());
  const requested = useRef(new Set<string>());
  const key = useMemo(() => [...new Set(ids)].sort().join(","), [ids]);

  useEffect(() => {
    const missing = key.split(",").filter((id) => id && !requested.current.has(id));
    if (missing.length === 0) return;
    missing.forEach((id) => requested.current.add(id));
    // Results are kept even if the visible ids change meanwhile (names are a cache, not view state).
    void Promise.all(
      missing.map((id) =>
        getCustomer(api, id).then(
          ({ detail }) => [id, { name: detail.customer.name, code: detail.customer.code }] as const,
          () => null,
        ),
      ),
    ).then((results) => {
      const found = results.filter((r): r is readonly [string, CustomerName] => r !== null);
      if (found.length === 0) return;
      setNames((prev) => {
        const next = new Map(prev);
        for (const [id, value] of found) next.set(id, value);
        return next;
      });
    });
  }, [api, key]);

  return names;
}

export type WarehouseState = { status: "loading" } | { status: "ready"; items: Warehouse[] } | { status: "error" };

/** All warehouses (inactive too, so old orders still show a name), default first. */
export function useWarehouses(): WarehouseState {
  const { api } = useSession();
  const [state, setState] = useState<WarehouseState>({ status: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    listWarehouses(api, { includeInactive: true, signal: controller.signal })
      .then((items) => {
        if (!controller.signal.aborted) setState({ status: "ready", items });
      })
      .catch(() => {
        if (!controller.signal.aborted) setState({ status: "error" });
      });
    return () => controller.abort();
  }, [api]);
  return state;
}

export function warehouseLabel(state: WarehouseState, id: string): string {
  const w = state.status === "ready" ? state.items.find((x) => x.id === id) : undefined;
  return w ? `${w.name} (${w.code})` : "Depo";
}

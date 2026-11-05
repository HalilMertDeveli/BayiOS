"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getVariantsBatch, listLocations, listWarehouses, variantBatches, type Location, type Warehouse } from "@/lib/inventory/api";
import { inventoryErrorMessage } from "@/lib/inventory/errors";
import type { VariantRef } from "@/lib/inventory/receipt-form";
import { useSession } from "@/lib/session/session-context";

export type Loadable<T> = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: T };

/** All warehouses of the tenant (incl. inactive ones, which the UI marks); default first. */
export function useWarehouses() {
  const { api } = useSession();
  const [state, setState] = useState<Loadable<Warehouse[]>>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    listWarehouses(api, { includeInactive: true, signal: controller.signal })
      .then((data) => {
        if (!controller.signal.aborted) setState({ status: "ready", data });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", message: inventoryErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  /** Replaces one warehouse after a successful create/update without a refetch. */
  const upsert = useCallback((warehouse: Warehouse) => {
    setState((prev) => {
      if (prev.status !== "ready") return prev;
      const others = prev.data.filter((w) => w.id !== warehouse.id).map((w) => (warehouse.isDefault ? { ...w, isDefault: false } : w));
      const data = [...others, warehouse].sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.code.localeCompare(b.code, "tr"));
      return { status: "ready", data };
    });
  }, []);
  return { state, reload, upsert };
}

/** Locations of one warehouse ("" = none). Re-fetches when the warehouse changes. */
export function useLocations(warehouseId: string, options: { includeInactive?: boolean; enabled?: boolean } = {}) {
  const { api } = useSession();
  const enabled = options.enabled ?? true;
  const includeInactive = options.includeInactive ?? false;
  const [state, setState] = useState<{ key: string; value: Loadable<Location[]> } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const key = `${warehouseId}|${includeInactive}|${attempt}`;

  useEffect(() => {
    if (!warehouseId || !enabled) return;
    const controller = new AbortController();
    listLocations(api, warehouseId, { includeInactive, signal: controller.signal })
      .then((data) => {
        if (!controller.signal.aborted) setState({ key, value: { status: "ready", data } });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ key, value: { status: "error", message: inventoryErrorMessage(error) } });
      });
    return () => controller.abort();
  }, [api, warehouseId, includeInactive, enabled, key]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  const current: Loadable<Location[]> =
    !warehouseId || !enabled ? { status: "ready", data: [] } : state?.key === key ? state.value : { status: "loading" };
  return { state: current, reload };
}

/** Locations of several warehouses, fetched once each (for showing location codes in balance/movement rows). */
export function useLocationNames(warehouseIds: readonly string[]) {
  const { api } = useSession();
  const [names, setNames] = useState<Map<string, string>>(() => new Map());
  const requested = useRef(new Set<string>());
  const idsKey = [...new Set(warehouseIds)].sort().join(",");

  useEffect(() => {
    const missing = idsKey ? idsKey.split(",").filter((id) => !requested.current.has(id)) : [];
    for (const id of missing) {
      requested.current.add(id);
      listLocations(api, id, { includeInactive: true })
        .then((locations) =>
          setNames((prev) => {
            const next = new Map(prev);
            for (const l of locations) next.set(l.id, l.code);
            return next;
          }),
        )
        .catch(() => requested.current.delete(id));
    }
  }, [api, idsKey]);

  return names;
}

/**
 * SKU/name for variant ids shown in stock and price rows. Unknown ids are fetched once with GET /variants/batch
 * (up to 100 per call) and cached for the page's lifetime; an id the API leaves out (deleted, foreign) maps to null.
 */
export function useVariantDirectory(variantIds: readonly string[]) {
  const { api } = useSession();
  const [variants, setVariants] = useState<Map<string, VariantRef | null>>(() => new Map());
  const requested = useRef(new Set<string>());
  const idsKey = [...new Set(variantIds)].sort().join(",");

  useEffect(() => {
    const seen = requested.current;
    const missing = idsKey ? idsKey.split(",").filter((id) => !seen.has(id)) : [];
    if (missing.length === 0) return;
    for (const id of missing) seen.add(id);
    // Not aborted when the visible ids change: the results are a cache, not view state.
    for (const chunk of variantBatches(missing)) {
      getVariantsBatch(api, chunk)
        .then((items) => {
          const found = new Map(items.map((v) => [v.id, { id: v.id, sku: v.sku, name: v.name ?? null, barcode: v.barcode ?? null }] as const));
          setVariants((prev) => {
            const next = new Map(prev);
            for (const id of chunk) next.set(id, found.get(id) ?? null);
            return next;
          });
        })
        .catch(() => {
          // Failed: allow a later effect run to request these ids again.
          for (const id of chunk) seen.delete(id);
        });
    }
  }, [api, idsKey]);

  return variants;
}

export function useWarehouseNames(state: Loadable<Warehouse[]>) {
  return useMemo(() => new Map(state.status === "ready" ? state.data.map((w) => [w.id, w]) : []), [state]);
}

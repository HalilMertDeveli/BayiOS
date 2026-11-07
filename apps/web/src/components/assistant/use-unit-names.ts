"use client";

import { useCallback, useEffect, useState } from "react";
import { unitLabel } from "@/lib/assistant/labels";
import { listUnits } from "@/lib/catalog/api";
import { useSession } from "@/lib/session/session-context";

/**
 * Catalog unit code → the unit's display name (#133: the review card showed "takim" instead of "Takım"). Candidates and
 * proposals carry the catalog unit CODE; the names come from GET /units (archived ones too: a variant may still use
 * one). Until the list is loaded, or if it cannot be read, the Turkish message-unit label / the code itself is shown.
 */
export function useUnitNames(): (code: string | null | undefined) => string | null {
  const { api } = useSession();
  const [names, setNames] = useState<ReadonlyMap<string, string> | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    listUnits(api, controller.signal, { includeArchived: true })
      .then((units) => setNames(new Map(units.map((u) => [u.code, u.name]))))
      .catch(() => {
        // Not fatal: the card falls back to the code (never blocks the review).
      });
    return () => controller.abort();
  }, [api]);

  return useCallback((code) => (code ? (names?.get(code) ?? unitLabel(code)) : null), [names]);
}

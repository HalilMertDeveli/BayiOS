"use client";

import { useCallback, useEffect, useState } from "react";
import { listBrands, listCategories, listCustomFields, listUnits, type Brand, type Category, type CustomField, type Unit } from "@/lib/catalog/api";
import { catalogErrorMessage } from "@/lib/catalog/errors";
import { useSession } from "@/lib/session/session-context";

export type CatalogReference = { brands: Brand[]; units: Unit[]; categories: Category[]; fields: CustomField[] };

export type ReferenceState =
  | { status: "loading" }
  | { status: "error"; message: string }
  | ({ status: "ready" } & CatalogReference);

/** Brands, units, categories and custom field definitions of the active tenant (all unpaged, ≤ 1000 each). */
export function useCatalogReference(): { state: ReferenceState; reload: () => void } {
  const { api } = useSession();
  const [state, setState] = useState<ReferenceState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    Promise.all([listBrands(api, signal), listUnits(api, signal), listCategories(api, signal), listCustomFields(api, { signal })])
      .then(([brands, units, categories, fields]) => {
        if (!signal.aborted) setState({ status: "ready", brands, units, categories, fields });
      })
      .catch((error: unknown) => {
        if (!signal.aborted) setState({ status: "error", message: catalogErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, attempt]);

  const reload = useCallback(() => {
    setState({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  return { state, reload };
}

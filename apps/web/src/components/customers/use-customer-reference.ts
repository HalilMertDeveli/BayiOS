"use client";

import { useCallback, useEffect, useState } from "react";
import { listDealerGroups, type DealerGroup } from "@/lib/customers/api";
import { customerErrorMessage } from "@/lib/customers/errors";
import { listPriceLists, type PriceList } from "@/lib/pricing/api";
import { pricingPermissions } from "@/lib/pricing/permissions";
import { useSession } from "@/lib/session/session-context";

export type CustomerReference =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; groups: DealerGroup[]; priceLists: PriceList[]; priceListsAvailable: boolean };

/**
 * Dealer groups and price lists (archived included, so an assigned archived record still shows its name).
 * Price lists need prices.read.sell; without it (or if that call fails) the page still works, names stay hidden.
 */
export function useCustomerReference() {
  const { api, access } = useSession();
  const canReadPrices = pricingPermissions(access).readSell;
  const [state, setState] = useState<CustomerReference>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const signal = controller.signal;
    Promise.all([
      listDealerGroups(api, { includeArchived: true, signal }),
      canReadPrices ? listPriceLists(api, { includeArchived: true, signal }).catch(() => null) : Promise.resolve(null),
    ])
      .then(([groups, priceLists]) => {
        if (signal.aborted) return;
        setState({ status: "ready", groups, priceLists: priceLists ?? [], priceListsAvailable: priceLists !== null });
      })
      .catch((error: unknown) => {
        if (!signal.aborted) setState({ status: "error", message: customerErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, canReadPrices, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  /** Replaces one group after a create/update without a refetch. */
  const upsertGroup = useCallback((group: DealerGroup) => {
    setState((prev) =>
      prev.status === "ready"
        ? { ...prev, groups: [...prev.groups.filter((g) => g.id !== group.id), group].sort((a, b) => a.name.localeCompare(b.name, "tr")) }
        : prev,
    );
  }, []);
  return { state, reload, upsertGroup };
}

/** Options for an assignment select: active records only, plus the current value if it is archived/unknown. */
export function assignableOptions<T extends { id: string; name: string; status: string }>(
  records: readonly T[],
  current: string,
  filter: (record: T) => boolean = () => true,
): { value: string; label: string }[] {
  const options = records.filter((r) => r.status !== "archived" && filter(r)).map((r) => ({ value: r.id, label: r.name }));
  if (current && !options.some((o) => o.value === current)) {
    const record = records.find((r) => r.id === current);
    options.push({ value: current, label: record ? `${record.name} (arşivlenmiş)` : "Erişilemeyen kayıt" });
  }
  return options;
}

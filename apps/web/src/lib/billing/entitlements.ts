import type { components } from "@/lib/api/schema";
import { SUGGESTIONS, type SubscriptionStatus } from "./labels";

/** `activeTenant.entitlements` of GET /me (#51): plan, status, trial end, features and limits (null = unlimited). */
export type TenantEntitlements = components["schemas"]["TenantEntitlementsResponse"];

/** Feature / limit codes the web gates on (services/api EntitlementCodes). */
export const FEATURES = {
  warehouseLocations: "warehouse_locations",
  aiOrderParsing: "ai_order_parsing",
} as const;

export const LIMITS = {
  maxWarehouses: "max_warehouses",
  maxUsers: "max_users",
  maxProducts: "max_products",
  aiParsesPerMonth: "ai_parses_per_month",
} as const;

/**
 * What the active tenant's plan allows — visibility only. The API enforces every entitlement again (fail-closed) and
 * answers 403 `entitlement.required` / `entitlement.limit_exceeded`; the UI only shows a locked state up front.
 * When /me carried no entitlements (`known: false`) nothing is locked here and the API decides.
 */
export type Entitlements = {
  readonly known: boolean;
  readonly plan: string | null;
  readonly status: SubscriptionStatus | null;
  readonly trialEndsAt: string | null;
  has(feature: string): boolean;
  /** The limit: null = unlimited; a code the API did not send is 0 (the API's own fail-closed rule). */
  limit(code: string): number | null;
};

function toNumber(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined || value === "") return null;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : null;
}

export function createEntitlements(source: TenantEntitlements | null | undefined): Entitlements {
  if (!source) return UNKNOWN_ENTITLEMENTS;
  const features = new Set(source.features ?? []);
  const limits = new Map<string, number | null>((source.limits ?? []).map((l) => [l.code, toNumber(l.value)] as const));
  return {
    known: true,
    plan: source.plan,
    status: source.status,
    trialEndsAt: source.trialEndsAt ?? null,
    has: (feature) => features.has(feature),
    limit: (code) => (limits.has(code) ? (limits.get(code) ?? null) : 0),
  };
}

/** No entitlement data (not loaded, or an older API): nothing is locked in the UI; the API stays authoritative. */
export const UNKNOWN_ENTITLEMENTS: Entitlements = {
  known: false,
  plan: null,
  status: null,
  trialEndsAt: null,
  has: () => true,
  limit: () => null,
};

export type Gate =
  | { locked: false }
  | {
      locked: true;
      /** "feature": the plan lacks it; "limit": one more would exceed the plan limit. */
      kind: "feature" | "limit";
      code: string;
      suggestedPlan: string | null;
      suggestedModule: string | null;
      limit?: number;
      current?: number;
    };

const OPEN: Gate = { locked: false };

function suggestion(code: string) {
  const s = SUGGESTIONS[code];
  return { suggestedPlan: s?.plan ?? null, suggestedModule: s?.module ?? null };
}

/** Locked when the tenant's plan lacks `feature`. */
export function featureGate(entitlements: Entitlements, feature: string): Gate {
  if (entitlements.has(feature)) return OPEN;
  return { locked: true, kind: "feature", code: feature, ...suggestion(feature) };
}

/** Locked when adding one more to `current` would exceed the limit `code` (null = unlimited). */
export function limitGate(entitlements: Entitlements, code: string, current: number): Gate {
  const limit = entitlements.limit(code);
  if (limit === null || current < limit) return OPEN;
  return { locked: true, kind: "limit", code, limit, current, ...suggestion(code) };
}

/** Creating another *active* warehouse (Temel: one active warehouse). Inactive warehouses do not count. */
export function warehouseGate(entitlements: Entitlements, warehouses: readonly { status: string }[]): Gate {
  return limitGate(entitlements, LIMITS.maxWarehouses, warehouses.filter((w) => w.status === "active").length);
}

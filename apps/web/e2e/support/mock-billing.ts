/**
 * MOCK BILLING API FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC, NOT THE REAL API. NO PAYMENTS ANYWHERE.
 *
 * Stand-in for /api/v1/billing/* and the `activeTenant.entitlements` of GET /me, following the contract shapes in
 * packages/contracts/openapi.json and the as-built rules of api-strategy §12.2: reads need billing.read (owner, admin),
 * store changes billing.manage (owner) + Idempotency-Key; 409 billing.module_unavailable / module_already_active /
 * trial_used / module_not_active; activation is free and time-limited. The catalog mirrors BillingCatalog 2026-11-09.1
 * (names, features, limits — Basic 999 users / warehouses / products, OD-02); `overrides` stand in for operator limit
 * overrides (billing.tenant_entitlements) so limit states stay testable. A simplified model, not the real resolver.
 */
import type { Route } from "@playwright/test";
import { entitlementsFor, grantsForRoleName, type FixtureEntitlements, type FixturePlan, type FixtureRules } from "../../src/test/me-fixtures";

type ModuleStatus = "included_in_trial" | "trialing" | "active" | "expired" | "cancelled" | "available" | "unavailable";

export type MockModule = {
  code: string;
  name: string;
  available: boolean;
  status: ModuleStatus;
  expiresAt: string | null;
  canActivate: boolean;
  canDeactivate: boolean;
  /** The free activation was already used (activate → 409 billing.trial_used). */
  trialUsed?: boolean;
};

export type MockBilling = {
  plan: FixturePlan;
  trialDaysLeft: number;
  modules: MockModule[];
  /** Usage counters that are not derived from other mocks. */
  aiParsesThisMonth: number;
  activeUsers: number;
  /** Active warehouses (max_warehouses usage); the backend wires it to the inventory mock when there is one. */
  activeWarehouses: () => number;
  /** Operator limit overrides (code → value; null = unlimited), applied over the plan like billing.tenant_entitlements. */
  overrides: Record<string, number | null>;
  requests: { method: string; path: string; headers: Record<string, string> }[];
};

const DAY_MS = 24 * 60 * 60 * 1000;
const iso = (offsetDays: number) => new Date(Date.now() + offsetDays * DAY_MS).toISOString();

const CATALOG = {
  catalogVersion: "2026-11-09.1",
  trialPlan: "professional",
  fallbackPlan: "basic",
  trialDays: 30,
  moduleTrialDays: 30,
  plans: [
    {
      code: "basic",
      name: "Temel",
      description: "Ürün, temel stok, sipariş, temel rapor. Dışa aktarma, audit, 2FA ve stok doğruluğu her pakette açıktır.",
      features: [],
      limits: [
        { code: "max_warehouses", value: 999 },
        { code: "max_users", value: 999 },
        { code: "max_products", value: 999 },
      ],
    },
    {
      code: "professional",
      name: "Profesyonel",
      description: "Gelişmiş depo: sınırsız depo ve ürün, raf/lokasyon, barkod operasyonları.",
      features: ["warehouse_locations", "barcode_operations"],
      limits: [
        { code: "max_warehouses", value: null },
        { code: "max_users", value: 999 },
        { code: "max_products", value: null },
      ],
    },
  ],
  modules: [
    {
      code: "ai_assistant",
      name: "AI WhatsApp asistanı",
      description: "WhatsApp sipariş metnini ayrıştırır, katalogla eşleştirir; sipariş her zaman insan onayıyla kesinleşir.",
      available: true,
      features: ["ai_order_parsing"],
      limits: [{ code: "ai_parses_per_month", value: 1000 }],
    },
    { code: "dealer_portal", name: "Bayi portalı", description: "Bayilerin kendi siparişlerini girdiği portal.", available: false, features: ["dealer_portal"], limits: [] },
    { code: "erp_integrations", name: "ERP entegrasyonları", description: "Muhasebe/ERP sistemleriyle senkronizasyon.", available: false, features: ["erp_integrations"], limits: [] },
  ],
};

function defaultModules(plan: FixturePlan): MockModule[] {
  const ai: MockModule =
    plan === "trial"
      ? { code: "ai_assistant", name: "AI WhatsApp asistanı", available: true, status: "included_in_trial", expiresAt: null, canActivate: true, canDeactivate: false }
      : { code: "ai_assistant", name: "AI WhatsApp asistanı", available: true, status: "available", expiresAt: null, canActivate: true, canDeactivate: false };
  const soon = (code: string, name: string): MockModule => ({ code, name, available: false, status: "unavailable", expiresAt: null, canActivate: false, canDeactivate: false });
  return [ai, soon("dealer_portal", "Bayi portalı"), soon("erp_integrations", "ERP entegrasyonları")];
}

export function createMockBilling(
  options: { plan?: FixturePlan; trialDaysLeft?: number; modules?: MockModule[]; overrides?: Record<string, number | null> } = {},
): MockBilling {
  const plan = options.plan ?? "trial";
  return {
    plan,
    trialDaysLeft: options.trialDaysLeft ?? 25,
    modules: options.modules ?? defaultModules(plan),
    aiParsesThisMonth: 12,
    activeUsers: 4,
    activeWarehouses: () => 1,
    overrides: options.overrides ?? {},
    requests: [],
  };
}

/** `activeTenant.entitlements` for GET /me: the plan preset plus the features/limits of active add-ons. */
export function mockEntitlements(billing: MockBilling): FixtureEntitlements {
  const base = entitlementsFor(billing.plan, { trialDaysLeft: billing.trialDaysLeft });
  const features = new Set(base.features);
  const limits = [...base.limits];
  for (const m of billing.modules) {
    if (m.status !== "trialing" && m.status !== "active" && m.status !== "included_in_trial") continue;
    const def = CATALOG.modules.find((c) => c.code === m.code);
    for (const f of def?.features ?? []) features.add(f);
    for (const l of def?.limits ?? []) if (!limits.some((x) => x.code === l.code)) limits.push(l);
  }
  for (const [code, value] of Object.entries(billing.overrides)) {
    const at = limits.findIndex((x) => x.code === code);
    if (at >= 0) limits[at] = { code, value };
    else limits.push({ code, value });
  }
  return { ...base, features: [...features], limits };
}

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const json = (route: Route, status: number, body: unknown) =>
  route.fulfill({ status, contentType: status >= 400 ? "application/problem+json" : "application/json", headers: HEADERS, body: JSON.stringify(body) });
const problem = (status: number, code: string, title = code) => ({ type: `https://bayios.app/problems/${code}`, title, status, code });

const publicModule = (m: MockModule) => ({
  code: m.code,
  name: m.name,
  available: m.available,
  status: m.status,
  expiresAt: m.expiresAt,
  canActivate: m.canActivate,
  canDeactivate: m.canDeactivate,
});

/** Handles /api/v1/billing/*; returns false for other paths. */
export async function handleBilling(route: Route, billing: MockBilling, role: string, rules: Partial<FixtureRules>): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  if (!path.startsWith("/api/v1/billing")) return false;
  const method = request.method();
  billing.requests.push({ method, path, headers: request.headers() });
  const grants = grantsForRoleName(role, rules);
  const has = (permission: string) => grants.some((g) => g.permission === permission && g.scope === "all");

  if (path === "/api/v1/billing/plans" && method === "GET") return json(route, 200, CATALOG).then(() => true);
  if (!has("billing.read")) return json(route, 403, problem(403, "auth.forbidden", "Missing permission: billing.read")).then(() => true);

  const ent = mockEntitlements(billing);
  const limitOf = (code: string) => {
    const l = ent.limits.find((x) => x.code === code);
    return l ? l.value : 0;
  };
  if (path === "/api/v1/billing/subscription" && method === "GET") {
    const plan = CATALOG.plans.find((p) => p.code === ent.plan)!;
    return json(route, 200, {
      plan: ent.plan,
      planName: plan.name,
      status: ent.status,
      trialEndsAt: ent.trialEndsAt,
      catalogVersion: ent.catalogVersion,
      features: ent.features,
      limits: ent.limits,
      modules: billing.modules.map(publicModule),
    }).then(() => true);
  }
  if (path === "/api/v1/billing/modules" && method === "GET") return json(route, 200, { items: billing.modules.map(publicModule) }).then(() => true);
  if (path === "/api/v1/billing/usage" && method === "GET") {
    const item = (code: string, current: number, period: string | null = null) => {
      const limit = limitOf(code);
      return { code, limit, current, remaining: limit === null ? null : Math.max(0, limit - current), period };
    };
    const items = [item("max_warehouses", billing.activeWarehouses()), item("max_users", billing.activeUsers)];
    if (ent.features.includes("ai_order_parsing")) items.push(item("ai_parses_per_month", billing.aiParsesThisMonth, "month"));
    return json(route, 200, { plan: ent.plan, status: ent.status, items }).then(() => true);
  }

  const action = path.match(/^\/api\/v1\/billing\/modules\/([^/]+)\/(activate|deactivate)$/);
  if (action && method === "POST") {
    if (!has("billing.manage")) return json(route, 403, problem(403, "auth.forbidden", "Missing permission: billing.manage")).then(() => true);
    if (!request.headers()["idempotency-key"]) return json(route, 428, problem(428, "precondition.required")).then(() => true);
    const m = billing.modules.find((x) => x.code === action[1]);
    if (!m) return json(route, 404, problem(404, "resource.not_found")).then(() => true);
    if (action[2] === "activate") {
      if (!m.available) return json(route, 409, problem(409, "billing.module_unavailable")).then(() => true);
      if (m.status === "trialing" || m.status === "active") return json(route, 409, problem(409, "billing.module_already_active")).then(() => true);
      if (m.trialUsed && m.status !== "cancelled") return json(route, 409, problem(409, "billing.trial_used")).then(() => true);
      // Started during the tenant trial: the module's own 30 days begin when the trial ends.
      const startOffset = billing.plan === "trial" ? billing.trialDaysLeft : 0;
      Object.assign(m, {
        status: "trialing",
        expiresAt: m.status === "cancelled" && m.expiresAt ? m.expiresAt : iso(startOffset + CATALOG.moduleTrialDays),
        canActivate: false,
        canDeactivate: true,
        trialUsed: true,
      });
      return json(route, 200, publicModule(m)).then(() => true);
    }
    if (m.status !== "trialing" && m.status !== "active") return json(route, 409, problem(409, "billing.module_not_active")).then(() => true);
    Object.assign(m, { status: "cancelled", canActivate: true, canDeactivate: false });
    return json(route, 200, publicModule(m)).then(() => true);
  }
  return json(route, 404, problem(404, "resource.not_found")).then(() => true);
}

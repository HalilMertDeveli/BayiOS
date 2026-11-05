/**
 * MOCK BACKEND FOR PLAYWRIGHT — NOT A REAL SESSION, NOT A REAL API.
 *
 * CI has no Supabase and no API. These helpers fake both at the network/cookie layer only:
 * - a Supabase session cookie in the exact @supabase/ssr format, holding an UNSIGNED fake JWT
 *   (the real API would reject it; nothing here talks to the real API);
 * - `page.route` handlers for the browser's calls to Supabase Auth and to the BayiOS API. GET /me answers with the
 *   active membership's effective permissions from the MOCK fixtures in src/test/me-fixtures.ts (what the real API
 *   returns for that role under the given tenant rules).
 * The real end-to-end run (local Supabase + API, no mocks) lives in e2e-local/ — see apps/web/README.md.
 */
import type { BrowserContext, Page, Route } from "@playwright/test";
import { createMockCatalog, handleCatalog, type MockCatalog } from "./mock-catalog";
import { createMockCommerce, handleCommerce, type MockCommerce } from "./mock-commerce";
import { createMockFulfilment, handleFulfilment, type MockFulfilment } from "./mock-fulfilment";
import { createMockInventory, handleInventory, type MockInventory } from "./mock-inventory";
import { createMockOrders, handleOrders, type MockOrders } from "./mock-orders";
import { grantsForRoleName, type FixtureRules } from "../../src/test/me-fixtures";

/** Must match the build-time env used for the e2e build (ci-web.yml / README). */
export const SUPABASE_URL = process.env.E2E_SUPABASE_URL ?? "http://127.0.0.1:54321";
export const API_URL = process.env.E2E_API_URL ?? "http://localhost:5280";
const APP_ORIGIN = `http://localhost:${process.env.E2E_PORT ?? 3200}`;

export type MockMembership = { tenantId: string; tenantName: string; tenantSlug: string; role: string; status: string };

export const USER = { id: "00000000-0000-4000-8000-000000000001", email: "mock.kullanici@example.test", fullName: "Mock Kullanıcı" };

export const TENANT_A: MockMembership = {
  tenantId: "00000000-0000-4000-8000-0000000000a1",
  tenantName: "Örnek Motor (mock)",
  tenantSlug: "ornek-motor",
  role: "owner",
  status: "active",
};

export const TENANT_B: MockMembership = {
  tenantId: "00000000-0000-4000-8000-0000000000b2",
  tenantName: "Örnek LED (mock)",
  tenantSlug: "ornek-led",
  role: "warehouse_staff",
  status: "active",
};

const b64url = (value: string) => Buffer.from(value).toString("base64url");

/** Unsigned look-alike of a Supabase access token. Only the browser and the (mocked) API ever see it. */
export function fakeAccessToken(): string {
  const now = Math.floor(Date.now() / 1000);
  const header = b64url(JSON.stringify({ alg: "ES256", kid: "mock-kid", typ: "JWT" }));
  const payload = b64url(
    JSON.stringify({ sub: USER.id, email: USER.email, aud: "authenticated", role: "authenticated", iat: now, exp: now + 3600 }),
  );
  return `${header}.${payload}.mock-signature-not-valid`;
}

export function fakeSession() {
  const now = Math.floor(Date.now() / 1000);
  return {
    access_token: fakeAccessToken(),
    token_type: "bearer",
    expires_in: 3600,
    expires_at: now + 3600,
    refresh_token: "mock-refresh-token",
    user: {
      id: USER.id,
      aud: "authenticated",
      role: "authenticated",
      email: USER.email,
      app_metadata: { provider: "email" },
      user_metadata: { full_name: USER.fullName },
      created_at: "2026-11-01T00:00:00Z",
    },
  };
}

/** Sets the session cookie the way @supabase/ssr writes it: sb-<host label>-auth-token = "base64-" + base64url(JSON). */
export async function addSessionCookie(context: BrowserContext) {
  const name = `sb-${new URL(SUPABASE_URL).hostname.split(".")[0]}-auth-token`;
  await context.addCookies([
    { name, value: `base64-${b64url(JSON.stringify(fakeSession()))}`, url: APP_ORIGIN, sameSite: "Lax" },
  ]);
}

const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? "application/problem+json" : "application/json",
    headers: { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After", ...headers },
    body: JSON.stringify(body),
  });

export function problem(status: number, code: string, extra: Record<string, unknown> = {}) {
  return { type: `https://bayios.app/problems/${code}`, title: code, status, code, ...extra };
}

/** Mocked Supabase Auth endpoints used by the browser client. */
export async function mockSupabaseAuth(
  page: Page,
  handlers: { token?: (route: Route) => unknown; signup?: (route: Route) => unknown } = {},
) {
  await page.route(`${SUPABASE_URL}/auth/v1/**`, async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/token")) {
      return handlers.token ? handlers.token(route) : json(route, 200, fakeSession());
    }
    if (url.pathname.endsWith("/signup")) {
      return handlers.signup ? handlers.signup(route) : json(route, 200, fakeSession());
    }
    if (url.pathname.endsWith("/logout")) return route.fulfill({ status: 204 });
    if (url.pathname.endsWith("/user")) return json(route, 200, fakeSession().user);
    if (url.pathname.endsWith("/recover")) return json(route, 200, {});
    return json(route, 404, { error: "not mocked" });
  });
}

export type ApiMockOptions = {
  memberships?: MockMembership[];
  /** Override for POST /api/v1/tenants. */
  createTenant?: (route: Route) => unknown;
  /** Override for POST /api/v1/sector-templates/{code}/apply. */
  applyTemplate?: (route: Route) => unknown;
  /** In-memory Catalog API (mock-catalog.ts). Defaults to a small synthetic catalog; null disables it. */
  catalog?: MockCatalog | null;
  /** In-memory Inventory/Warehouse API (mock-inventory.ts). Defaults to two warehouses and a few balances; null disables it. */
  inventory?: MockInventory | null;
  /** In-memory Customers + Pricing API (mock-commerce.ts). Defaults to 3 customers, 2 dealer groups, 3 price lists; null disables it. */
  commerce?: MockCommerce | null;
  /** In-memory Orders API (mock-orders.ts). Defaults to no orders; null disables it. */
  orders?: MockOrders | null;
  /** In-memory Warehouse fulfilment API (mock-fulfilment.ts): picking tasks + shipments. Defaults to none; null disables it. */
  fulfilment?: MockFulfilment | null;
  /**
   * Tenant rules behind the conditional grants in GET /me (and the orders mock gates). Default: the API defaults, except
   * "sales may approve orders" off (as in `defaultSettings`).
   */
  rules?: Partial<FixtureRules>;
  /** Tenants that are suspended/closed: /me returns no activeTenant for them and every tenant-scoped call gets 403 tenant.inactive. */
  inactiveTenants?: string[];
};

/** MOCK default tenant rules for /me (see ApiMockOptions.rules). */
export const MOCK_RULES: Partial<FixtureRules> = { salesCanApproveOrders: false };

/** Mocked BayiOS API. `state.memberships` can be changed by a test (e.g. after a tenant is created). */
export async function mockApi(page: Page, options: ApiMockOptions = {}) {
  const catalog = options.catalog === undefined ? createMockCatalog({ products: 3 }) : options.catalog;
  const inventory = options.inventory === undefined ? createMockInventory(catalog) : options.inventory;
  const commerce = options.commerce === undefined ? createMockCommerce(catalog) : options.commerce;
  const orders = options.orders === undefined ? createMockOrders() : options.orders;
  const fulfilment = options.fulfilment === undefined ? createMockFulfilment() : options.fulfilment;
  const state = {
    memberships: options.memberships ?? [TENANT_A],
    rules: { ...MOCK_RULES, ...options.rules },
    inactiveTenants: options.inactiveTenants ?? [],
    catalog,
    inventory,
    commerce,
    orders,
    fulfilment,
    requests: [] as { method: string; path: string; search: string; headers: Record<string, string>; body: unknown }[],
  };

  await page.route(`${API_URL}/api/v1/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    if (method === "OPTIONS") {
      return route.fulfill({
        status: 204,
        headers: {
          "Access-Control-Allow-Origin": "*",
          "Access-Control-Allow-Methods": "GET,POST,PUT,PATCH,DELETE",
          "Access-Control-Allow-Headers": "*",
        },
      });
    }
    state.requests.push({ method, path, search: url.search, headers: request.headers(), body: request.postDataJSON?.() ?? null });

    if (path === "/api/v1/meta") return json(route, 200, { name: "BayiOS API (mock)", version: "0.0.0", environment: "Mock" });
    const tenantHeader = request.headers()["x-tenant-id"];
    if (path === "/api/v1/me") {
      // Optional X-Tenant-Id: the active membership's role and effective permissions (MOCK fixtures); null when the
      // header is absent, foreign, or the tenant is not active — exactly as the API does.
      const m = state.memberships.find((x) => x.tenantId === tenantHeader && x.status === "active");
      const activeTenant =
        m && !state.inactiveTenants.includes(m.tenantId)
          ? { tenantId: m.tenantId, role: m.role, permissions: grantsForRoleName(m.role, state.rules) }
          : null;
      return json(route, 200, { userId: USER.id, email: USER.email, fullName: USER.fullName, memberships: state.memberships, activeTenant });
    }
    if (tenantHeader && state.inactiveTenants.includes(tenantHeader)) {
      return json(route, 403, problem(403, "tenant.inactive", { title: "This tenant is not active" }));
    }
    if (path === "/api/v1/members" && method === "GET") {
      // members.read; synthetic members (unsorted on purpose: the web sorts by name).
      const role = state.memberships.find((m) => m.tenantId === tenantHeader)?.role ?? "viewer";
      if (!grantsForRoleName(role, state.rules).some((g) => g.permission === "members.read")) {
        return json(route, 403, problem(403, "auth.forbidden", { title: "Missing permission: members.read" }));
      }
      const member = (userId: string, displayName: string, memberRole: string) => ({
        userId,
        displayName,
        fullName: displayName,
        email: `${userId.slice(-4)}@example.test`,
        role: memberRole,
        status: "active",
        createdAt: "2026-11-01T00:00:00Z",
      });
      const items = [
        member("00000000-0000-4000-8000-7c0000000001", "Deniz Depocu (mock)", "warehouse_staff"),
        member(USER.id, USER.fullName, role),
        member("00000000-0000-4000-8000-7c0000000002", "Ali Toplayıcı (mock)", "warehouse_staff"),
      ];
      return json(route, 200, { items, page: { nextCursor: null, hasMore: false, limit: 200 } });
    }
    if (path === "/api/v1/tenants" && method === "POST") {
      if (options.createTenant) return options.createTenant(route);
      return json(route, 201, {});
    }
    if (path.startsWith("/api/v1/sector-templates/") && path.endsWith("/apply") && method === "POST") {
      if (options.applyTemplate) return options.applyTemplate(route);
      return json(route, 200, { template: path.split("/")[4], unitsCreated: ["ADET"], categoriesCreated: [], fieldsCreated: [], skipped: 0 });
    }
    if (path === "/api/v1/tenant" && method === "GET") {
      const m = state.memberships.find((x) => x.tenantId === request.headers()["x-tenant-id"]);
      return json(route, 200, { ...tenantBody(m), version: 0 }, { ETag: 'W/"0"' });
    }
    if (path === "/api/v1/tenant" && method === "PATCH") {
      const m = state.memberships.find((x) => x.tenantId === request.headers()["x-tenant-id"]);
      return json(route, 200, { ...tenantBody(m), ...(request.postDataJSON() as object), version: 1 }, { ETag: 'W/"1"' });
    }
    if (path === "/api/v1/tenant/settings" && method === "GET") {
      return json(route, 200, defaultSettings, { ETag: 'W/"0"' });
    }
    if (path === "/api/v1/tenant/settings" && method === "PATCH") {
      return json(route, 200, { ...defaultSettings, ...(request.postDataJSON() as object) }, { ETag: 'W/"1"' });
    }
    if (orders) {
      const role = state.memberships.find((m) => m.tenantId === request.headers()["x-tenant-id"])?.role ?? "viewer";
      const taskOrderIds = () => (fulfilment ? fulfilment.tasks.map((t) => t.orderId) : []);
      if (await handleOrders(route, orders, { catalog, commerce, taskOrderIds, rules: state.rules }, role)) return;
    }
    if (fulfilment) {
      const role = state.memberships.find((m) => m.tenantId === request.headers()["x-tenant-id"])?.role ?? "viewer";
      if (await handleFulfilment(route, fulfilment, role)) return;
    }
    if (inventory && (await handleInventory(route, inventory, catalog))) return;
    if (commerce) {
      const role = state.memberships.find((m) => m.tenantId === request.headers()["x-tenant-id"])?.role ?? "viewer";
      if (await handleCommerce(route, commerce, catalog, role)) return;
    }
    if (catalog && (await handleCatalog(route, catalog))) return;
    return json(route, 404, problem(404, "resource.not_found"));
  });
  return state;
}

function tenantBody(m: MockMembership | undefined) {
  return {
    id: m?.tenantId ?? "",
    name: m?.tenantName ?? "",
    slug: m?.tenantSlug ?? "",
    status: "active",
    sector: "general",
    defaultCurrency: "TRY",
    locale: "tr-TR",
    timeZone: "Europe/Istanbul",
    role: m?.role ?? "owner",
    logoObjectPath: null,
    taxNumber: null,
    taxOffice: null,
    phone: null,
  };
}

export const defaultSettings = {
  negativeStockPolicy: "block",
  salesCanApproveOrders: false,
  salesMaxDiscountPercent: 0,
  showCostToManager: true,
  showCostToViewer: false,
  staffCanAssignBarcode: false,
  staffCanApproveReceipt: false,
  require2fa: false,
  defaultTaxRate: 20,
  pricesIncludeTaxDisplay: false,
};

/** Signed-in user with the given memberships; the first one is selected when there is only one. */
export async function signedIn(page: Page, options: ApiMockOptions = {}) {
  await addSessionCookie(page.context());
  await mockSupabaseAuth(page);
  return mockApi(page, options);
}

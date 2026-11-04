/**
 * MOCK BACKEND FOR PLAYWRIGHT — NOT A REAL SESSION, NOT A REAL API.
 *
 * CI has no Supabase and no API. These helpers fake both at the network/cookie layer only:
 * - a Supabase session cookie in the exact @supabase/ssr format, holding an UNSIGNED fake JWT
 *   (the real API would reject it; nothing here talks to the real API);
 * - `page.route` handlers for the browser's calls to Supabase Auth and to the BayiOS API.
 * The real end-to-end run (local Supabase + API, no mocks) lives in e2e-local/ — see apps/web/README.md.
 */
import type { BrowserContext, Page, Route } from "@playwright/test";
import { createMockCatalog, handleCatalog, type MockCatalog } from "./mock-catalog";

/** Must match the build-time env used for the e2e build (ci-web.yml / README). */
export const SUPABASE_URL = process.env.E2E_SUPABASE_URL ?? "http://127.0.0.1:54321";
export const API_URL = process.env.E2E_API_URL ?? "http://localhost:5280";
const APP_ORIGIN = "http://localhost:3200";

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
    headers: { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag", ...headers },
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
};

/** Mocked BayiOS API. `state.memberships` can be changed by a test (e.g. after a tenant is created). */
export async function mockApi(page: Page, options: ApiMockOptions = {}) {
  const catalog = options.catalog === undefined ? createMockCatalog({ products: 3 }) : options.catalog;
  const state = {
    memberships: options.memberships ?? [TENANT_A],
    catalog,
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
          "Access-Control-Allow-Methods": "GET,POST,PATCH,DELETE",
          "Access-Control-Allow-Headers": "*",
        },
      });
    }
    state.requests.push({ method, path, search: url.search, headers: request.headers(), body: request.postDataJSON?.() ?? null });

    if (path === "/api/v1/meta") return json(route, 200, { name: "BayiOS API (mock)", version: "0.0.0", environment: "Mock" });
    if (path === "/api/v1/me") {
      return json(route, 200, { userId: USER.id, email: USER.email, fullName: USER.fullName, memberships: state.memberships });
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

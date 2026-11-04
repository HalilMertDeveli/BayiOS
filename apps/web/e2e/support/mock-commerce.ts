/**
 * MOCK CUSTOMERS + PRICING API FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API.
 *
 * Stand-in for /api/v1/customers, /api/v1/dealer-groups, /api/v1/price-lists and /api/v1/pricing/quote following
 * the contract shapes in packages/contracts/openapi.json and the as-built notes in docs/architecture/data-model.md
 * §5.4/§5.5: keyset-style `page.nextCursor`, ETag/If-Match → 412, Idempotency-Key replay, masked 11-digit tax numbers
 * in lists, one default address per type, purchase lists hidden from roles without prices.read.cost, per-row 422 on
 * the bulk item upsert, quote resolution customer list → dealer group list → default list (else `no_price`).
 * It is a simplified model, not the real validation. Tests inject single responses with `overrides` (one-shot,
 * keyed "METHOD /path"). All names, codes, numbers and prices are invented.
 */
import type { Route } from "@playwright/test";
import { fulfillJson, problemBody, type MockCatalog } from "./mock-catalog";

type Json = Record<string, unknown>;

export type MockCustomer = {
  id: string;
  code: string;
  name: string;
  contactName: string | null;
  taxNumber: string | null;
  taxOffice: string | null;
  phone: string | null;
  email: string | null;
  whatsappNumbers: string[];
  dealerGroupId: string | null;
  priceListId: string | null;
  paymentTermDays: number | null;
  creditLimit: number | string | null;
  currency: string | null;
  notes: string | null;
  status: string;
  anonymizedAt: string | null;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockAddress = {
  id: string;
  customerId: string;
  type: string;
  isDefault: boolean;
  title: string | null;
  line1: string;
  line2: string | null;
  district: string | null;
  city: string;
  postalCode: string | null;
  country: string;
  contactName: string | null;
  contactPhone: string | null;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockDealerGroup = {
  id: string;
  name: string;
  priceListId: string | null;
  defaultDiscountPercent: number | string | null;
  status: string;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockPriceList = {
  id: string;
  name: string;
  kind: string;
  currency: string;
  isDefault: boolean;
  pricesIncludeTax: boolean;
  validFrom: string | null;
  validTo: string | null;
  basePriceListId: string | null;
  discountPercent: number | string | null;
  status: string;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockPriceItem = {
  id: string;
  priceListId: string;
  variantId: string;
  minQuantity: number | string;
  unitPrice: number | string;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type CommerceOverride = (route: Route, body: Json | null) => Promise<unknown> | unknown;

export type MockCommerce = {
  customers: MockCustomer[];
  addresses: MockAddress[];
  groups: MockDealerGroup[];
  priceLists: MockPriceList[];
  items: MockPriceItem[];
  overrides: Map<string, CommerceOverride>;
  idempotency: Map<string, { status: number; body: unknown }>;
};

const NOW = "2026-11-01T09:00:00Z";
const uuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;

export const GROUP_IDS = { bayiA: uuid("7a", 1), perakende: uuid("7a", 2) };
export const PRICE_LIST_IDS = { perakende: uuid("7b", 1), bayiA: uuid("7b", 2), alis: uuid("7b", 3) };
export const customerId = (n: number) => uuid("7c", n);

export function makeCustomer(n: number, overrides: Partial<MockCustomer> = {}): MockCustomer {
  const nn = String(n).padStart(3, "0");
  return {
    id: customerId(n),
    code: `C-${nn}`,
    name: `Mock Müşteri ${nn}`,
    contactName: n === 1 ? "Mock Yetkili" : null,
    // Odd customers: 11-digit (TCKN-like, masked in lists); even: 10-digit VKN. Synthetic values.
    taxNumber: n % 2 === 1 ? `1000000${String(n).padStart(4, "0")}` : `900000${String(n).padStart(4, "0")}`,
    taxOffice: null,
    phone: `0212 555 ${String(n).padStart(4, "0")}`,
    email: null,
    whatsappNumbers: [],
    dealerGroupId: n === 1 ? GROUP_IDS.bayiA : null,
    priceListId: null,
    paymentTermDays: 30,
    creditLimit: null,
    currency: null,
    notes: null,
    status: "active",
    anonymizedAt: null,
    version: 1,
    createdAt: NOW,
    updatedAt: null,
    ...overrides,
  };
}

/**
 * Two dealer groups, three price lists (default "Perakende" TRY, "Bayi A" TRY assigned to group Bayi A, a purchase
 * list) and items for the first catalog variants: Perakende 100 + 10n, Bayi A 90 + 10n with a tier at 10 pieces.
 * Variant 3 has no price anywhere (→ no_price).
 */
export function createMockCommerce(catalog: MockCatalog | null, options: { customers?: number } = {}): MockCommerce {
  const variants = (catalog?.products ?? []).flatMap((p) => p.variants);
  const items: MockPriceItem[] = [];
  let n = 0;
  const item = (priceListId: string, variantId: string, minQuantity: number, unitPrice: number) =>
    items.push({ id: uuid("7d", ++n), priceListId, variantId, minQuantity, unitPrice, version: 1, createdAt: NOW, updatedAt: null });
  variants.slice(0, 2).forEach((v, i) => {
    item(PRICE_LIST_IDS.perakende, v.id, 1, 100 + 10 * (i + 1));
    item(PRICE_LIST_IDS.bayiA, v.id, 1, 90 + 10 * (i + 1));
    item(PRICE_LIST_IDS.bayiA, v.id, 10, 80 + 10 * (i + 1));
  });
  const customers = Array.from({ length: options.customers ?? 3 }, (_, i) => makeCustomer(i + 1));
  const addresses: MockAddress[] = customers.length
    ? [
        address(1, customers[0].id, "shipping", true, "Merkez (mock)", "Örnek Mah. Mock Cd. No:1"),
        address(2, customers[0].id, "shipping", false, "Depo (mock)", "Sanayi Sit. Mock Sk. No:2"),
      ]
    : [];
  return {
    customers,
    addresses,
    groups: [
      { id: GROUP_IDS.bayiA, name: "Bayi A (mock)", priceListId: PRICE_LIST_IDS.bayiA, defaultDiscountPercent: 5, status: "active", version: 1, createdAt: NOW, updatedAt: null },
      { id: GROUP_IDS.perakende, name: "Perakende Grup (mock)", priceListId: null, defaultDiscountPercent: null, status: "active", version: 1, createdAt: NOW, updatedAt: null },
    ],
    priceLists: [
      list(PRICE_LIST_IDS.perakende, "Perakende (mock)", { isDefault: true }),
      list(PRICE_LIST_IDS.bayiA, "Bayi A Listesi (mock)"),
      list(PRICE_LIST_IDS.alis, "Alış Listesi (mock)", { kind: "purchase" }),
    ],
    items,
    overrides: new Map(),
    idempotency: new Map(),
  };
}

function address(n: number, customerId: string, type: string, isDefault: boolean, title: string, line1: string): MockAddress {
  return {
    id: uuid("7e", n),
    customerId,
    type,
    isDefault,
    title,
    line1,
    line2: null,
    district: "Mock İlçe",
    city: "İstanbul",
    postalCode: null,
    country: "TR",
    contactName: null,
    contactPhone: null,
    version: 1,
    createdAt: new Date(Date.UTC(2026, 9, 1) + n * 60_000).toISOString(),
    updatedAt: null,
  };
}

function list(id: string, name: string, overrides: Partial<MockPriceList> = {}): MockPriceList {
  return {
    id,
    name,
    kind: "sales",
    currency: "TRY",
    isDefault: false,
    pricesIncludeTax: false,
    validFrom: null,
    validTo: null,
    basePriceListId: null,
    discountPercent: null,
    status: "active",
    version: 1,
    createdAt: NOW,
    updatedAt: null,
    ...overrides,
  };
}

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Idempotent-Replayed" };
const etag = (version: number) => ({ ETag: `W/"${version}"` });
const versionOf = (ifMatch: string | undefined) => (ifMatch ? Number(ifMatch.replace(/^W\//, "").replace(/"/g, "")) : NaN);
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, body, { ...HEADERS, ...headers });
const done = (p: Promise<unknown>) => p.then(() => true);
const DECIMAL = /^(0|[1-9]\d*)(\.\d+)?$/;
const decimals = (v: unknown) => (String(v).split(".")[1] ?? "").length;

function pageOf<T>(items: T[], params: URLSearchParams) {
  const limit = Number(params.get("limit") ?? 50);
  const offset = Number((params.get("cursor") ?? "o:0").slice(2));
  const hasMore = offset + limit < items.length;
  return { items: items.slice(offset, offset + limit), page: { nextCursor: hasMore ? `o:${offset + limit}` : null, hasMore, limit } };
}

const masked = (c: MockCustomer) => ({ ...c, taxNumber: c.taxNumber && c.taxNumber.length === 11 ? `*******${c.taxNumber.slice(-4)}` : c.taxNumber });

/** Handles a Customers/Pricing request; returns false when the path is not one of them. */
export async function handleCommerce(route: Route, mock: MockCommerce, catalog: MockCatalog | null, role: string): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  if (!/^\/api\/v1\/(customers|dealer-groups|price-lists|pricing)(\/|$)/.test(path)) return false;
  const body = (request.postDataJSON?.() ?? null) as Json | null;

  const override = mock.overrides.get(`${method} ${path}`);
  if (override) {
    mock.overrides.delete(`${method} ${path}`);
    await override(route, body);
    return true;
  }

  const ifMatch = request.headers()["if-match"];
  const key = request.headers()["idempotency-key"];
  const ownerAdmin = role === "owner" || role === "admin";
  const variants = (catalog?.products ?? []).flatMap((p) => p.variants);
  const now = () => new Date().toISOString();

  if ((method === "POST" || method === "PUT") && key) {
    const replay = mock.idempotency.get(`${method} ${path} ${key}`);
    if (replay) return done(json(route, replay.status, replay.body, { "Idempotent-Replayed": "true" }));
  }
  const succeed = (status: number, payload: unknown, headers: Record<string, string> = {}) => {
    if (key) mock.idempotency.set(`${method} ${path} ${key}`, { status, body: payload });
    return done(json(route, status, payload, headers));
  };
  const needsKey = (method === "POST" || method === "PUT") && path !== "/api/v1/pricing/quote" && !key;
  if (needsKey) return done(json(route, 428, problemBody(428, "precondition.required")));

  // ---- Customers ----
  if (path === "/api/v1/customers" && method === "GET") {
    const p = url.searchParams;
    const statuses = (p.get("status") ?? "active,inactive").split(",");
    const q = (p.get("q") ?? "").toLocaleLowerCase("tr");
    let items = mock.customers.filter((c) => statuses.includes(c.status));
    if (p.get("dealerGroupId")) items = items.filter((c) => c.dealerGroupId === p.get("dealerGroupId"));
    if (q) {
      const compact = q.replace(/\s+/g, "");
      items = items.filter(
        (c) => c.name.toLocaleLowerCase("tr").includes(q) || c.code.toLocaleLowerCase("tr").includes(q) || c.taxNumber === compact || c.phone === p.get("q"),
      );
    }
    items = [...items].sort((a, b) => a.name.localeCompare(b.name, "tr") || a.id.localeCompare(b.id));
    const page = pageOf(items, p);
    return done(json(route, 200, { items: page.items.map(masked), page: page.page }));
  }
  if (path === "/api/v1/customers" && method === "POST") {
    const code = String(body?.code ?? "").trim();
    const name = String(body?.name ?? "").trim();
    const errors: Record<string, string[]> = {};
    if (!code || /\s/.test(code)) errors.code = ["Required: 1–30 characters without whitespace."];
    if (!name) errors.name = ["Required: 1–200 characters."];
    if (Object.keys(errors).length > 0) return done(json(route, 422, problemBody(422, "validation.failed", { errors })));
    if ((body?.dealerGroupId || body?.priceListId) && !["owner", "admin", "manager"].includes(role)) return done(json(route, 403, problemBody(403, "auth.forbidden")));
    if (mock.customers.some((c) => c.code === code)) return done(json(route, 409, problemBody(409, "customers.code_taken")));
    const created = makeCustomer(100 + mock.customers.length, {
      ...(body as Partial<MockCustomer>),
      code,
      name,
      whatsappNumbers: (body?.whatsappNumbers as string[]) ?? [],
      createdAt: now(),
    });
    mock.customers.push(created);
    return succeed(201, { customer: created, addresses: [] }, etag(created.version));
  }
  const anonymizeMatch = path.match(/^\/api\/v1\/customers\/([^/]+)\/anonymize$/);
  if (anonymizeMatch && method === "POST") {
    const c = mock.customers.find((x) => x.id === anonymizeMatch[1]);
    if (!c) return done(json(route, 404, problemBody(404, "resource.not_found")));
    if (!ownerAdmin) return done(json(route, 403, problemBody(403, "auth.forbidden")));
    if (c.anonymizedAt) return done(json(route, 409, problemBody(409, "customers.anonymized")));
    Object.assign(c, {
      name: `Silinmiş müşteri #${c.id.slice(-6)}`,
      contactName: null,
      taxNumber: null,
      taxOffice: null,
      phone: null,
      email: null,
      whatsappNumbers: [],
      notes: null,
      status: "archived",
      anonymizedAt: now(),
      version: c.version + 1,
    });
    mock.addresses = mock.addresses.filter((a) => a.customerId !== c.id);
    return succeed(200, { customer: c, addresses: [] }, etag(c.version));
  }
  const addressMatch = path.match(/^\/api\/v1\/customers\/([^/]+)\/addresses(?:\/([^/]+))?$/);
  if (addressMatch) {
    const c = mock.customers.find((x) => x.id === addressMatch[1]);
    if (!c) return done(json(route, 404, problemBody(404, "resource.not_found")));
    const ofCustomer = () => mock.addresses.filter((a) => a.customerId === c.id);
    const clearDefault = (type: string) => {
      for (const a of ofCustomer()) if (a.type === type && a.isDefault) Object.assign(a, { isDefault: false, version: a.version + 1 });
    };
    if (!addressMatch[2] && method === "GET") return done(json(route, 200, { items: ofCustomer() }));
    if (c.status === "archived") return done(json(route, 409, problemBody(409, "customers.archived")));
    if (!addressMatch[2] && method === "POST") {
      const errors: Record<string, string[]> = {};
      if (!body?.line1) errors.line1 = ["Required: 1–200 characters."];
      if (!body?.city) errors.city = ["Required: 1–100 characters."];
      if (Object.keys(errors).length > 0) return done(json(route, 422, problemBody(422, "validation.failed", { errors })));
      const type = String(body?.type ?? "shipping");
      const first = !ofCustomer().some((a) => a.type === type);
      if (body?.isDefault === true && !first) clearDefault(type);
      const created: MockAddress = {
        ...address(100 + mock.addresses.length, c.id, type, first || body?.isDefault === true, (body?.title as string) ?? null, String(body?.line1)),
        line2: (body?.line2 as string) ?? null,
        district: (body?.district as string) ?? null,
        city: String(body?.city),
        country: String(body?.country ?? "TR"),
        createdAt: now(),
      };
      mock.addresses.push(created);
      return succeed(201, created, etag(created.version));
    }
    const a = mock.addresses.find((x) => x.id === addressMatch[2] && x.customerId === c.id);
    if (!a) return done(json(route, 404, problemBody(404, "resource.not_found")));
    if (method === "PATCH") {
      if (!ifMatch) return done(json(route, 428, problemBody(428, "precondition.required")));
      if (versionOf(ifMatch) !== a.version) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));
      const { isDefault, ...rest } = body ?? {};
      if (isDefault === false) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { isDefault: ["Only true is accepted: make another address the default instead."] } })));
      if (isDefault === true && !a.isDefault) {
        clearDefault(a.type);
        a.isDefault = true;
      }
      Object.assign(a, rest, { version: a.version + 1, updatedAt: now() });
      return done(json(route, 200, a, etag(a.version)));
    }
    if (method === "DELETE") {
      mock.addresses = mock.addresses.filter((x) => x.id !== a.id);
      if (a.isDefault) {
        const next = ofCustomer()
          .filter((x) => x.type === a.type)
          .sort((x, y) => x.createdAt.localeCompare(y.createdAt))[0];
        if (next) Object.assign(next, { isDefault: true, version: next.version + 1 });
      }
      await route.fulfill({ status: 204, headers: HEADERS });
      return true;
    }
  }
  const customerMatch = path.match(/^\/api\/v1\/customers\/([^/]+)$/);
  if (customerMatch) {
    const c = mock.customers.find((x) => x.id === customerMatch[1]);
    if (!c) return done(json(route, 404, problemBody(404, "resource.not_found")));
    if (method === "GET") return done(json(route, 200, { customer: c, addresses: mock.addresses.filter((a) => a.customerId === c.id) }, etag(c.version)));
    if (method === "PATCH") {
      if (!ifMatch) return done(json(route, 428, problemBody(428, "precondition.required")));
      if (versionOf(ifMatch) !== c.version) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));
      if (c.anonymizedAt) return done(json(route, 409, problemBody(409, "customers.anonymized")));
      if (body && typeof body.taxNumber === "string" && !/^\d{10,11}$/.test(body.taxNumber)) {
        return done(json(route, 422, problemBody(422, "validation.failed", { errors: { taxNumber: ["Must be 10 (VKN) or 11 (TCKN) digits."] } })));
      }
      if (body && ("dealerGroupId" in body || "priceListId" in body) && !["owner", "admin", "manager"].includes(role)) {
        return done(json(route, 403, problemBody(403, "auth.forbidden")));
      }
      Object.assign(c, body, { version: c.version + 1, updatedAt: now() });
      return done(json(route, 200, { customer: c, addresses: mock.addresses.filter((a) => a.customerId === c.id) }, etag(c.version)));
    }
  }

  // ---- Dealer groups ----
  if (path === "/api/v1/dealer-groups" && method === "GET") {
    const all = url.searchParams.get("includeArchived") === "true";
    const items = mock.groups.filter((g) => all || g.status !== "archived").sort((a, b) => a.name.localeCompare(b.name, "tr"));
    return done(json(route, 200, { items }));
  }
  if (path === "/api/v1/dealer-groups" && method === "POST") {
    const name = String(body?.name ?? "").trim();
    if (!name) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { name: ["Must be 1–100 characters."] } })));
    if (mock.groups.some((g) => g.status !== "archived" && g.name.toLocaleLowerCase("tr") === name.toLocaleLowerCase("tr"))) {
      return done(json(route, 409, problemBody(409, "customers.group_name_taken")));
    }
    const created: MockDealerGroup = {
      id: uuid("7a", 100 + mock.groups.length),
      name,
      priceListId: (body?.priceListId as string) ?? null,
      defaultDiscountPercent: (body?.defaultDiscountPercent as string) ?? null,
      status: "active",
      version: 1,
      createdAt: now(),
      updatedAt: null,
    };
    mock.groups.push(created);
    return succeed(201, created, etag(created.version));
  }
  const groupMatch = path.match(/^\/api\/v1\/dealer-groups\/([^/]+)$/);
  if (groupMatch) {
    const g = mock.groups.find((x) => x.id === groupMatch[1]);
    if (!g) return done(json(route, 404, problemBody(404, "resource.not_found")));
    if (method === "GET") return done(json(route, 200, g, etag(g.version)));
    if (method === "PATCH") {
      if (versionOf(ifMatch) !== g.version) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));
      Object.assign(g, body, { version: g.version + 1, updatedAt: now() });
      return done(json(route, 200, g, etag(g.version)));
    }
  }

  // ---- Price lists ----
  const visible = (l: MockPriceList) => l.kind === "sales" || ownerAdmin;
  if (path === "/api/v1/price-lists" && method === "GET") {
    const all = url.searchParams.get("includeArchived") === "true";
    const items = mock.priceLists
      .filter((l) => visible(l) && (all || l.status !== "archived"))
      .sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.name.localeCompare(b.name, "tr"));
    return done(json(route, 200, { items }));
  }
  if (path === "/api/v1/price-lists" && method === "POST") {
    if (!ownerAdmin) return done(json(route, 403, problemBody(403, "auth.forbidden")));
    const name = String(body?.name ?? "").trim();
    if (!name) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { name: ["Must be 1–100 characters."] } })));
    if (mock.priceLists.some((l) => l.status !== "archived" && l.name === name)) return done(json(route, 409, problemBody(409, "pricing.name_taken")));
    const kind = String(body?.kind ?? "sales");
    const currency = String(body?.currency ?? "TRY");
    const firstOfCurrency = kind === "sales" && !mock.priceLists.some((l) => l.kind === "sales" && l.currency === currency && l.status !== "archived");
    const makeDefault = kind === "sales" && (firstOfCurrency || body?.isDefault === true);
    if (makeDefault) for (const l of mock.priceLists) if (l.currency === currency && l.kind === "sales") l.isDefault = false;
    const created = list(uuid("7b", 100 + mock.priceLists.length), name, {
      kind,
      currency,
      isDefault: makeDefault,
      pricesIncludeTax: body?.pricesIncludeTax === true,
      validFrom: (body?.validFrom as string) ?? null,
      validTo: (body?.validTo as string) ?? null,
      basePriceListId: (body?.basePriceListId as string) ?? null,
      discountPercent: (body?.discountPercent as string) ?? null,
      createdAt: now(),
    });
    mock.priceLists.push(created);
    return succeed(201, created, etag(created.version));
  }
  const itemsMatch = path.match(/^\/api\/v1\/price-lists\/([^/]+)\/items(?:\/([^/]+))?$/);
  if (itemsMatch) {
    const l = mock.priceLists.find((x) => x.id === itemsMatch[1] && visible(x));
    if (!l) return done(json(route, 404, problemBody(404, "resource.not_found")));
    if (!itemsMatch[2] && method === "GET") {
      const variantId = url.searchParams.get("variantId");
      const rows = mock.items.filter((i) => i.priceListId === l.id && (!variantId || i.variantId === variantId));
      return done(json(route, 200, pageOf(rows, url.searchParams)));
    }
    if (!ownerAdmin) return done(json(route, 403, problemBody(403, "auth.forbidden")));
    if (l.status === "archived") return done(json(route, 409, problemBody(409, "pricing.list_archived")));
    if (!itemsMatch[2] && method === "PUT") {
      const input = (body?.items as Json[]) ?? [];
      const errors: Record<string, string[]> = {};
      if (input.length === 0 || input.length > 500) errors.items = ["Must contain 1–500 items."];
      const seen = new Set<string>();
      input.forEach((row, i) => {
        if (!variants.some((v) => v.id === row.variantId)) errors[`items[${i}].variantId`] = ["Variant not found."];
        const q = String(row.minQuantity ?? "");
        if (!DECIMAL.test(q) || Number(q) <= 0 || decimals(q) > 3) errors[`items[${i}].minQuantity`] = ["Must be greater than 0 with at most 3 decimals."];
        const p = String(row.unitPrice ?? "");
        if (!DECIMAL.test(p) || decimals(p) > 4) errors[`items[${i}].unitPrice`] = ["Required: 0 or more with at most 4 decimals."];
        const k = `${row.variantId}|${Number(q)}`;
        if (seen.has(k)) errors[`items[${i}]`] = ["Duplicate variantId + minQuantity in this request."];
        seen.add(k);
      });
      if (Object.keys(errors).length > 0) return done(json(route, 422, problemBody(422, "validation.failed", { errors })));
      let created = 0;
      let updated = 0;
      const result: MockPriceItem[] = [];
      for (const row of input) {
        const existing = mock.items.find((i) => i.priceListId === l.id && i.variantId === row.variantId && Number(i.minQuantity) === Number(row.minQuantity));
        if (existing) {
          Object.assign(existing, { unitPrice: row.unitPrice as string, version: existing.version + 1, updatedAt: now() });
          updated += 1;
          result.push(existing);
        } else {
          const createdItem: MockPriceItem = {
            id: uuid("7d", 500 + mock.items.length),
            priceListId: l.id,
            variantId: String(row.variantId),
            minQuantity: row.minQuantity as string,
            unitPrice: row.unitPrice as string,
            version: 1,
            createdAt: now(),
            updatedAt: null,
          };
          mock.items.push(createdItem);
          created += 1;
          result.push(createdItem);
        }
      }
      return succeed(200, { created, updated, items: result });
    }
    if (itemsMatch[2] && method === "DELETE") {
      mock.items = mock.items.filter((i) => i.id !== itemsMatch[2]);
      await route.fulfill({ status: 204, headers: HEADERS });
      return true;
    }
  }
  const listMatch = path.match(/^\/api\/v1\/price-lists\/([^/]+)$/);
  if (listMatch) {
    const l = mock.priceLists.find((x) => x.id === listMatch[1] && visible(x));
    if (!l) return done(json(route, 404, problemBody(404, "resource.not_found")));
    if (method === "GET") return done(json(route, 200, l, etag(l.version)));
    if (method === "PATCH") {
      if (!ownerAdmin) return done(json(route, 403, problemBody(403, "auth.forbidden")));
      if (!ifMatch) return done(json(route, 428, problemBody(428, "precondition.required")));
      if (versionOf(ifMatch) !== l.version) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));
      if (l.status === "archived") return done(json(route, 409, problemBody(409, "pricing.list_archived")));
      const { isDefault, ...rest } = body ?? {};
      if (l.isDefault && rest.status && rest.status !== "active") return done(json(route, 409, problemBody(409, "pricing.default_inactive")));
      if (isDefault === true) for (const x of mock.priceLists) if (x.currency === l.currency && x.kind === "sales") x.isDefault = false;
      Object.assign(l, rest, isDefault === true ? { isDefault: true } : {}, { version: l.version + 1, updatedAt: now() });
      return done(json(route, 200, l, etag(l.version)));
    }
  }

  // ---- Quote ----
  if (path === "/api/v1/pricing/quote" && method === "POST") {
    const lines = (body?.lines as Json[]) ?? [];
    const errors: Record<string, string[]> = {};
    if (lines.length === 0) errors.lines = ["Must contain 1–500 lines."];
    const customer = body?.customerId ? mock.customers.find((c) => c.id === body.customerId) : null;
    if (body?.customerId && !customer) errors.customerId = ["Customer not found."];
    lines.forEach((line, i) => {
      if (!variants.some((v) => v.id === line.variantId)) errors[`lines[${i}].variantId`] = ["Variant not found."];
    });
    if (Object.keys(errors).length > 0) return done(json(route, 422, problemBody(422, "validation.failed", { errors })));
    const currency = String(body?.currency ?? customer?.currency ?? "TRY");
    const group = customer?.dealerGroupId ? mock.groups.find((g) => g.id === customer.dealerGroupId && g.status !== "archived") : undefined;
    const usable = (id: string | null | undefined) => mock.priceLists.find((l) => l.id === id && l.status === "active" && l.kind === "sales" && l.currency === currency);
    const chain: [MockPriceList | undefined, string][] = [
      [usable(customer?.priceListId), "customer_price_list"],
      [usable(group?.priceListId), "dealer_group_price_list"],
      [mock.priceLists.find((l) => l.isDefault && l.status === "active" && l.currency === currency), "default_price_list"],
    ];
    const resolved = lines.map((line) => {
      const variant = variants.find((v) => v.id === line.variantId)!;
      const qty = Number(line.quantity);
      const tierOf = (listId: string) =>
        mock.items
          .filter((i) => i.priceListId === listId && i.variantId === variant.id && Number(i.minQuantity) <= qty)
          .sort((a, b) => Number(b.minQuantity) - Number(a.minQuantity))[0];
      for (const [l, source] of chain) {
        if (!l) continue;
        const hit = tierOf(l.id);
        const base = !hit && l.basePriceListId ? tierOf(l.basePriceListId) : undefined;
        if (!hit && !base) continue;
        const unitPrice = hit ? hit.unitPrice : Math.round(Number(base!.unitPrice) * (100 - Number(l.discountPercent)) * 100) / 10000;
        return {
          variantId: variant.id,
          sku: variant.sku,
          quantity: line.quantity,
          status: "priced",
          unitPrice,
          source,
          rule: hit ? "item" : "base_discount",
          priceListId: l.id,
          priceListName: l.name,
          priceListItemId: (hit ?? base)!.id,
          itemPriceListId: (hit ?? base)!.priceListId,
          minQuantity: (hit ?? base)!.minQuantity,
          baseUnitPrice: hit ? null : base!.unitPrice,
          discountPercent: hit ? null : l.discountPercent,
          pricesIncludeTax: l.pricesIncludeTax,
          taxRate: 20,
        };
      }
      return {
        variantId: variant.id,
        sku: variant.sku,
        quantity: line.quantity,
        status: "no_price",
        unitPrice: null,
        source: null,
        rule: null,
        priceListId: null,
        priceListName: null,
        priceListItemId: null,
        itemPriceListId: null,
        minQuantity: null,
        baseUnitPrice: null,
        discountPercent: null,
        pricesIncludeTax: null,
        taxRate: 20,
      };
    });
    return done(
      json(route, 200, {
        customerId: customer?.id ?? null,
        currency,
        date: (body?.date as string) ?? "2026-11-05",
        dealerGroupDiscountPercent: group?.defaultDiscountPercent ?? null,
        lines: resolved,
      }),
    );
  }
  return false;
}

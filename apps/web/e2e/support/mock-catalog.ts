/**
 * MOCK CATALOG API FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API.
 *
 * A small stand-in for the Catalog endpoints (products, variants, brands, units, categories, custom fields)
 * so the product screens can be exercised without Supabase or the .NET API. It mimics the contract shapes in
 * packages/contracts/openapi.json (keyset-style `page.nextCursor`, ETag `W/"<version>"`, If-Match → 412,
 * Problem Details), not the real validation rules. Tests override single responses with `overrides`.
 * All names, SKUs and barcodes are invented.
 */
import type { Route } from "@playwright/test";

type Json = Record<string, unknown>;

export type MockField = {
  id: string;
  entityType: "product" | "variant";
  key: string;
  label: string;
  dataType: string;
  unit: string | null;
  options: string[];
  required: boolean;
  filterable: boolean;
  sortOrder: number;
  isArchived: boolean;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockVariant = {
  id: string;
  productId: string;
  sku: string;
  barcode: string | null;
  name: string | null;
  optionValues: Record<string, string>;
  unitId: string;
  status: string;
  attributes: Json;
  version: number;
  createdAt: string;
  createdBy: null;
  updatedAt: string | null;
  updatedBy: null;
};

export type MockProduct = {
  id: string;
  name: string;
  description: string | null;
  categoryId: string | null;
  brandId: string | null;
  defaultUnitId: string;
  taxRate: number;
  status: string;
  attributes: Json;
  variants: MockVariant[];
  version: number;
  createdAt: string;
  createdBy: null;
  updatedAt: string | null;
  updatedBy: null;
};

export type CatalogOverride = (route: Route, body: Json | null) => Promise<unknown> | unknown;

export type MockCatalog = {
  units: Json[];
  brands: Json[];
  categories: Json[];
  fields: MockField[];
  products: MockProduct[];
  /** One-shot overrides, keyed "METHOD /path" (e.g. "POST /api/v1/products"). Removed after use. */
  overrides: Map<string, CatalogOverride>;
};

const NOW = "2026-11-01T09:00:00Z";
const uuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;

export const UNIT_ADET = { id: uuid("a", 1), code: "ADET", name: "Adet", allowsFraction: false, isArchived: false, version: 1, createdAt: NOW, updatedAt: null };
export const BRAND_IDS = { sim: uuid("b", 1), parlak: uuid("b", 2) };
export const CATEGORY_IDS = { aydinlatma: uuid("c", 1), panel: uuid("c", 2), motor: uuid("c", 3) };

function mockField(n: number, f: Partial<MockField> & Pick<MockField, "key" | "label" | "dataType">): MockField {
  return {
    id: uuid("f", n),
    entityType: "product",
    unit: null,
    options: [],
    required: false,
    filterable: false,
    sortOrder: n,
    isArchived: false,
    version: 1,
    createdAt: NOW,
    updatedAt: null,
    ...f,
  };
}

/** Custom fields covering every data type (synthetic LED/motor-parts examples). */
export function defaultFields(): MockField[] {
  return [
    mockField(1, { key: "guc", label: "Güç", dataType: "number", unit: "W", required: true, filterable: true }),
    mockField(2, { key: "renk_sicakligi", label: "Renk sıcaklığı", dataType: "range", unit: "K", filterable: true }),
    mockField(3, { key: "ip_sinifi", label: "IP sınıfı", dataType: "enum", options: ["IP20", "IP44", "IP65"], filterable: true }),
    mockField(4, { key: "uyumlu_model", label: "Uyumlu model", dataType: "multi_enum", options: ["CG125", "CB150", "NMAX"] }),
    mockField(5, { key: "dis_mekan", label: "Dış mekân", dataType: "boolean" }),
    mockField(6, { key: "garanti_ay", label: "Garanti (ay)", dataType: "integer" }),
    mockField(7, { key: "uretim_tarihi", label: "Üretim tarihi", dataType: "date" }),
    mockField(8, { key: "not", label: "Not", dataType: "text" }),
    mockField(9, { key: "renk", label: "Renk", dataType: "enum", entityType: "variant", options: ["Sarı", "Beyaz"], filterable: true }),
  ];
}

/** Id of the n-th synthetic product made by makeProduct/createMockCatalog. */
export const productId = (n: number) => uuid("d", n);

export function makeProduct(n: number, overrides: Partial<MockProduct> = {}): MockProduct {
  const id = uuid("d", n);
  const product: MockProduct = {
    id,
    name: `Örnek Ürün ${String(n).padStart(3, "0")}`,
    description: null,
    categoryId: n % 2 === 0 ? CATEGORY_IDS.panel : CATEGORY_IDS.motor,
    brandId: n % 3 === 0 ? BRAND_IDS.parlak : BRAND_IDS.sim,
    defaultUnitId: UNIT_ADET.id,
    taxRate: 20,
    status: "active",
    attributes: { guc: n % 2 === 0 ? 18 : 9 },
    variants: [],
    version: 1,
    createdAt: new Date(Date.UTC(2026, 9, 1) + n * 60_000).toISOString(),
    createdBy: null,
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  };
  if (product.variants.length === 0) {
    product.variants = [makeVariant(product.id, n, 1)];
  }
  return product;
}

export function makeVariant(productId: string, n: number, i: number, overrides: Partial<MockVariant> = {}): MockVariant {
  return {
    id: uuid("e", n * 10 + i),
    productId,
    sku: `MOCK-${String(n).padStart(3, "0")}-${i}`,
    barcode: `2000000${String(n * 10 + i).padStart(6, "0")}`,
    name: null,
    optionValues: {},
    unitId: UNIT_ADET.id,
    status: "active",
    attributes: {},
    version: 1,
    createdAt: NOW,
    createdBy: null,
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  };
}

export function createMockCatalog(options: { products?: number | MockProduct[]; fields?: MockField[] } = {}): MockCatalog {
  const products =
    typeof options.products === "number"
      ? Array.from({ length: options.products }, (_, i) => makeProduct(i + 1))
      : (options.products ?? []);
  return {
    units: [UNIT_ADET],
    brands: [
      { id: BRAND_IDS.sim, name: "Simya (mock)", isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
      { id: BRAND_IDS.parlak, name: "Parlak (mock)", isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
    ],
    categories: [
      { id: CATEGORY_IDS.aydinlatma, parentId: null, name: "Aydınlatma", path: `/${CATEGORY_IDS.aydinlatma}/`, depth: 1, sortOrder: 0, isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
      { id: CATEGORY_IDS.panel, parentId: CATEGORY_IDS.aydinlatma, name: "Panel", path: `/${CATEGORY_IDS.aydinlatma}/${CATEGORY_IDS.panel}/`, depth: 2, sortOrder: 0, isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
      { id: CATEGORY_IDS.motor, parentId: null, name: "Motor", path: `/${CATEGORY_IDS.motor}/`, depth: 1, sortOrder: 1, isArchived: false, version: 1, createdAt: NOW, updatedAt: null },
    ],
    fields: options.fields ?? defaultFields(),
    products,
    overrides: new Map(),
  };
}

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag" };

export function fulfillJson(route: Route, status: number, body: unknown, headers: Record<string, string> = {}) {
  return route.fulfill({
    status,
    contentType: status >= 400 ? "application/problem+json" : "application/json",
    headers: { ...HEADERS, ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export const problemBody = (status: number, code: string, extra: Json = {}) => ({
  type: `https://bayios.app/problems/${code}`,
  title: code,
  status,
  code,
  ...extra,
});

const etag = (version: number) => ({ ETag: `W/"${version}"` });
const versionOf = (ifMatch: string | undefined) => (ifMatch ? Number(ifMatch.replace(/^W\//, "").replace(/"/g, "")) : NaN);

function categoryAndDescendants(catalog: MockCatalog, id: string): Set<string> {
  const ids = new Set([id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const c of catalog.categories) {
      if (c.parentId && ids.has(c.parentId as string) && !ids.has(c.id as string)) {
        ids.add(c.id as string);
        grew = true;
      }
    }
  }
  return ids;
}

function matchesAttribute(catalog: MockCatalog, p: MockProduct, param: string, value: string): boolean {
  let key = param.slice(5);
  let op = "eq";
  if (/\.(min|max)$/.test(key)) {
    op = key.slice(-3);
    key = key.slice(0, -4);
  }
  const def = catalog.fields.find((f) => f.key === key);
  if (!def) return false;
  const sources = def.entityType === "variant" ? p.variants.filter((v) => v.status !== "archived").map((v) => v.attributes) : [p.attributes];
  return sources.some((attrs) => {
    const stored = attrs[key];
    if (stored === undefined) return false;
    if (op === "min") return Number(stored) >= Number(value);
    if (op === "max") return Number(stored) <= Number(value);
    if (def.dataType === "multi_enum") return Array.isArray(stored) && stored.includes(value);
    if (def.dataType === "range") {
      const r = stored as { min: number; max: number };
      return r.min <= Number(value) && r.max >= Number(value);
    }
    return String(stored) === value;
  });
}

function listProducts(catalog: MockCatalog, params: URLSearchParams) {
  const statuses = (params.get("status") ?? "active,inactive").split(",");
  const q = (params.get("q") ?? "").toLocaleLowerCase("tr");
  const categoryId = params.get("categoryId");
  const brandId = params.get("brandId");
  const cats = categoryId ? categoryAndDescendants(catalog, categoryId) : null;
  let items = catalog.products.filter((p) => statuses.includes(p.status));
  if (q) {
    items = items.filter(
      (p) =>
        p.name.toLocaleLowerCase("tr").includes(q) ||
        p.variants.some((v) => v.sku.toLocaleLowerCase("tr").includes(q) || v.barcode === params.get("q")),
    );
  }
  if (cats) items = items.filter((p) => p.categoryId && cats.has(p.categoryId));
  if (brandId) items = items.filter((p) => p.brandId === brandId);
  params.forEach((value, key) => {
    if (key.startsWith("attr.")) items = items.filter((p) => matchesAttribute(catalog, p, key, value));
  });
  items =
    params.get("sort") === "name"
      ? [...items].sort((a, b) => a.name.localeCompare(b.name, "tr"))
      : [...items].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const limit = Number(params.get("limit") ?? 50);
  const offset = Number((params.get("cursor") ?? "o:0").slice(2));
  const page = items.slice(offset, offset + limit);
  const hasMore = offset + limit < items.length;
  return { items: page, page: { nextCursor: hasMore ? `o:${offset + limit}` : null, hasMore, limit } };
}

function mergeAttributes(current: Json, patch: Json | undefined): Json {
  const next = { ...current };
  for (const [k, v] of Object.entries(patch ?? {})) {
    if (v === null) delete next[k];
    else next[k] = v;
  }
  return next;
}

type ReferenceKind = "units" | "brands" | "categories";
const lower = (v: unknown) => String(v ?? "").trim().toLocaleLowerCase("tr");
const cleanText = (v: unknown) => (typeof v === "string" ? v.trim().replace(/\s+/g, " ") : "");
let referenceCounter = 0;

/**
 * Units / brands / categories (#129): GET (includeArchived, brand `q`), POST (Idempotency-Key required + replayed,
 * 409 brand_name_taken / unit_code_taken / category_name_taken, 422 field errors in the API's English shapes),
 * PATCH (If-Match → 412, merge patch) and DELETE = archive (If-Match; 409 category_has_children). Roles are not checked
 * here: the screens gate on the MOCK /me permissions and the API's own tests cover the 403s.
 */
async function handleReference(
  route: Route,
  catalog: MockCatalog,
  r: { path: string; method: string; body: Json | null; ifMatch: string | undefined; params: URLSearchParams },
): Promise<boolean> {
  const match = r.path.match(/^\/api\/v1\/(units|brands|categories)(?:\/([^/]+))?$/);
  if (!match) return false;
  const kind = match[1] as ReferenceKind;
  const list = catalog[kind];
  const id = match[2];
  const reply = (status: number, payload: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, payload, headers).then(() => true);
  const invalid = (errors: Record<string, string[]>) => reply(422, problemBody(422, "validation.failed", { errors }));
  const active = (x: Json) => x.isArchived !== true;

  const nameProblem = (field: string, value: unknown, max: number) => {
    const name = cleanText(value);
    return !name || name.length > max ? { [field]: [`Must be 1–${max} characters.`] } : null;
  };
  const conflict = (candidate: Json, self: string | null): string | null => {
    if (kind === "brands" && list.some((x) => x.id !== self && active(x) && lower(x.name) === lower(candidate.name))) return "catalog.brand_name_taken";
    if (kind === "units" && list.some((x) => x.id !== self && lower(x.code) === lower(candidate.code))) return "catalog.unit_code_taken";
    if (
      kind === "categories" &&
      list.some((x) => x.id !== self && active(x) && (x.parentId ?? null) === (candidate.parentId ?? null) && lower(x.name) === lower(candidate.name))
    ) {
      return "catalog.category_name_taken";
    }
    return null;
  };

  if (!id && r.method === "GET") {
    const all = r.params.get("includeArchived") === "true";
    const q = lower(r.params.get("q"));
    const items = list.filter((x) => (all || active(x)) && (!q || lower(x.name).includes(q)));
    return reply(200, { items });
  }

  if (!id && r.method === "POST") {
    const key = route.request().headers()["idempotency-key"];
    if (!key) return reply(428, problemBody(428, "precondition.required"));
    const replayKey = `${kind}:${key}`;
    const replayed = referenceReplays.get(replayKey);
    if (replayed) return reply(201, replayed, { ...etag(Number(replayed.version)), "Idempotent-Replayed": "true" });
    const b = r.body ?? {};
    const now = new Date().toISOString();
    let created: Json;
    if (kind === "brands") {
      const problem = nameProblem("name", b.name, 100);
      if (problem) return invalid(problem);
      created = { id: uuid("b", 500 + ++referenceCounter), name: cleanText(b.name), isArchived: false, version: 1, createdAt: now, updatedAt: null };
    } else if (kind === "units") {
      const code = String(b.code ?? "").trim().toLowerCase();
      const errors: Record<string, string[]> = {};
      if (!code || /\s/.test(code) || code.length > 20) errors.code = ["Code must be 1–20 characters without whitespace."];
      Object.assign(errors, nameProblem("name", b.name, 50) ?? {});
      if (Object.keys(errors).length) return invalid(errors);
      created = { id: uuid("a", 500 + ++referenceCounter), code, name: cleanText(b.name), allowsFraction: b.allowsFraction === true, isArchived: false, version: 1, createdAt: now, updatedAt: null };
    } else {
      const problem = nameProblem("name", b.name, 100);
      if (problem) return invalid(problem);
      const parentId = (b.parentId as string | null) ?? null;
      const parent = parentId ? list.find((x) => x.id === parentId && active(x)) : null;
      if (parentId && !parent) return invalid({ parentId: ["Parent category not found."] });
      const depth = parent ? Number(parent.depth) + 1 : 1;
      if (depth > 4) return invalid({ parentId: ["Category tree depth is limited to 4."] });
      const newId = uuid("c", 500 + ++referenceCounter);
      created = {
        id: newId,
        parentId,
        name: cleanText(b.name),
        path: `${parent ? String(parent.path) : "/"}${newId}/`,
        depth,
        sortOrder: Number(b.sortOrder ?? 0),
        isArchived: false,
        version: 1,
        createdAt: now,
        updatedAt: null,
      };
    }
    const taken = conflict(created, null);
    if (taken) return reply(409, problemBody(409, taken));
    list.push(created);
    referenceReplays.set(replayKey, created);
    return reply(201, created, etag(1));
  }

  if (!id) return false;
  const item = list.find((x) => x.id === id);
  if (!item) return reply(404, problemBody(404, "resource.not_found"));
  if (!r.ifMatch) return reply(428, problemBody(428, "precondition.required"));
  if (versionOf(r.ifMatch) !== Number(item.version)) return reply(412, problemBody(412, "concurrency.version_mismatch"));

  if (r.method === "PATCH") {
    const b = r.body ?? {};
    const max = kind === "units" ? 50 : 100;
    if (b.name !== undefined && b.name !== null) {
      const problem = nameProblem("name", b.name, max);
      if (problem) return invalid(problem);
    }
    const next: Json = { ...item, ...(b.name ? { name: cleanText(b.name) } : {}) };
    if (kind === "units" && typeof b.allowsFraction === "boolean") next.allowsFraction = b.allowsFraction;
    if (kind === "categories" && b.sortOrder !== undefined && b.sortOrder !== null) next.sortOrder = Number(b.sortOrder);
    const taken = conflict(next, id);
    if (taken) return reply(409, problemBody(409, taken));
    Object.assign(item, next, { version: Number(item.version) + 1, updatedAt: new Date().toISOString() });
    return reply(200, item, etag(Number(item.version)));
  }
  if (r.method === "DELETE") {
    if (kind === "categories" && list.some((x) => x.parentId === id && active(x))) {
      return reply(409, problemBody(409, "catalog.category_has_children"));
    }
    Object.assign(item, { isArchived: true, version: Number(item.version) + 1, updatedAt: new Date().toISOString() });
    await route.fulfill({ status: 204, headers: HEADERS });
    return true;
  }
  return false;
}

const referenceReplays = new Map<string, Json>();

/** Handles a Catalog request; returns false when the path is not a Catalog endpoint. */
export async function handleCatalog(route: Route, catalog: MockCatalog): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  const body = (request.postDataJSON?.() ?? null) as Json | null;

  const overrideKey = `${method} ${path}`;
  const override = catalog.overrides.get(overrideKey);
  if (override) {
    catalog.overrides.delete(overrideKey);
    await override(route, body);
    return true;
  }

  const ifMatch = request.headers()["if-match"];
  if (await handleReference(route, catalog, { path, method, body, ifMatch, params: url.searchParams })) return true;

  if (path === "/api/v1/custom-fields") {
    if (method === "GET") {
      const all = url.searchParams.get("includeArchived") === "true";
      await fulfillJson(route, 200, { items: catalog.fields.filter((f) => all || !f.isArchived) });
      return true;
    }
    if (method === "POST") {
      const created: MockField = mockField(catalog.fields.length + 100, body as unknown as MockField);
      created.id = uuid("f", catalog.fields.length + 100);
      catalog.fields.push(created);
      await fulfillJson(route, 201, created, etag(created.version));
      return true;
    }
  }
  const fieldMatch = path.match(/^\/api\/v1\/custom-fields\/([^/]+)$/);
  if (fieldMatch) {
    const f = catalog.fields.find((x) => x.id === fieldMatch[1]);
    if (!f) return fulfillJson(route, 404, problemBody(404, "resource.not_found")).then(() => true);
    if (versionOf(ifMatch) !== f.version) return fulfillJson(route, 412, problemBody(412, "concurrency.version_mismatch")).then(() => true);
    if (method === "PATCH") Object.assign(f, body, { version: f.version + 1 });
    if (method === "DELETE") Object.assign(f, { isArchived: true, version: f.version + 1 });
    await (method === "DELETE" ? route.fulfill({ status: 204, headers: HEADERS }) : fulfillJson(route, 200, f, etag(f.version)));
    return true;
  }

  if (path === "/api/v1/products" && method === "GET") {
    await fulfillJson(route, 200, listProducts(catalog, url.searchParams));
    return true;
  }
  if (path === "/api/v1/products" && method === "POST") {
    const n = catalog.products.length + 500;
    const input = body ?? {};
    const product = makeProduct(n, {
      name: String(input.name),
      description: (input.description as string) ?? null,
      categoryId: (input.categoryId as string) ?? null,
      brandId: (input.brandId as string) ?? null,
      defaultUnitId: String(input.defaultUnitId),
      taxRate: Number(input.taxRate),
      status: String(input.status ?? "active"),
      attributes: (input.attributes as Json) ?? {},
      createdAt: new Date().toISOString(),
    });
    product.variants = ((input.variants as Json[]) ?? []).map((v, i) =>
      makeVariant(product.id, n, i + 1, {
        sku: String(v.sku),
        barcode: (v.barcode as string) ?? null,
        name: (v.name as string) ?? null,
        status: String(v.status ?? "active"),
        attributes: (v.attributes as Json) ?? {},
      }),
    );
    catalog.products.push(product);
    await fulfillJson(route, 201, product, etag(product.version));
    return true;
  }

  const productMatch = path.match(/^\/api\/v1\/products\/([^/]+)(\/variants)?$/);
  if (productMatch) {
    const product = catalog.products.find((p) => p.id === productMatch[1]);
    if (!product) return fulfillJson(route, 404, problemBody(404, "resource.not_found")).then(() => true);
    if (productMatch[2] && method === "POST") {
      const v = body ?? {};
      const variant = makeVariant(product.id, catalog.products.indexOf(product) + 1, product.variants.length + 1, {
        sku: String(v.sku),
        barcode: (v.barcode as string) ?? null,
        name: (v.name as string) ?? null,
        attributes: (v.attributes as Json) ?? {},
      });
      product.variants.push(variant);
      await fulfillJson(route, 201, variant, etag(variant.version));
      return true;
    }
    if (method === "GET") return fulfillJson(route, 200, product, etag(product.version)).then(() => true);
    if (versionOf(ifMatch) !== product.version) {
      await fulfillJson(route, 412, problemBody(412, "concurrency.version_mismatch"));
      return true;
    }
    if (method === "PATCH") {
      const { attributes, ...rest } = body ?? {};
      Object.assign(product, rest, { attributes: mergeAttributes(product.attributes, attributes as Json), version: product.version + 1, updatedAt: new Date().toISOString() });
      await fulfillJson(route, 200, product, etag(product.version));
      return true;
    }
    if (method === "DELETE") {
      Object.assign(product, { status: "archived", version: product.version + 1 });
      await route.fulfill({ status: 204, headers: HEADERS });
      return true;
    }
  }

  // Variant reads (scanner lookup, search, by id) — used by the stock screens.
  const allVariants = () => catalog.products.flatMap((p) => p.variants);
  if (method === "GET" && path === "/api/v1/variants/lookup") {
    const barcode = url.searchParams.get("barcode");
    const found = allVariants().find((v) => v.barcode === barcode && v.status !== "archived");
    await (found ? fulfillJson(route, 200, found, etag(found.version)) : fulfillJson(route, 404, problemBody(404, "resource.not_found")));
    return true;
  }
  if (method === "GET" && path === "/api/v1/variants") {
    const q = (url.searchParams.get("q") ?? "").toLocaleLowerCase("tr");
    const sku = url.searchParams.get("sku");
    const barcode = url.searchParams.get("barcode");
    let items = allVariants().filter((v) => v.status !== "archived");
    if (sku) items = items.filter((v) => v.sku === sku.toLocaleUpperCase("tr"));
    if (barcode) items = items.filter((v) => v.barcode === barcode);
    if (q) items = items.filter((v) => v.sku.toLocaleLowerCase("tr").includes(q) || v.barcode === url.searchParams.get("q") || (v.name ?? "").toLocaleLowerCase("tr").includes(q));
    items = [...items].sort((a, b) => a.sku.localeCompare(b.sku));
    const limit = Number(url.searchParams.get("limit") ?? 50);
    await fulfillJson(route, 200, { items: items.slice(0, limit), page: { nextCursor: null, hasMore: items.length > limit, limit } });
    return true;
  }

  // GET /variants/batch?ids= (1–100): summaries; unknown ids are left out, as the API does.
  if (method === "GET" && path === "/api/v1/variants/batch") {
    const ids = (url.searchParams.get("ids") ?? "").split(",").filter(Boolean);
    if (ids.length === 0 || ids.length > 100) return fulfillJson(route, 400, problemBody(400, "request.malformed")).then(() => true);
    const items = catalog.products.flatMap((p) =>
      p.variants
        .filter((v) => ids.includes(v.id))
        .map((v) => ({ id: v.id, productId: p.id, sku: v.sku, name: v.name ?? null, productName: p.name, barcode: v.barcode ?? null, status: v.status })),
    );
    await fulfillJson(route, 200, { items });
    return true;
  }

  const variantMatch = path.match(/^\/api\/v1\/variants\/([^/]+)$/);
  if (variantMatch && method === "GET") {
    const found = allVariants().find((v) => v.id === variantMatch[1]);
    await (found ? fulfillJson(route, 200, found, etag(found.version)) : fulfillJson(route, 404, problemBody(404, "resource.not_found")));
    return true;
  }
  if (variantMatch) {
    const product = catalog.products.find((p) => p.variants.some((v) => v.id === variantMatch[1]));
    const variant = product?.variants.find((v) => v.id === variantMatch[1]);
    if (!variant) return fulfillJson(route, 404, problemBody(404, "resource.not_found")).then(() => true);
    if (versionOf(ifMatch) !== variant.version) return fulfillJson(route, 412, problemBody(412, "concurrency.version_mismatch")).then(() => true);
    if (method === "PATCH") {
      const { attributes, ...rest } = body ?? {};
      Object.assign(variant, rest, { attributes: mergeAttributes(variant.attributes, attributes as Json), version: variant.version + 1 });
      await fulfillJson(route, 200, variant, etag(variant.version));
      return true;
    }
    if (method === "DELETE") {
      Object.assign(variant, { status: "archived", version: variant.version + 1 });
      await route.fulfill({ status: 204, headers: HEADERS });
      return true;
    }
  }
  return false;
}

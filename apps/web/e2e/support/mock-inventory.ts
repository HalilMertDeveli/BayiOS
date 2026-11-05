/**
 * MOCK INVENTORY + WAREHOUSE API FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API.
 *
 * Stand-in for /api/v1/warehouses, /api/v1/locations, /api/v1/inventory/* following the contract shapes in
 * packages/contracts/openapi.json and the as-built rules in docs/architecture/inventory-and-orders.md §6:
 * keyset-style `page.nextCursor`, ETag/If-Match → 412, Idempotency-Key replay, `warehouse.default_inactive`,
 * `warehouse.has_stock`, location required in located warehouses, negative-stock policy (block → 409
 * `inventory.insufficient_available`; warn → 409 `inventory.insufficient_stock` + `confirmationRequired` until the
 * request repeats with `confirmNegativeStock: true`). It is a simplified model, not the real validation.
 * Tests inject single responses with `overrides` (one-shot, keyed "METHOD /path"). All data is invented.
 */
import type { Route } from "@playwright/test";
import { fulfillJson, problemBody, type MockCatalog } from "./mock-catalog";

type Json = Record<string, unknown>;

export type MockWarehouse = {
  id: string;
  code: string;
  name: string;
  usesLocations: boolean;
  isDefault: boolean;
  status: "active" | "inactive";
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockLocation = {
  id: string;
  warehouseId: string;
  code: string;
  barcode: string | null;
  pickSequence: number;
  isActive: boolean;
  version: number;
  createdAt: string;
  updatedAt: string | null;
};

export type MockBalance = {
  id: string;
  variantId: string;
  warehouseId: string;
  locationId: string | null;
  onHand: number;
  reserved: number;
  available: number;
  version: number;
  updatedAt: string | null;
};

export type MockMovement = {
  id: string;
  variantId: string;
  warehouseId: string;
  locationId: string | null;
  type: string;
  quantity: number;
  unitCost: number | null;
  sourceType: string;
  sourceId: string | null;
  sourceLineId: string | null;
  reason: string | null;
  balanceAfter: number;
  correlationId: string | null;
  occurredAt: string;
  createdBy: string | null;
};

export type InventoryOverride = (route: Route, body: Json | null) => Promise<unknown> | unknown;

export type MockInventory = {
  warehouses: MockWarehouse[];
  locations: MockLocation[];
  balances: MockBalance[];
  movements: MockMovement[];
  /** Tenant setting negative_stock_policy. */
  policy: "block" | "warn" | "allow";
  overrides: Map<string, InventoryOverride>;
  /** Idempotency-Key → stored success response (replayed with Idempotent-Replayed: true). */
  idempotency: Map<string, { status: number; body: unknown }>;
  /** When true, the next successful write is applied but its response is dropped (network error in the browser). */
  dropNextResponse: boolean;
};

const NOW = "2026-11-01T09:00:00Z";
const uuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;

export const WAREHOUSE_IDS = { main: uuid("9a", 1), raf: uuid("9a", 2), old: uuid("9a", 3) };
export const LOCATION_IDS = { a01: uuid("9b", 1), a02: uuid("9b", 2) };

export function defaultWarehouses(): MockWarehouse[] {
  return [
    { id: WAREHOUSE_IDS.main, code: "MAIN", name: "Ana Depo (mock)", usesLocations: false, isDefault: true, status: "active", version: 1, createdAt: NOW, updatedAt: null },
    { id: WAREHOUSE_IDS.raf, code: "RAF", name: "Raflı Depo (mock)", usesLocations: true, isDefault: false, status: "active", version: 1, createdAt: NOW, updatedAt: null },
  ];
}

export function defaultLocations(): MockLocation[] {
  return [
    { id: LOCATION_IDS.a01, warehouseId: WAREHOUSE_IDS.raf, code: "A-01", barcode: "RAF-A01", pickSequence: 10, isActive: true, version: 1, createdAt: NOW, updatedAt: null },
    { id: LOCATION_IDS.a02, warehouseId: WAREHOUSE_IDS.raf, code: "A-02", barcode: null, pickSequence: 20, isActive: true, version: 1, createdAt: NOW, updatedAt: null },
  ];
}

/**
 * Inventory for a catalog: by default every variant of the first `stocked` products gets one balance in MAIN
 * (onHand = 10 + n, reserved = n % 3), so balance n belongs to product n.
 */
export function createMockInventory(
  catalog: MockCatalog | null,
  options: { stocked?: number; warehouses?: MockWarehouse[]; locations?: MockLocation[]; policy?: MockInventory["policy"] } = {},
): MockInventory {
  const variants = (catalog?.products ?? []).flatMap((p) => p.variants).slice(0, options.stocked ?? 3);
  const balances: MockBalance[] = variants.map((v, i) => {
    const onHand = 10 + i + 1;
    const reserved = (i + 1) % 3;
    return { id: uuid("9c", i + 1), variantId: v.id, warehouseId: WAREHOUSE_IDS.main, locationId: null, onHand, reserved, available: onHand - reserved, version: 1, updatedAt: NOW };
  });
  return {
    warehouses: options.warehouses ?? defaultWarehouses(),
    locations: options.locations ?? defaultLocations(),
    balances,
    movements: [],
    policy: options.policy ?? "block",
    overrides: new Map(),
    idempotency: new Map(),
    dropNextResponse: false,
  };
}

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const etag = (version: number) => ({ ETag: `W/"${version}"`, ...HEADERS });
const versionOf = (ifMatch: string | undefined) => (ifMatch ? Number(ifMatch.replace(/^W\//, "").replace(/"/g, "")) : NaN);
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, body, { ...HEADERS, ...headers });

/** 409 inventory.busy with Retry-After, as the API sends it (for overrides). */
export function busyResponse(route: Route) {
  return json(route, 409, problemBody(409, "inventory.busy"), { "Retry-After": "1" });
}

function page<T extends { id: string }>(items: T[], params: URLSearchParams) {
  const limit = Number(params.get("limit") ?? 50);
  const offset = Number((params.get("cursor") ?? "o:0").slice(2));
  const slice = items.slice(offset, offset + limit);
  const hasMore = offset + limit < items.length;
  return { items: slice, page: { nextCursor: hasMore ? `o:${offset + limit}` : null, hasMore, limit } };
}

function balanceRow(inv: MockInventory, variantId: string, warehouseId: string, locationId: string | null): MockBalance {
  let row = inv.balances.find((b) => b.variantId === variantId && b.warehouseId === warehouseId && b.locationId === locationId);
  if (!row) {
    row = { id: uuid("9c", 500 + inv.balances.length), variantId, warehouseId, locationId, onHand: 0, reserved: 0, available: 0, version: 0, updatedAt: null };
    inv.balances.push(row);
  }
  return row;
}

function apply(inv: MockInventory, row: MockBalance, type: string, delta: number, sourceType: string, sourceId: string, reason: string | null): MockMovement {
  row.onHand = Math.round((row.onHand + delta) * 1000) / 1000;
  row.available = Math.round((row.onHand - row.reserved) * 1000) / 1000;
  row.version += 1;
  row.updatedAt = new Date().toISOString();
  const movement: MockMovement = {
    id: uuid("9d", inv.movements.length + 1),
    variantId: row.variantId,
    warehouseId: row.warehouseId,
    locationId: row.locationId,
    type,
    quantity: delta,
    unitCost: null,
    sourceType,
    sourceId,
    sourceLineId: null,
    reason,
    balanceAfter: row.onHand,
    correlationId: null,
    occurredAt: new Date(Date.UTC(2026, 10, 2) + inv.movements.length * 60_000).toISOString(),
    createdBy: null,
  };
  inv.movements.push(movement);
  return movement;
}

/** Validates the warehouse/location pair like the API (§2.6); returns the 422 errors, if any. */
function locationErrors(inv: MockInventory, warehouse: MockWarehouse, field: string, locationId: string | null): Record<string, string[]> {
  if (!warehouse.usesLocations) return locationId ? { [field]: ["This warehouse does not use locations."] } : {};
  if (!locationId) return { [field]: ["A location is required in this warehouse."] };
  const location = inv.locations.find((l) => l.id === locationId && l.warehouseId === warehouse.id);
  if (!location) return { [field]: ["Location not found in this warehouse."] };
  if (!location.isActive) return { [field]: ["Location is inactive."] };
  return {};
}

/** Handles a Warehouse/Inventory request; returns false when the path is not one of them. */
export async function handleInventory(route: Route, inv: MockInventory, catalog: MockCatalog | null): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  if (!path.startsWith("/api/v1/warehouses") && !path.startsWith("/api/v1/locations") && !path.startsWith("/api/v1/inventory")) return false;
  const body = (request.postDataJSON?.() ?? null) as Json | null;

  const override = inv.overrides.get(`${method} ${path}`);
  if (override) {
    inv.overrides.delete(`${method} ${path}`);
    await override(route, body);
    return true;
  }

  const ifMatch = request.headers()["if-match"];
  const key = request.headers()["idempotency-key"];
  const variants = (catalog?.products ?? []).flatMap((p) => p.variants);

  /** Stores and sends a write's success response (or drops it once, after applying it). */
  const succeed = async (status: number, payload: unknown, headers: Record<string, string> = {}) => {
    if (key) inv.idempotency.set(`${method} ${path} ${key}`, { status, body: payload });
    if (inv.dropNextResponse) {
      inv.dropNextResponse = false;
      await route.abort("connectionreset");
      return true;
    }
    await json(route, status, payload, headers);
    return true;
  };

  if (method === "POST" && key) {
    const replay = inv.idempotency.get(`${method} ${path} ${key}`);
    if (replay) {
      await json(route, replay.status, replay.body, { "Idempotent-Replayed": "true" });
      return true;
    }
  }

  // ---- Warehouses ----
  if (path === "/api/v1/warehouses" && method === "GET") {
    const all = url.searchParams.get("includeInactive") === "true";
    const items = inv.warehouses.filter((w) => all || w.status === "active").sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.code.localeCompare(b.code));
    await json(route, 200, { items });
    return true;
  }
  if (path === "/api/v1/warehouses" && method === "POST") {
    const code = String(body?.code ?? "").trim().toUpperCase();
    if (inv.warehouses.some((w) => w.code === code)) return json(route, 409, problemBody(409, "warehouse.code_taken")).then(() => true);
    const created: MockWarehouse = {
      id: uuid("9a", 100 + inv.warehouses.length),
      code,
      name: String(body?.name ?? "").trim(),
      usesLocations: body?.usesLocations === true,
      isDefault: !inv.warehouses.some((w) => w.isDefault),
      status: "active",
      version: 1,
      createdAt: new Date().toISOString(),
      updatedAt: null,
    };
    inv.warehouses.push(created);
    return succeed(201, created, etag(created.version));
  }
  const whMatch = path.match(/^\/api\/v1\/warehouses\/([^/]+)(\/locations)?$/);
  if (whMatch) {
    const warehouse = inv.warehouses.find((w) => w.id === whMatch[1]);
    if (!warehouse) return json(route, 404, problemBody(404, "resource.not_found")).then(() => true);
    if (whMatch[2] && method === "GET") {
      const all = url.searchParams.get("includeInactive") === "true";
      const items = inv.locations
        .filter((l) => l.warehouseId === warehouse.id && (all || l.isActive))
        .sort((a, b) => a.pickSequence - b.pickSequence || a.code.localeCompare(b.code));
      await json(route, 200, { items });
      return true;
    }
    if (whMatch[2] && method === "POST") {
      if (!warehouse.usesLocations) return json(route, 409, problemBody(409, "warehouse.locations_disabled")).then(() => true);
      const code = String(body?.code ?? "").trim().toUpperCase();
      if (inv.locations.some((l) => l.warehouseId === warehouse.id && l.code === code)) {
        return json(route, 409, problemBody(409, "warehouse.location_code_taken")).then(() => true);
      }
      const created: MockLocation = {
        id: uuid("9b", 100 + inv.locations.length),
        warehouseId: warehouse.id,
        code,
        barcode: (body?.barcode as string) ?? null,
        pickSequence: Number(body?.pickSequence ?? 0),
        isActive: true,
        version: 1,
        createdAt: new Date().toISOString(),
        updatedAt: null,
      };
      inv.locations.push(created);
      return succeed(201, created, etag(created.version));
    }
    if (method === "GET") return json(route, 200, warehouse, etag(warehouse.version)).then(() => true);
    if (method === "PATCH") {
      if (!ifMatch) return json(route, 428, problemBody(428, "precondition.required")).then(() => true);
      if (versionOf(ifMatch) !== warehouse.version) return json(route, 412, problemBody(412, "concurrency.version_mismatch")).then(() => true);
      const makeDefault = body?.isDefault === true && !warehouse.isDefault;
      const status = (body?.status as MockWarehouse["status"]) ?? warehouse.status;
      if (status !== "active" && (warehouse.isDefault || makeDefault)) return json(route, 409, problemBody(409, "warehouse.default_inactive")).then(() => true);
      if (typeof body?.usesLocations === "boolean" && body.usesLocations !== warehouse.usesLocations) {
        const hasStock = inv.balances.some((b) => b.warehouseId === warehouse.id && (b.onHand !== 0 || b.reserved !== 0));
        if (hasStock) return json(route, 409, problemBody(409, "warehouse.has_stock")).then(() => true);
        warehouse.usesLocations = body.usesLocations;
      }
      if (typeof body?.name === "string") warehouse.name = body.name;
      warehouse.status = status;
      if (makeDefault) {
        for (const w of inv.warehouses) w.isDefault = false;
        warehouse.isDefault = true;
      }
      warehouse.version += 1;
      warehouse.updatedAt = new Date().toISOString();
      await json(route, 200, warehouse, etag(warehouse.version));
      return true;
    }
  }
  const locMatch = path.match(/^\/api\/v1\/locations\/([^/]+)$/);
  if (locMatch && method === "PATCH") {
    const location = inv.locations.find((l) => l.id === locMatch[1]);
    if (!location) return json(route, 404, problemBody(404, "resource.not_found")).then(() => true);
    if (versionOf(ifMatch) !== location.version) return json(route, 412, problemBody(412, "concurrency.version_mismatch")).then(() => true);
    if (typeof body?.code === "string") location.code = body.code.toUpperCase();
    if (typeof body?.barcode === "string") location.barcode = body.barcode || null;
    if (typeof body?.pickSequence === "number") location.pickSequence = body.pickSequence;
    if (typeof body?.isActive === "boolean") location.isActive = body.isActive;
    location.version += 1;
    await json(route, 200, location, etag(location.version));
    return true;
  }

  // ---- Inventory ----
  if (path === "/api/v1/inventory/balances" && method === "GET") {
    const p = url.searchParams;
    // q: SKU contains, exact barcode or product name contains (as the Catalog variant search); "*" = too broad (422).
    const q = (p.get("q") ?? "").trim();
    if (q === "*") {
      await json(route, 422, problemBody(422, "inventory.search_too_broad"));
      return true;
    }
    const term = q.toLocaleLowerCase("tr");
    const matching = q
      ? new Set(
          (catalog?.products ?? []).flatMap((pr) =>
            pr.variants
              .filter((v) => v.sku.toLocaleLowerCase("tr").includes(term) || v.barcode === q || pr.name.toLocaleLowerCase("tr").includes(term))
              .map((v) => v.id),
          ),
        )
      : null;
    const items = inv.balances
      .filter((b) => !matching || matching.has(b.variantId))
      .filter((b) => (!p.get("variantId") || b.variantId === p.get("variantId")) && (!p.get("warehouseId") || b.warehouseId === p.get("warehouseId")))
      .filter((b) => !p.get("locationId") || b.locationId === p.get("locationId"))
      .sort((a, b) => a.id.localeCompare(b.id));
    await json(route, 200, page(items, p));
    return true;
  }
  if (path === "/api/v1/inventory/movements" && method === "GET") {
    const p = url.searchParams;
    const items = inv.movements
      .filter((m) => (!p.get("variantId") || m.variantId === p.get("variantId")) && (!p.get("warehouseId") || m.warehouseId === p.get("warehouseId")))
      .filter((m) => (!p.get("type") || m.type === p.get("type")) && (!p.get("sourceId") || m.sourceId === p.get("sourceId")))
      .sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
    await json(route, 200, page(items, p));
    return true;
  }
  if (path === "/api/v1/inventory/receipts" && method === "POST") {
    if (!key) return json(route, 428, problemBody(428, "precondition.required")).then(() => true);
    const warehouse = body?.warehouseId ? inv.warehouses.find((w) => w.id === body.warehouseId) : inv.warehouses.find((w) => w.isDefault);
    if (!warehouse) return json(route, 422, problemBody(422, "validation.failed", { errors: { warehouseId: ["Warehouse not found."] } })).then(() => true);
    const lines = (body?.lines as Json[]) ?? [];
    let errors: Record<string, string[]> = {};
    if (lines.length === 0) errors.lines = ["Between 1 and 500 lines are required."];
    lines.forEach((l, i) => {
      errors = { ...errors, ...locationErrors(inv, warehouse, `lines[${i}].locationId`, (l.locationId as string) ?? null) };
      if (!variants.some((v) => v.id === l.variantId)) errors[`lines[${i}].variantId`] = ["Variant not found."];
      if (!(Number(l.quantity) > 0)) errors[`lines[${i}].quantity`] = ["Must be greater than 0 with at most 3 decimals."];
    });
    if (Object.keys(errors).length > 0) return json(route, 422, problemBody(422, "validation.failed", { errors })).then(() => true);
    const receiptId = uuid("9e", inv.idempotency.size + 1);
    const movements = lines.map((l) =>
      apply(inv, balanceRow(inv, String(l.variantId), warehouse.id, (l.locationId as string) ?? null), "receipt", Number(l.quantity), "receipt", receiptId, (body?.reference as string) ?? null),
    );
    return succeed(201, { receiptId, warehouseId: warehouse.id, movements });
  }
  if (path === "/api/v1/inventory/adjustments" && method === "POST") {
    if (!key) return json(route, 428, problemBody(428, "precondition.required")).then(() => true);
    const warehouse = inv.warehouses.find((w) => w.id === body?.warehouseId);
    const errors: Record<string, string[]> = warehouse ? locationErrors(inv, warehouse, "locationId", (body?.locationId as string) ?? null) : { warehouseId: ["Warehouse not found."] };
    const variant = variants.find((v) => v.id === body?.variantId);
    if (!variant) errors.variantId = ["Variant not found."];
    if (!body?.reason) errors.reason = ["A reason of 1–500 characters is required."];
    if (Object.keys(errors).length > 0 || !warehouse || !variant) return json(route, 422, problemBody(422, "validation.failed", { errors })).then(() => true);
    const type = String(body?.type);
    const quantity = Number(body?.quantity);
    const row = balanceRow(inv, variant.id, warehouse.id, (body?.locationId as string) ?? null);
    const outbound = type === "adjustment_out" || type === "damage_out";
    if (outbound && row.available < quantity) {
      const shortages = [{ variantId: variant.id, sku: variant.sku, warehouseId: warehouse.id, locationId: row.locationId, requested: quantity, available: row.available }];
      if (inv.policy === "block") return json(route, 409, problemBody(409, "inventory.insufficient_available", { shortages })).then(() => true);
      if (inv.policy === "warn" && body?.confirmNegativeStock !== true) {
        return json(route, 409, problemBody(409, "inventory.insufficient_stock", { shortages, confirmationRequired: true })).then(() => true);
      }
    }
    const movement = apply(inv, row, type, outbound ? -quantity : quantity, "adjustment", uuid("9f", inv.movements.length + 1), String(body?.reason));
    return succeed(201, movement);
  }
  return false;
}

/**
 * MOCK ORDERS API FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API.
 *
 * Stand-in for /api/v1/orders following packages/contracts/openapi.json and docs/architecture/inventory-and-orders.md
 * §7: keyset-style `page.nextCursor` (newest first), status/customer/source/date/number filters, ETag/If-Match → 412,
 * Idempotency-Key required on create and transitions (replayed on reuse), draft-only PATCH (409 order.not_editable),
 * the order-desk transitions with 409 order.invalid_transition, 422 order.price_missing on submit/approve, all-or-nothing
 * approve with 409 inventory.insufficient_stock + `shortages` (+ `confirmationRequired` under the warn policy), and the
 * role gates as the API enforces them (only ✓ grants; sales cannot edit drafts, approve, cancel or override prices).
 * Prices come from the mock default price list only (no customer/dealer lists, no tiers); totals use simplified
 * rounding. Tests inject single responses with `overrides` (one-shot, keyed "METHOD /path"). All values are invented.
 */
import type { Route } from "@playwright/test";
import { fulfillJson, problemBody, type MockCatalog } from "./mock-catalog";
import { PRICE_LIST_IDS, type MockCommerce } from "./mock-commerce";
import { WAREHOUSE_IDS } from "./mock-inventory";

type Json = Record<string, unknown>;

export type MockOrderLine = {
  id: string;
  lineNo: number;
  variantId: string;
  sku: string;
  name: string;
  unit: string;
  quantity: number;
  priceStatus: string;
  unitPrice: number | null;
  priceSource: string | null;
  priceListId: string | null;
  priceOverridden: boolean;
  discountPercent: number | null;
  discountAmount: number | null;
  taxRate: number;
  lineSubtotal: number | null;
  lineDiscount: number | null;
  lineTotal: number | null;
  reservedQuantity: number;
  shippedQuantity: number;
  cancelledQuantity: number;
  openQuantity: number;
};

export type MockHistory = {
  id: string;
  action: string;
  fromStatus: string | null;
  toStatus: string;
  fulfillmentStatus: string;
  reason: string | null;
  changedBy: string | null;
  changedAt: string;
};

export type MockOrder = {
  id: string;
  number: string;
  customerId: string;
  warehouseId: string;
  status: string;
  fulfillmentStatus: string;
  source: string;
  currency: string;
  pricesIncludeTax: boolean;
  priceDate: string;
  requestedDeliveryDate: string | null;
  notes: string | null;
  createdAt: string;
  approvedAt: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  /** A picker reported a shortage (Warehouse, JOB 10). */
  hasShortage: boolean;
  version: number;
  lines: MockOrderLine[];
  history: MockHistory[];
};

export type OrderOverride = (route: Route, body: Json | null) => Promise<unknown> | unknown;

export type MockOrders = {
  orders: MockOrder[];
  /** Satılabilir stock per variant for approve (missing = plenty). */
  available: Map<string, number>;
  /** Tenant negative_stock_policy. */
  policy: "block" | "warn";
  overrides: Map<string, OrderOverride>;
  idempotency: Map<string, { status: number; body: unknown }>;
};

export const MOCK_USER_ID = "00000000-0000-4000-8000-000000000001";
const OTHER_USER_ID = "00000000-0000-4000-8000-000000000099";
const uuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;
export const orderId = (n: number) => uuid("6a", n);
const round2 = (v: number) => Math.round(v * 100) / 100;

type Ctx = { catalog: MockCatalog | null; commerce: MockCommerce | null };

function variantInfo(ctx: Ctx, variantId: string) {
  for (const p of ctx.catalog?.products ?? []) {
    const v = p.variants.find((x) => x.id === variantId);
    if (v) return { sku: v.sku, name: p.name, taxRate: p.taxRate };
  }
  return null;
}

function listPrice(ctx: Ctx, variantId: string): number | null {
  const item = ctx.commerce?.items.find((i) => i.priceListId === PRICE_LIST_IDS.perakende && i.variantId === variantId && Number(i.minQuantity) <= 1);
  return item ? Number(item.unitPrice) : null;
}

function priceLine(ctx: Ctx, line: MockOrderLine, input: { unitPrice?: unknown; discountPercent?: unknown; discountAmount?: unknown }) {
  const manual = input.unitPrice === null || input.unitPrice === undefined ? null : Number(input.unitPrice);
  const list = listPrice(ctx, line.variantId);
  line.unitPrice = manual ?? list;
  line.priceOverridden = manual !== null;
  line.priceSource = manual !== null ? "manual" : list !== null ? "default_price_list" : null;
  line.priceListId = manual === null && list !== null ? PRICE_LIST_IDS.perakende : null;
  line.priceStatus = line.unitPrice === null ? "no_price" : "priced";
  line.discountPercent = input.discountPercent === null || input.discountPercent === undefined ? null : Number(input.discountPercent);
  line.discountAmount = input.discountAmount === null || input.discountAmount === undefined ? null : Number(input.discountAmount);
  if (line.unitPrice === null) {
    line.lineSubtotal = line.lineDiscount = line.lineTotal = null;
  } else {
    const sub = round2(line.quantity * line.unitPrice);
    const disc = line.discountAmount ?? round2((sub * (line.discountPercent ?? 0)) / 100);
    line.lineSubtotal = sub;
    line.lineDiscount = disc;
    line.lineTotal = round2(sub - disc);
  }
  line.openQuantity = line.quantity - line.shippedQuantity - line.cancelledQuantity;
}

function totals(order: MockOrder) {
  const priced = order.lines.filter((l) => l.lineTotal !== null);
  const subtotal = round2(priced.reduce((s, l) => s + (l.lineSubtotal ?? 0), 0));
  const discountTotal = round2(priced.reduce((s, l) => s + (l.lineDiscount ?? 0), 0));
  const rates = [...new Set(priced.map((l) => l.taxRate))];
  const taxBreakdown = rates.map((rate) => {
    const base = round2(priced.filter((l) => l.taxRate === rate).reduce((s, l) => s + (l.lineTotal ?? 0), 0));
    const tax = round2((base * rate) / 100);
    return { rate, taxBase: base, tax, total: round2(base + tax) };
  });
  const taxTotal = round2(taxBreakdown.reduce((s, t) => s + t.tax, 0));
  const netTotal = round2(subtotal - discountTotal);
  return {
    pricesIncludeTax: order.pricesIncludeTax,
    subtotal,
    discountTotal,
    taxTotal,
    netTotal,
    grandTotal: round2(netTotal + taxTotal),
    taxBreakdown,
    hasUnpricedLines: order.lines.some((l) => l.unitPrice === null),
  };
}

function orderResponse(order: MockOrder) {
  return {
    id: order.id,
    number: order.number,
    customerId: order.customerId,
    warehouseId: order.warehouseId,
    status: order.status,
    fulfillmentStatus: order.fulfillmentStatus,
    source: order.source,
    sourceReferenceId: null,
    currency: order.currency,
    priceDate: order.priceDate,
    requestedDeliveryDate: order.requestedDeliveryDate,
    notes: order.notes,
    totals: totals(order),
    createdAt: order.createdAt,
    createdBy: MOCK_USER_ID,
    submittedAt: null,
    approvedAt: order.approvedAt,
    approvedBy: order.approvedAt ? MOCK_USER_ID : null,
    shippedAt: null,
    completedAt: null,
    cancelledAt: order.cancelledAt,
    cancelReason: order.cancelReason,
    hasShortage: order.hasShortage,
    version: order.version,
  };
}

export function orderDetailBody(order: MockOrder) {
  return { order: orderResponse(order), lines: order.lines, history: order.history, shipments: [] };
}

function summary(order: MockOrder) {
  return {
    id: order.id,
    number: order.number,
    customerId: order.customerId,
    warehouseId: order.warehouseId,
    status: order.status,
    fulfillmentStatus: order.fulfillmentStatus,
    source: order.source,
    currency: order.currency,
    grandTotal: totals(order).grandTotal,
    lineCount: order.lines.length,
    createdAt: order.createdAt,
    version: order.version,
  };
}

let historyCounter = 0;
function addHistory(order: MockOrder, action: string, from: string | null, reason: string | null = null, by = MOCK_USER_ID) {
  order.history.push({
    id: uuid("6c", ++historyCounter),
    action,
    fromStatus: from,
    toStatus: order.status,
    fulfillmentStatus: order.fulfillmentStatus,
    reason,
    changedBy: by,
    changedAt: new Date(Date.UTC(2026, 10, 5, 9) + historyCounter * 60_000).toISOString(),
  });
}

/** A synthetic order: lines priced from the default list; `status` sets the state directly (history is added). */
export function makeOrder(
  ctx: Ctx,
  n: number,
  options: {
    customerId: string;
    status?: string;
    fulfillmentStatus?: string;
    hasShortage?: boolean;
    source?: string;
    createdAt?: string;
    lines?: { variantId: string; quantity: number; unitPrice?: number | null }[];
  },
): MockOrder {
  const order: MockOrder = {
    id: orderId(n),
    number: `SIP-2026-${String(n).padStart(6, "0")}`,
    customerId: options.customerId,
    warehouseId: WAREHOUSE_IDS.main,
    status: "draft",
    fulfillmentStatus: options.fulfillmentStatus ?? "none",
    source: options.source ?? "manual",
    currency: "TRY",
    pricesIncludeTax: false,
    priceDate: "2026-11-05",
    requestedDeliveryDate: null,
    notes: null,
    createdAt: options.createdAt ?? new Date(Date.UTC(2026, 10, 1) + n * 3_600_000).toISOString(),
    approvedAt: null,
    cancelledAt: null,
    cancelReason: null,
    hasShortage: options.hasShortage ?? false,
    version: 1,
    lines: [],
    history: [],
  };
  order.lines = (options.lines ?? []).map((l, i) => buildLine(ctx, order, i, { id: null, variantId: l.variantId, quantity: l.quantity, unitPrice: l.unitPrice ?? null }));
  addHistory(order, "create", null, null, OTHER_USER_ID);
  const status = options.status ?? "draft";
  if (status !== "draft") {
    const from = order.status;
    order.status = status;
    if (["approved", "picking", "packed", "shipped", "completed"].includes(status)) {
      order.approvedAt = "2026-11-05T10:00:00Z";
      for (const l of order.lines) l.reservedQuantity = l.quantity;
    }
    addHistory(order, status === "pending_approval" ? "submit" : status === "cancelled" ? "cancel" : "approve", from, null, OTHER_USER_ID);
  }
  return order;
}

let lineCounter = 0;
function buildLine(ctx: Ctx, order: MockOrder, index: number, input: Json, existing?: MockOrderLine): MockOrderLine {
  const variantId = String(input.variantId);
  const info = variantInfo(ctx, variantId);
  const line: MockOrderLine = existing ?? {
    id: uuid("6b", ++lineCounter),
    lineNo: 0,
    variantId,
    sku: info?.sku ?? "?",
    name: info?.name ?? "",
    unit: "ADET",
    quantity: 0,
    priceStatus: "no_price",
    unitPrice: null,
    priceSource: null,
    priceListId: null,
    priceOverridden: false,
    discountPercent: null,
    discountAmount: null,
    taxRate: info?.taxRate ?? 20,
    lineSubtotal: null,
    lineDiscount: null,
    lineTotal: null,
    reservedQuantity: 0,
    shippedQuantity: 0,
    cancelledQuantity: 0,
    openQuantity: 0,
  };
  line.lineNo = index + 1;
  line.variantId = variantId;
  line.sku = info?.sku ?? line.sku;
  line.quantity = Number(input.quantity);
  priceLine(ctx, line, input);
  void order;
  return line;
}

export function createMockOrders(options: { orders?: MockOrder[]; policy?: MockOrders["policy"]; available?: [string, number][] } = {}): MockOrders {
  return {
    orders: options.orders ?? [],
    available: new Map(options.available ?? []),
    policy: options.policy ?? "block",
    overrides: new Map(),
    idempotency: new Map(),
  };
}

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, body, { ...HEADERS, ...headers });
const done = (p: Promise<unknown>) => p.then(() => true);
const versionOf = (ifMatch: string | undefined) => (ifMatch ? Number(ifMatch.replace(/^W\//, "").replace(/"/g, "")) : NaN);
const MANAGERS = ["owner", "admin", "manager"];

/** 409 order.busy with Retry-After: 1, as the API sends it on a lock timeout (for overrides). */
export function orderBusyResponse(route: Route) {
  return json(route, 409, problemBody(409, "order.busy"), { "Retry-After": "1" });
}

const PERMISSIONS: Record<string, string[]> = {
  read: ["owner", "admin", "manager", "sales", "viewer"],
  create: ["owner", "admin", "manager", "sales"],
  edit: MANAGERS,
  submit: ["owner", "admin", "manager", "sales"],
  approve: MANAGERS,
  cancel: MANAGERS,
  override: MANAGERS,
};
const allowed = (role: string, permission: string) => PERMISSIONS[permission].includes(role);

/** Handles an Orders request; returns false when the path is not one of them. */
export async function handleOrders(route: Route, mock: MockOrders, ctx: Ctx, role: string): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  if (!path.startsWith("/api/v1/orders")) return false;
  const body = (request.postDataJSON?.() ?? null) as Json | null;

  const override = mock.overrides.get(`${method} ${path}`);
  if (override) {
    mock.overrides.delete(`${method} ${path}`);
    await override(route, body);
    return true;
  }

  const forbidden = (permission: string) => done(json(route, 403, problemBody(403, "auth.forbidden", { title: `Missing permission: ${permission}` })));
  if (!allowed(role, "read")) return forbidden("orders.read");

  const key = request.headers()["idempotency-key"];
  const ifMatch = request.headers()["if-match"];
  if (method === "POST" && !key) return done(json(route, 428, problemBody(428, "precondition.required")));
  if (method === "POST" && key) {
    const replay = mock.idempotency.get(`${path} ${key}`);
    if (replay) return done(json(route, replay.status, replay.body, { "Idempotent-Replayed": "true" }));
  }
  const succeed = (status: number, order: MockOrder) => {
    const payload = orderDetailBody(order);
    if (key) mock.idempotency.set(`${path} ${key}`, { status, body: payload });
    return done(json(route, status, payload, { ETag: `W/"${order.version}"` }));
  };

  // ---- List ----
  if (path === "/api/v1/orders" && method === "GET") {
    const p = url.searchParams;
    const statuses = (p.get("status") ?? "").split(",").filter(Boolean);
    let items = [...mock.orders];
    if (statuses.length) items = items.filter((o) => statuses.includes(o.status));
    if (p.get("customerId")) items = items.filter((o) => o.customerId === p.get("customerId"));
    if (p.get("source")) items = items.filter((o) => o.source === p.get("source"));
    if (p.get("from")) items = items.filter((o) => o.createdAt.slice(0, 10) >= p.get("from")!);
    if (p.get("to")) items = items.filter((o) => o.createdAt.slice(0, 10) <= p.get("to")!);
    if (p.get("q")) items = items.filter((o) => o.number.toLowerCase().includes(p.get("q")!.toLowerCase()));
    items.sort((a, b) => b.createdAt.localeCompare(a.createdAt) || b.id.localeCompare(a.id));
    const limit = Number(p.get("limit") ?? 50);
    const offset = Number((p.get("cursor") ?? "o:0").slice(2));
    const hasMore = offset + limit < items.length;
    return done(json(route, 200, { items: items.slice(offset, offset + limit).map(summary), page: { nextCursor: hasMore ? `o:${offset + limit}` : null, hasMore, limit } }));
  }

  // ---- Create ----
  if (path === "/api/v1/orders" && method === "POST") {
    if (!allowed(role, "create")) return forbidden("orders.create");
    if (!body?.customerId) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { customerId: ["Required."] } })));
    const lines = (body.lines as Json[] | null) ?? [];
    if (!allowed(role, "override") && lines.some((l) => l.unitPrice != null || l.discountPercent != null || l.discountAmount != null)) {
      return forbidden("orders.price.override");
    }
    const n = mock.orders.length + 1000;
    const order = makeOrder(ctx, n, { customerId: String(body.customerId), createdAt: new Date(Date.UTC(2026, 10, 5, 12) + mock.orders.length * 60_000).toISOString() });
    order.history = [];
    addHistory(order, "create", null);
    order.warehouseId = String(body.warehouseId ?? WAREHOUSE_IDS.main);
    order.notes = (body.notes as string | null) ?? null;
    order.requestedDeliveryDate = (body.requestedDeliveryDate as string | null) ?? null;
    order.pricesIncludeTax = body.pricesIncludeTax === true;
    order.lines = lines.map((l, i) => buildLine(ctx, order, i, l));
    mock.orders.push(order);
    return succeed(201, order);
  }

  const match = path.match(/^\/api\/v1\/orders\/([^/]+)(?:\/([a-z-]+))?$/);
  if (!match) return false;
  const order = mock.orders.find((o) => o.id === match[1]);
  if (!order) return done(json(route, 404, problemBody(404, "resource.not_found")));
  const action = match[2];

  if (!action && method === "GET") return done(json(route, 200, orderDetailBody(order), { ETag: `W/"${order.version}"` }));

  const checkVersion = () => ifMatch !== undefined && versionOf(ifMatch) !== order.version;

  // ---- Draft edit ----
  if (!action && method === "PATCH") {
    if (!allowed(role, "edit")) return forbidden("orders.edit.draft");
    if (!ifMatch) return done(json(route, 428, problemBody(428, "precondition.required")));
    if (checkVersion()) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));
    if (order.status !== "draft") return done(json(route, 409, problemBody(409, "order.not_editable")));
    if (body && "customerId" in body) order.customerId = String(body.customerId);
    if (body && "warehouseId" in body) order.warehouseId = String(body.warehouseId);
    if (body && "notes" in body) order.notes = (body.notes as string | null) ?? null;
    if (body && "requestedDeliveryDate" in body) order.requestedDeliveryDate = (body.requestedDeliveryDate as string | null) ?? null;
    if (body && "pricesIncludeTax" in body) order.pricesIncludeTax = body.pricesIncludeTax === true;
    if (body && Array.isArray(body.lines)) {
      const inputs = body.lines as Json[];
      const errors: Record<string, string[]> = {};
      inputs.forEach((l, i) => {
        if (!(Number(l.quantity) > 0)) errors[`lines[${i}].quantity`] = ["Must be greater than 0 with at most 3 decimals."];
      });
      if (Object.keys(errors).length) return done(json(route, 422, problemBody(422, "validation.failed", { errors })));
      order.lines = inputs.map((l, i) => buildLine(ctx, order, i, l, order.lines.find((x) => x.id === l.id)));
    }
    order.version += 1;
    return done(json(route, 200, orderDetailBody(order), { ETag: `W/"${order.version}"` }));
  }

  if (method !== "POST" || !action) return false;

  // ---- Transitions ----
  const gate: Record<string, string> = { submit: "submit", withdraw: "submit", reject: "approve", approve: "approve", cancel: "cancel" };
  if (!gate[action]) return done(json(route, 404, problemBody(404, "resource.not_found")));
  if (!allowed(role, gate[action])) return forbidden(`orders.${gate[action]}`);
  if (action === "approve" && !ifMatch) return done(json(route, 428, problemBody(428, "precondition.required")));
  if (checkVersion()) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));

  const from = order.status;
  const next: Record<string, Record<string, string>> = {
    submit: { draft: "pending_approval" },
    withdraw: { pending_approval: "draft" },
    reject: { pending_approval: "draft" },
    approve: { pending_approval: "approved" },
    cancel: { draft: "cancelled", pending_approval: "cancelled", ...(order.fulfillmentStatus === "none" ? { approved: "cancelled", picking: "cancelled", packed: "cancelled" } : {}) },
  };
  const target = next[action][from];
  if (!target) return done(json(route, 409, problemBody(409, "order.invalid_transition")));

  const reason = typeof body?.reason === "string" ? body.reason.trim() : "";
  if ((action === "reject" || action === "cancel") && !reason) {
    return done(json(route, 422, problemBody(422, "validation.failed", { errors: { reason: ["Required."] } })));
  }
  if (action === "submit" || action === "approve") {
    if (order.lines.length === 0) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { lines: ["An order needs at least one line."] } })));
    const unpriced = order.lines.filter((l) => l.unitPrice === null);
    if (unpriced.length) {
      return done(
        json(
          route,
          422,
          problemBody(422, "order.price_missing", { errors: Object.fromEntries(unpriced.map((l) => [`lines[${l.lineNo - 1}].unitPrice`, [`No price for ${l.sku}.`]])) }),
        ),
      );
    }
  }
  if (action === "approve") {
    const shortages = order.lines
      .filter((l) => mock.available.has(l.variantId) && l.quantity > mock.available.get(l.variantId)!)
      .map((l) => ({ variantId: l.variantId, sku: l.sku, warehouseId: order.warehouseId, locationId: null, requested: l.quantity, available: mock.available.get(l.variantId)! }));
    if (shortages.length && !(mock.policy === "warn" && body?.confirmNegativeStock === true)) {
      return done(
        json(route, 409, problemBody(409, "inventory.insufficient_stock", { shortages, ...(mock.policy === "warn" ? { confirmationRequired: true } : {}) })),
      );
    }
    for (const l of order.lines) {
      l.reservedQuantity = l.quantity;
      if (mock.available.has(l.variantId)) mock.available.set(l.variantId, mock.available.get(l.variantId)! - l.quantity);
    }
    order.approvedAt = "2026-11-05T12:00:00Z";
  }
  if (action === "cancel") {
    order.cancelledAt = "2026-11-05T12:30:00Z";
    order.cancelReason = reason;
    for (const l of order.lines) l.reservedQuantity = 0;
  }
  order.status = target;
  order.version += 1;
  addHistory(order, action, from, reason || null);
  return succeed(200, order);
}

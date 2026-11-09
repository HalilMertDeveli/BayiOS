/**
 * MOCK WAREHOUSE FULFILMENT API FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API.
 *
 * Stand-in for /api/v1/picking-tasks and /api/v1/shipments following packages/contracts/openapi.json and
 * docs/architecture/inventory-and-orders.md §8: "mine" (assigned | pool), list with status filter, claim / assign /
 * unassign (tasks.assign), scans resolving barcode then SKU with picking.unknown_code / wrong_item / location_required /
 * unknown_location / wrong_location / over_pick (nothing changes on a rejection), shortage, complete (→ draft shipment
 * from what was picked), pack check (packing.over_pack / quantity_mismatch), confirm (once: same key replays, other key
 * 409 shipment.already_confirmed) and the movements of a confirmed shipment via GET /inventory/movements?sourceId=.
 * Idempotency-Key required on every POST and replayed; If-Match checked when sent (412). Staff see only their own and
 * pool tasks (others → 404) and act only on their own (403 picking.not_assignee). Tests inject single responses with
 * `overrides` (one-shot, keyed "METHOD /path"). All values are invented.
 */
import type { Route } from "@playwright/test";
import { fulfillJson, problemBody } from "./mock-catalog";

type Json = Record<string, unknown>;

export type MockTaskLine = {
  id: string;
  sequence: number;
  orderLineId: string;
  variantId: string;
  sku: string;
  name: string;
  unit: string;
  barcode: string | null;
  locationId: string | null;
  locationCode: string | null;
  locationBarcode: string | null;
  quantityToPick: number;
  quantityPicked: number;
  quantityShort: number;
  shortageReason: string | null;
  /** #98: false when the shelf was deactivated (the line needs a re-allocation); default true. */
  locationIsActive?: boolean;
};

export type MockTask = {
  id: string;
  type: string;
  status: string;
  orderId: string;
  orderNumber: string;
  warehouseId: string;
  assignedTo: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  version: number;
  lines: MockTaskLine[];
  shipmentId: string | null;
  /** #98: low | normal | high | urgent. */
  priority: string;
  customerName: string | null;
  requestedDeliveryDate: string | null;
};

export type MockShipmentLine = {
  id: string;
  orderLineId: string;
  variantId: string;
  sku: string;
  name: string;
  /** Product barcode (#86): the pack check matches it without loading the task. */
  barcode: string | null;
  unit: string;
  locationId: string | null;
  locationCode: string | null;
  pickedQuantity: number;
  quantity: number;
  packedQuantity: number | null;
  shortQuantity: number;
  shortageReason: string | null;
};

export type MockShipment = {
  id: string;
  number: string;
  orderId: string;
  orderNumber: string;
  pickingTaskId: string | null;
  warehouseId: string;
  status: string;
  carrier: string | null;
  trackingNumber: string | null;
  packageCount: number | null;
  createdAt: string;
  packedAt: string | null;
  shippedAt: string | null;
  confirmedBy: string | null;
  version: number;
  lines: MockShipmentLine[];
  packages: { id: string; packageNo: number; items: { shipmentLineId: string; quantity: number }[] }[];
};

export type FulfilmentOverride = (route: Route, body: Json | null) => Promise<unknown> | unknown;

export type MockFulfilment = {
  userId: string;
  tasks: MockTask[];
  shipments: MockShipment[];
  /** Other shelves of the warehouse (code/barcode → id) that are not on any task line. */
  shelves: { id: string; code: string; barcode: string }[];
  overrides: Map<string, FulfilmentOverride>;
  idempotency: Map<string, { status: number; body: unknown }>;
  movements: Json[];
  /**
   * #98 re-allocation: free stock on the spare shelf (Z-99) that open quantity can move to; what does not fit stays
   * (unmovedQuantity). Default: plenty.
   */
  spareShelfStock: number;
  /** Orders a supervisor may create a pick task for (POST /picking-tasks); mock-backend wires it to the orders mock. */
  orderOf?: (orderId: string) => { number: string; status: string; customerName: string | null; requestedDeliveryDate: string | null } | null;
};

const uuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;
export const taskId = (n: number) => uuid("7a", n);
export const shipmentId = (n: number) => uuid("7b", n);
export const OTHER_USER = uuid("7c", 1);
const WAREHOUSE = uuid("9a", 1);
export const SHELVES = { a: uuid("7d", 1), b: uuid("7d", 2), z: uuid("7d", 9) };

export function makeLine(n: number, overrides: Partial<MockTaskLine> = {}): MockTaskLine {
  return {
    id: uuid("7e", n),
    sequence: n,
    orderLineId: uuid("7f", n),
    variantId: uuid("70", n),
    sku: `MOCK-00${n}`,
    name: `Mock ürün ${n}`,
    unit: "ADET",
    barcode: `869000000001${n}`,
    locationId: n === 1 ? SHELVES.a : SHELVES.b,
    locationCode: n === 1 ? "A-01" : "B-02",
    locationBarcode: n === 1 ? "LOC-A01" : "LOC-B02",
    quantityToPick: 2,
    quantityPicked: 0,
    quantityShort: 0,
    shortageReason: null,
    ...overrides,
  };
}

export function makeTask(n: number, overrides: Partial<MockTask> = {}): MockTask {
  return {
    id: taskId(n),
    type: "pick",
    status: "open",
    orderId: uuid("6a", n),
    orderNumber: `SIP-2026-${String(n).padStart(6, "0")}`,
    warehouseId: WAREHOUSE,
    assignedTo: null,
    createdAt: `2026-11-05T0${Math.min(n, 9)}:00:00Z`,
    startedAt: null,
    completedAt: null,
    version: 1,
    lines: [makeLine(1), makeLine(2, { quantityToPick: 1 })],
    shipmentId: null,
    priority: "normal",
    customerName: `Mock Bayi ${n}`,
    requestedDeliveryDate: null,
    ...overrides,
  };
}

export function makeShipment(task: MockTask, n: number, overrides: Partial<MockShipment> = {}): MockShipment {
  return {
    id: shipmentId(n),
    number: `${task.orderNumber}-S1`,
    orderId: task.orderId,
    orderNumber: task.orderNumber,
    pickingTaskId: task.id,
    warehouseId: task.warehouseId,
    status: "draft",
    carrier: null,
    trackingNumber: null,
    packageCount: null,
    createdAt: "2026-11-05T10:00:00Z",
    packedAt: null,
    shippedAt: null,
    confirmedBy: null,
    version: 1,
    lines: task.lines
      .filter((l) => l.quantityPicked > 0)
      .map((l, i) => ({
        id: uuid("71", n * 10 + i),
        orderLineId: l.orderLineId,
        variantId: l.variantId,
        sku: l.sku,
        name: l.name,
        barcode: l.barcode,
        unit: l.unit,
        locationId: l.locationId,
        locationCode: l.locationCode,
        pickedQuantity: l.quantityPicked,
        quantity: l.quantityPicked,
        packedQuantity: null,
        shortQuantity: 0,
        shortageReason: null,
      })),
    packages: [],
    ...overrides,
  };
}

export function createMockFulfilment(options: Partial<Pick<MockFulfilment, "tasks" | "shipments" | "userId">> = {}): MockFulfilment {
  return {
    userId: options.userId ?? "00000000-0000-4000-8000-000000000001",
    tasks: options.tasks ?? [],
    shipments: options.shipments ?? [],
    shelves: [{ id: SHELVES.z, code: "Z-99", barcode: "LOC-Z99" }],
    overrides: new Map(),
    idempotency: new Map(),
    movements: [],
    spareShelfStock: Number.POSITIVE_INFINITY,
  };
}

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, body, { ...HEADERS, ...headers });
const done = (p: Promise<unknown>) => p.then(() => true);
const MANAGERS = ["owner", "admin", "manager"];
const round = (n: number) => Math.round(n * 1000) / 1000;
const remainingOf = (l: MockTaskLine) => round(l.quantityToPick - l.quantityPicked - l.quantityShort);

/** 409 busy with Retry-After: 1, as the API sends it on a lock timeout (for overrides). */
export function fulfilmentBusyResponse(route: Route, code = "picking.busy") {
  return json(route, 409, problemBody(409, code), { "Retry-After": "1" });
}

// Simplified: sales (tasks.read.all K¹², tasks of its own orders) sees every task in the mock.
const PERMS: Record<string, string[]> = {
  readAll: [...MANAGERS, "viewer", "sales"],
  readOwn: [...MANAGERS, "warehouse_staff"],
  assign: MANAGERS,
  claim: ["manager", "warehouse_staff"],
  pick: [...MANAGERS, "warehouse_staff"],
  ship: [...MANAGERS, "warehouse_staff"],
};

/** Display names of the synthetic members (GET /members in mock-backend.ts uses the same ids). */
export const MOCK_MEMBER_NAMES: Record<string, string> = {
  "00000000-0000-4000-8000-000000000001": "Mock Kullanıcı",
  [uuid("7c", 1)]: "Deniz Depocu (mock)",
};

function summary(t: MockTask) {
  const sum = (k: "quantityToPick" | "quantityPicked" | "quantityShort") => round(t.lines.reduce((a, l) => a + l[k], 0));
  return {
    id: t.id,
    type: t.type,
    status: t.status,
    orderId: t.orderId,
    orderNumber: t.orderNumber,
    warehouseId: t.warehouseId,
    assignedTo: t.assignedTo,
    assigneeName: t.assignedTo ? (MOCK_MEMBER_NAMES[t.assignedTo] ?? null) : null,
    lineCount: t.lines.filter((l) => l.quantityToPick > 0).length,
    quantityToPick: sum("quantityToPick"),
    quantityPicked: sum("quantityPicked"),
    quantityShort: sum("quantityShort"),
    createdAt: t.createdAt,
    startedAt: t.startedAt,
    completedAt: t.completedAt,
    version: t.version,
    priority: t.priority,
    customerName: t.customerName,
    requestedDeliveryDate: t.requestedDeliveryDate,
  };
}

const lineBody = (l: MockTaskLine) => ({ ...l, locationIsActive: l.locationId ? (l.locationIsActive ?? true) : null, quantityRemaining: remainingOf(l) });
/**
 * Lines in walking order on their current shelves (shelf code, then sequence) as the API sends them; re-allocated lines
 * join their new shelf's place, emptied lines (nothing to pick) are hidden.
 */
const taskBody = (t: MockTask) => ({
  task: summary(t),
  lines: t.lines
    .filter((l) => l.quantityToPick > 0)
    .sort((a, b) => (a.locationCode ?? "").localeCompare(b.locationCode ?? "") || a.sequence - b.sequence)
    .map(lineBody),
  shipmentId: t.shipmentId,
});
const PRIORITIES = ["low", "normal", "high", "urgent"];

function shipmentSummary(s: MockShipment) {
  return {
    id: s.id,
    number: s.number,
    orderId: s.orderId,
    orderNumber: s.orderNumber,
    warehouseId: s.warehouseId,
    status: s.status,
    lineCount: s.lines.length,
    quantity: round(s.lines.reduce((a, l) => a + l.quantity, 0)),
    createdAt: s.createdAt,
    shippedAt: s.shippedAt,
    version: s.version,
  };
}

const active = (t: MockTask) => t.status === "open" || t.status === "in_progress";

/** Handles a fulfilment request; returns false when the path is not one of them. */
export async function handleFulfilment(route: Route, mock: MockFulfilment, role: string): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  const isMovements = path === "/api/v1/inventory/movements" && mock.shipments.some((s) => s.id === url.searchParams.get("sourceId"));
  if (!path.startsWith("/api/v1/picking-tasks") && !path.startsWith("/api/v1/shipments") && !isMovements) return false;
  const body = (request.postDataJSON?.() ?? null) as Json | null;

  const override = mock.overrides.get(`${method} ${path}`);
  if (override) {
    mock.overrides.delete(`${method} ${path}`);
    await override(route, body);
    return true;
  }

  if (isMovements) {
    const sourceId = url.searchParams.get("sourceId");
    const items = mock.movements.filter((m) => m.sourceId === sourceId);
    return done(json(route, 200, { items, page: { nextCursor: null, hasMore: false, limit: 50 } }));
  }

  const can = (p: string) => PERMS[p].includes(role);
  const forbidden = (p: string) => done(json(route, 403, problemBody(403, "auth.forbidden", { title: `Missing permission: ${p}` })));
  const notFound = () => done(json(route, 404, problemBody(404, "resource.not_found")));
  const key = request.headers()["idempotency-key"];
  const ifMatch = request.headers()["if-match"];
  if (method === "POST") {
    if (!key) return done(json(route, 428, problemBody(428, "precondition.required")));
    const replay = mock.idempotency.get(`${path} ${key}`);
    if (replay) return done(json(route, replay.status, replay.body, { "Idempotent-Replayed": "true" }));
  }
  const versionMismatch = (version: number) => ifMatch !== undefined && ifMatch !== `W/"${version}"`;
  const reply = (status: number, payload: unknown, version: number) => {
    if (method === "POST" && key) mock.idempotency.set(`${path} ${key}`, { status, body: payload });
    return done(json(route, status, payload, { ETag: `W/"${version}"` }));
  };
  const fail = (status: number, code: string, extra: Json = {}) => {
    const payload = problemBody(status, code, extra);
    if (method === "POST" && key && status !== 409) mock.idempotency.set(`${path} ${key}`, { status, body: payload });
    return done(json(route, status, payload));
  };
  const visible = (t: MockTask) => can("assign") || can("readAll") || t.assignedTo === mock.userId || (t.assignedTo === null && active(t));

  // ---- Picking tasks ----
  if (path === "/api/v1/picking-tasks/mine" && method === "GET") {
    if (!can("readOwn")) return forbidden("tasks.read.own");
    const scope = url.searchParams.get("scope") ?? "all";
    const items = mock.tasks.filter(
      (t) => active(t) && ((scope !== "pool" && t.assignedTo === mock.userId) || (scope !== "assigned" && t.assignedTo === null)),
    );
    return done(json(route, 200, { items: items.map(summary) }));
  }
  if (path === "/api/v1/picking-tasks" && method === "GET") {
    if (!can("readAll")) return forbidden("tasks.read.all");
    const statuses = (url.searchParams.get("status") ?? "").split(",").filter(Boolean);
    const priority = url.searchParams.get("priority");
    if (priority !== null && !PRIORITIES.includes(priority)) return done(json(route, 400, problemBody(400, "request.malformed")));
    const orderFilter = url.searchParams.get("orderId");
    const typeFilter = url.searchParams.get("type");
    const items = mock.tasks.filter(
      (t) =>
        (!statuses.length || statuses.includes(t.status)) &&
        (!priority || t.priority === priority) &&
        (!orderFilter || t.orderId === orderFilter) &&
        (!typeFilter || t.type === typeFilter),
    );
    return done(json(route, 200, { items: items.map(summary), page: { nextCursor: null, hasMore: false, limit: 50 } }));
  }
  if (path === "/api/v1/picking-tasks" && method === "POST") {
    // #98: a supervisor creates the pick task of an approved order (auto-create off).
    if (!can("assign")) return forbidden("tasks.assign");
    const priority = (body?.priority as string | null | undefined) ?? "normal";
    if (!PRIORITIES.includes(priority)) return fail(422, "validation.failed", { errors: { priority: ["Must be one of: low, normal, high, urgent."] } });
    const orderIdValue = String(body?.orderId ?? "");
    const order = mock.orderOf?.(orderIdValue) ?? null;
    if (!order) return fail(404, "resource.not_found");
    if (!["approved", "picking", "packed"].includes(order.status)) return fail(409, "picking.order_not_fulfillable");
    if (mock.tasks.some((t) => t.orderId === orderIdValue && t.type === "pick" && active(t))) return fail(409, "picking.fulfillment_in_progress");
    const n = mock.tasks.length + 50;
    const task = makeTask(n, {
      orderId: orderIdValue,
      orderNumber: order.number,
      customerName: order.customerName,
      requestedDeliveryDate: order.requestedDeliveryDate,
      priority,
      createdAt: "2026-11-09T09:00:00Z",
      lines: [makeLine(1, { id: uuid("7e", n * 10 + 1) }), makeLine(2, { id: uuid("7e", n * 10 + 2), quantityToPick: 1 })],
    });
    mock.tasks.push(task);
    return reply(201, taskBody(task), task.version);
  }
  const taskMatch = /^\/api\/v1\/picking-tasks\/([^/]+)(?:\/(.+))?$/.exec(path);
  if (taskMatch) {
    const task = mock.tasks.find((t) => t.id === taskMatch[1]);
    const action = taskMatch[2] ?? "";
    // GET accepts tasks.read.own or tasks.read.all (x-permission-any-of); work needs tasks.read.own.
    if (!can("readOwn") && !(method === "GET" && can("readAll"))) return forbidden("tasks.read.own");
    if (!task || !visible(task)) return notFound();
    if (method === "GET" && action === "") return done(json(route, 200, taskBody(task), { ETag: `W/"${task.version}"` }));
    if (method !== "POST") return notFound();
    if (versionMismatch(task.version)) return fail(412, "concurrency.version_mismatch");
    const touch = () => {
      task.version++;
    };
    const ownWork = () => can("assign") || task.assignedTo === mock.userId;

    if (action === "claim") {
      if (!can("claim")) return forbidden("tasks.claim");
      if (task.assignedTo !== null) return fail(409, "picking.already_assigned");
      task.assignedTo = mock.userId;
      touch();
      return reply(200, taskBody(task), task.version);
    }
    if (action === "assign" || action === "unassign") {
      if (!can("assign")) return forbidden("tasks.assign");
      if (!active(task)) return fail(409, "picking.invalid_transition");
      task.assignedTo = action === "assign" ? String(body?.userId) : null;
      touch();
      return reply(200, taskBody(task), task.version);
    }
    if (action === "priority") {
      if (!can("assign")) return forbidden("tasks.assign");
      const priority = String(body?.priority ?? "");
      if (!PRIORITIES.includes(priority)) return fail(422, "validation.failed", { errors: { priority: ["Must be one of: low, normal, high, urgent."] } });
      if (!active(task)) return fail(409, "picking.invalid_transition");
      if (task.priority !== priority) {
        task.priority = priority;
        touch();
      }
      return reply(200, taskBody(task), task.version);
    }
    if (action === "reallocate") {
      // #98: open quantity on inactive shelves (or on fromLocationId) moves to the spare shelf Z-99 as far as its free
      // stock goes; picked / short quantities never move; emptied lines stay with nothing to pick (hidden).
      if (!can("assign")) return forbidden("tasks.assign");
      if (task.type !== "pick" || !active(task)) return fail(409, "picking.invalid_transition");
      const from = (body?.fromLocationId as string | null | undefined) ?? null;
      const known = [...task.lines.map((l) => l.locationId), ...mock.shelves.map((s) => s.id)];
      if (from && !known.includes(from)) return fail(422, "validation.failed", { errors: { fromLocationId: ["Location not found in this task's warehouse."] } });
      const spare = mock.shelves[0];
      let moved = 0;
      let unmoved = 0;
      for (const line of [...task.lines]) {
        const open = remainingOf(line);
        const source = from ? line.locationId === from : line.locationId !== null && line.locationIsActive === false;
        if (!source || open <= 0 || line.locationId === spare.id) continue;
        const take = Math.min(open, mock.spareShelfStock);
        unmoved = round(unmoved + open - take);
        if (take <= 0) continue;
        mock.spareShelfStock = round(mock.spareShelfStock - take);
        moved = round(moved + take);
        line.quantityToPick = round(line.quantityToPick - take);
        const target = task.lines.find((l) => l.orderLineId === line.orderLineId && l.locationId === spare.id);
        if (target) target.quantityToPick = round(target.quantityToPick + take);
        else
          task.lines.push({
            ...line,
            id: uuid("7e", 900 + task.lines.length),
            sequence: task.lines.length + 1,
            locationId: spare.id,
            locationCode: spare.code,
            locationBarcode: spare.barcode,
            locationIsActive: true,
            quantityToPick: take,
            quantityPicked: 0,
            quantityShort: 0,
            shortageReason: null,
          });
      }
      if (moved > 0) touch();
      return reply(200, { task: taskBody(task), movedQuantity: moved, unmovedQuantity: unmoved }, task.version);
    }
    if (!can("pick")) return forbidden("tasks.pick");
    if (!ownWork()) return fail(403, "picking.not_assignee");
    if (!active(task)) return fail(409, "picking.invalid_transition");
    const begin = () => {
      if (task.status === "open") {
        task.status = "in_progress";
        task.startedAt = "2026-11-05T09:00:00Z";
      }
    };
    if (action === "start") {
      begin();
      touch();
      return reply(200, taskBody(task), task.version);
    }
    if (action === "scans") {
      const code = String(body?.code ?? "");
      const location = (body?.location as string | null) ?? null;
      const quantity = Number(body?.quantity ?? 1);
      const variantLines = task.lines.filter((l) => l.barcode === code || l.sku.toUpperCase() === code.toUpperCase());
      const known = variantLines.length > 0 || code.startsWith("MOCK-") || code.startsWith("8690");
      if (!known) return fail(422, "picking.unknown_code");
      let candidates = variantLines;
      if (candidates.length === 0) return fail(422, "picking.wrong_item");
      if (candidates.some((l) => l.locationId)) {
        if (!location) return fail(422, "picking.location_required");
        const upper = location.toUpperCase();
        const shelf =
          task.lines.find((l) => l.locationBarcode === location || l.locationCode === upper)?.locationId ??
          mock.shelves.find((s) => s.barcode === location || s.code === upper)?.id;
        if (!shelf) return fail(422, "picking.unknown_location");
        candidates = candidates.filter((l) => l.locationId === shelf);
        if (candidates.length === 0) return fail(422, "picking.wrong_location");
      }
      const open = round(candidates.reduce((a, l) => a + remainingOf(l), 0));
      if (quantity > open) return fail(422, "picking.over_pick");
      let left = quantity;
      const changed: MockTaskLine[] = [];
      for (const l of candidates) {
        const take = Math.min(left, remainingOf(l));
        if (take <= 0) continue;
        l.quantityPicked = round(l.quantityPicked + take);
        changed.push(l);
        left = round(left - take);
        if (left === 0) break;
      }
      begin();
      touch();
      return reply(
        200,
        {
          taskId: task.id,
          status: task.status,
          version: task.version,
          lines: changed.map((l) => ({ id: l.id, variantId: l.variantId, sku: l.sku, locationId: l.locationId, quantityToPick: l.quantityToPick, quantityPicked: l.quantityPicked, quantityShort: l.quantityShort, quantityRemaining: remainingOf(l) })),
          openLineCount: task.lines.filter((l) => remainingOf(l) > 0).length,
        },
        task.version,
      );
    }
    const shortage = /^lines\/([^/]+)\/shortage$/.exec(action);
    if (shortage) {
      const line = task.lines.find((l) => l.id === shortage[1]);
      if (!line) return fail(422, "validation.failed", { errors: { lineId: ["Line not found in this task."] } });
      const open = remainingOf(line);
      if (open <= 0) return fail(409, "picking.line_closed");
      const quantity = body?.quantity === null || body?.quantity === undefined ? open : Number(body.quantity);
      if (quantity > open) return fail(422, "picking.shortage_exceeds_open");
      line.quantityShort = round(line.quantityShort + quantity);
      line.shortageReason = String(body?.reason);
      begin();
      touch();
      return reply(200, taskBody(task), task.version);
    }
    if (action === "complete") {
      if (task.lines.some((l) => remainingOf(l) > 0)) return fail(409, "picking.incomplete");
      task.status = task.lines.some((l) => l.quantityShort > 0) ? "completed_with_shortage" : "completed";
      task.completedAt = "2026-11-05T09:30:00Z";
      if (task.lines.some((l) => l.quantityPicked > 0)) {
        const shipment = makeShipment(task, mock.shipments.length + 1);
        mock.shipments.push(shipment);
        task.shipmentId = shipment.id;
      }
      touch();
      return reply(200, taskBody(task), task.version);
    }
    return notFound();
  }

  // ---- Shipments ----
  if (!can("readOwn") && !(method === "GET" && can("readAll"))) return forbidden("tasks.read.own");
  const shipmentVisible = (s: MockShipment) => can("assign") || can("readAll") || mock.tasks.find((t) => t.id === s.pickingTaskId)?.assignedTo === mock.userId;
  if (path === "/api/v1/shipments" && method === "GET") {
    const statuses = (url.searchParams.get("status") ?? "").split(",").filter(Boolean);
    const items = mock.shipments.filter((s) => shipmentVisible(s) && (!statuses.length || statuses.includes(s.status)));
    return done(json(route, 200, { items: items.map(shipmentSummary), page: { nextCursor: null, hasMore: false, limit: 50 } }));
  }
  const shipMatch = /^\/api\/v1\/shipments\/([^/]+)(?:\/(.+))?$/.exec(path);
  if (!shipMatch) return notFound();
  const shipment = mock.shipments.find((s) => s.id === shipMatch[1]);
  if (!shipment || !shipmentVisible(shipment)) return notFound();
  const action = shipMatch[2] ?? "";
  if (method === "GET" && action === "") return done(json(route, 200, shipment, { ETag: `W/"${shipment.version}"` }));
  if (method !== "POST") return notFound();
  if (action === "confirm") {
    if (!can("ship")) return forbidden("orders.ship");
    if (shipment.status === "confirmed") return fail(409, "shipment.already_confirmed");
  }
  if (versionMismatch(shipment.version)) return fail(412, "concurrency.version_mismatch");
  if (action === "cancel") {
    // tasks.assign; draft or packed only; no stock moves, a putaway task for what was picked.
    if (!can("assign")) return forbidden("tasks.assign");
    if (shipment.status !== "draft" && shipment.status !== "packed") return fail(409, "shipment.invalid_transition");
    const reason = typeof body?.reason === "string" ? body.reason.trim() : "";
    if (!reason) return fail(422, "validation.failed", { errors: { reason: ["Required."] } });
    shipment.status = "cancelled";
    shipment.version++;
    return reply(200, { shipment, putawayTaskId: uuid("7e", 1) }, shipment.version);
  }
  if (action === "pack") {
    if (shipment.status !== "draft") return fail(409, "shipment.invalid_transition");
    const packed = new Map<string, number>();
    const packages = (body?.packages as { items: { shipmentLineId: string; quantity: number }[] }[]) ?? [];
    for (const p of packages) for (const i of p.items) packed.set(i.shipmentLineId, round((packed.get(i.shipmentLineId) ?? 0) + Number(i.quantity)));
    const mismatch = (pred: (l: MockShipmentLine) => boolean) =>
      Object.fromEntries(shipment.lines.filter(pred).map((l) => [l.id, [`${l.sku}: ships ${l.quantity}, packed ${packed.get(l.id) ?? 0}.`]]));
    const over = mismatch((l) => (packed.get(l.id) ?? 0) > l.quantity);
    if (Object.keys(over).length) return fail(422, "packing.over_pack", { errors: over });
    const under = mismatch((l) => (packed.get(l.id) ?? 0) < l.quantity);
    if (Object.keys(under).length) return fail(422, "packing.quantity_mismatch", { errors: under });
    shipment.packages = packages.map((p, i) => ({ id: uuid("72", i + 1), packageNo: i + 1, items: p.items }));
    shipment.lines.forEach((l) => (l.packedQuantity = packed.get(l.id) ?? 0));
    shipment.packageCount = packages.length;
    shipment.status = "packed";
    shipment.packedAt = "2026-11-05T10:30:00Z";
    shipment.version++;
    return reply(200, shipment, shipment.version);
  }
  const shortage = /^lines\/([^/]+)\/shortage$/.exec(action);
  if (shortage) {
    if (shipment.status !== "draft") return fail(409, "shipment.invalid_transition");
    const line = shipment.lines.find((l) => l.id === shortage[1]);
    if (!line) return notFound();
    const quantity = body?.quantity === null || body?.quantity === undefined ? line.quantity : Number(body.quantity);
    if (quantity > line.quantity) return fail(422, "picking.shortage_exceeds_open");
    line.quantity = round(line.quantity - quantity);
    line.shortQuantity = round(line.shortQuantity + quantity);
    line.shortageReason = String(body?.reason);
    shipment.version++;
    return reply(200, shipment, shipment.version);
  }
  if (action === "confirm") {
    if (shipment.status !== "draft" && shipment.status !== "packed") return fail(409, "shipment.invalid_transition");
    shipment.status = "confirmed";
    shipment.carrier = (body?.carrier as string | null) ?? null;
    shipment.trackingNumber = (body?.trackingNumber as string | null) ?? null;
    shipment.packageCount = (body?.packageCount as number | null) ?? shipment.packageCount;
    shipment.shippedAt = "2026-11-05T11:00:00Z";
    shipment.confirmedBy = mock.userId;
    shipment.version++;
    const movementIds: string[] = [];
    shipment.lines
      .filter((l) => l.quantity > 0)
      .forEach((l, i) => {
        const id = uuid("73", mock.movements.length + i + 1);
        movementIds.push(id);
        mock.movements.push({
          id,
          variantId: l.variantId,
          warehouseId: shipment.warehouseId,
          locationId: l.locationId,
          type: "shipment_out",
          quantity: -l.quantity,
          unitCost: null,
          sourceType: "shipment",
          sourceId: shipment.id,
          sourceLineId: l.id,
          reason: null,
          balanceAfter: 0,
          correlationId: null,
          occurredAt: "2026-11-05T11:00:00Z",
          createdBy: mock.userId,
        });
      });
    return reply(200, { shipment, orderStatus: "shipped", orderFulfillmentStatus: "full", movementIds }, shipment.version);
  }
  return notFound();
}

/**
 * MOCK STOCK TRANSFERS, STOCK COUNTS AND SHELF MOVES FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API.
 *
 * Stand-in for /api/v1/stock-transfers, /api/v1/stock-counts and POST /api/v1/inventory/relocations (#168), following
 * the contract shapes in packages/contracts/openapi.json and the as-built rules of services/api (Warehouse module):
 * two-stage transfer (draft → in_transit "Yolda" → partially_received → received | closed, cancel only as draft), the
 * in-transit leg booked with `warehouseId = sourceId = transfer id`, receipts by line id or by scanned code + shelf
 * code, 422 transfer.over_receipt / unknown_code / wrong_item / unknown_location, close as lost or return_to_source;
 * counts with snapshot, blind mode (system quantities null without inventory.count.manage), set/add entries, recount,
 * apply with 409 count.lines_moved until `acceptMovedLines`, count_correction movements. ETag/If-Match → 412,
 * Idempotency-Key replay. It writes into the shared MockInventory (balances, movements). A simplified model, not the
 * real validation. All data is invented.
 */
import type { Route } from "@playwright/test";
import { grantsForRoleName, type FixtureRules } from "../../src/test/me-fixtures";
import { fulfillJson, problemBody, type MockCatalog } from "./mock-catalog";
import type { MockBalance, MockInventory, MockMovement } from "./mock-inventory";

type Json = Record<string, unknown>;

export type MockTransferLine = {
  id: string;
  lineNo: number;
  variantId: string;
  sku: string;
  sourceLocationId: string | null;
  destinationLocationId: string | null;
  quantity: number;
  receivedQuantity: number;
  returnedQuantity: number;
  lostQuantity: number;
};

export type MockTransfer = {
  id: string;
  number: string;
  sourceWarehouseId: string;
  destinationWarehouseId: string;
  status: string;
  note: string | null;
  lines: MockTransferLine[];
  isOverdue: boolean;
  dispatchedAt: string | null;
  lastReceivedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  closeResolution: string | null;
  closeReason: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  version: number;
};

export type MockCountLine = {
  id: string;
  variantId: string;
  sku: string;
  locationId: string | null;
  expectedQuantity: number;
  countedQuantity: number | null;
  snapshotAt: string;
  countedAt: string | null;
  status: string;
  movedSinceSnapshot: boolean;
  appliedDelta: number | null;
};

export type MockCount = {
  id: string;
  number: string;
  warehouseId: string;
  scope: string;
  locationIds: string[];
  variantIds: string[];
  isBlind: boolean;
  status: string;
  note: string | null;
  lines: MockCountLine[];
  snapshotAt: string;
  submittedAt: string | null;
  appliedAt: string | null;
  createdAt: string;
  cancelledAt: string | null;
  cancelReason: string | null;
  version: number;
};

export type StockDocumentOverride = (route: Route, body: Json | null) => Promise<unknown> | unknown;

export type MockStockDocuments = {
  transfers: MockTransfer[];
  counts: MockCount[];
  /** One-shot overrides keyed "METHOD /path". */
  overrides: Map<string, StockDocumentOverride>;
  idempotency: Map<string, { status: number; body: unknown; etag?: string }>;
};

export function createMockStockDocuments(options: { transfers?: MockTransfer[]; counts?: MockCount[] } = {}): MockStockDocuments {
  return { transfers: options.transfers ?? [], counts: options.counts ?? [], overrides: new Map(), idempotency: new Map() };
}

/** Ids end in 8 hex digits, so the number is "TRF-" + those, as the API derives it. */
const docId = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;
const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const etag = (version: number) => `W/"${version}"`;
const versionOf = (ifMatch: string) => Number(ifMatch.replace(/^W\//, "").replace(/"/g, ""));
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, body, { ...HEADERS, ...headers });
const round = (n: number) => Math.round(n * 1000) / 1000;
const now = () => new Date().toISOString();

const outstanding = (l: MockTransferLine) => round(l.quantity - l.receivedQuantity - l.returnedQuantity - l.lostQuantity);

function balanceRow(inv: MockInventory, variantId: string, warehouseId: string, locationId: string | null): MockBalance {
  let row = inv.balances.find((b) => b.variantId === variantId && b.warehouseId === warehouseId && b.locationId === locationId);
  if (!row) {
    row = { id: docId("9c", 700 + inv.balances.length), variantId, warehouseId, locationId, onHand: 0, reserved: 0, available: 0, version: 0, updatedAt: null };
    inv.balances.push(row);
  }
  return row;
}

/** Writes one movement; `warehouseId` may be a transfer id (the in-transit point, no balance row in the list). */
function move(inv: MockInventory, input: { variantId: string; warehouseId: string; locationId: string | null; type: string; quantity: number; sourceType: string; sourceId: string; transferNumber?: string; reason: string | null; balance?: MockBalance; transitAfter?: number }): MockMovement {
  let after = input.transitAfter ?? 0;
  if (input.balance) {
    input.balance.onHand = round(input.balance.onHand + input.quantity);
    input.balance.available = round(input.balance.onHand - input.balance.reserved);
    input.balance.version += 1;
    input.balance.updatedAt = now();
    after = input.balance.onHand;
  }
  const movement: MockMovement = {
    id: docId("9d", 500 + inv.movements.length),
    variantId: input.variantId,
    warehouseId: input.warehouseId,
    locationId: input.locationId,
    type: input.type,
    quantity: input.quantity,
    unitCost: null,
    sourceType: input.sourceType,
    sourceId: input.sourceId,
    sourceLineId: null,
    reason: input.reason,
    balanceAfter: after,
    correlationId: null,
    occurredAt: new Date(Date.UTC(2026, 10, 9) + inv.movements.length * 60_000).toISOString(),
    createdBy: null,
    // #98: the API flags the in-transit point (transfer id in the warehouse slot) and names two-stage transfers.
    isTransit: input.sourceType === "transfer" && input.warehouseId === input.sourceId,
    transferNumber: input.transferNumber ?? null,
  };
  inv.movements.push(movement);
  return movement;
}

function transferBody(inv: MockInventory, t: MockTransfer) {
  const name = (id: string) => inv.warehouses.find((w) => w.id === id)?.name ?? null;
  const code = (id: string | null) => (id ? (inv.locations.find((l) => l.id === id)?.code ?? null) : null);
  const dispatched = t.status !== "draft" && t.status !== "cancelled";
  const lines = t.lines.map((l) => ({
    id: l.id,
    lineNo: l.lineNo,
    variantId: l.variantId,
    sku: l.sku,
    sourceLocationId: l.sourceLocationId,
    sourceLocationCode: code(l.sourceLocationId),
    destinationLocationId: l.destinationLocationId,
    destinationLocationCode: code(l.destinationLocationId),
    quantity: l.quantity,
    receivedQuantity: l.receivedQuantity,
    inTransitQuantity: dispatched ? outstanding(l) : 0,
    returnedQuantity: l.returnedQuantity,
    lostQuantity: l.lostQuantity,
    discrepancyQuantity: dispatched ? round(l.quantity - l.receivedQuantity) : 0,
  }));
  const sum = (pick: (x: (typeof lines)[number]) => number) => round(lines.reduce((s, x) => s + pick(x), 0));
  const discrepancy = sum((x) => x.discrepancyQuantity);
  return {
    transfer: {
      id: t.id,
      number: t.number,
      sourceWarehouseId: t.sourceWarehouseId,
      sourceWarehouseName: name(t.sourceWarehouseId),
      destinationWarehouseId: t.destinationWarehouseId,
      destinationWarehouseName: name(t.destinationWarehouseId),
      status: t.status,
      note: t.note,
      lineCount: lines.length,
      quantity: sum((x) => x.quantity),
      receivedQuantity: sum((x) => x.receivedQuantity),
      inTransitQuantity: sum((x) => x.inTransitQuantity),
      returnedQuantity: sum((x) => x.returnedQuantity),
      lostQuantity: sum((x) => x.lostQuantity),
      discrepancyQuantity: discrepancy,
      hasDiscrepancy: (t.status === "partially_received" || t.status === "received" || t.status === "closed") && discrepancy > 0,
      isOverdue: t.isOverdue,
      dispatchedAt: t.dispatchedAt,
      lastReceivedAt: t.lastReceivedAt,
      completedAt: t.completedAt,
      createdAt: t.createdAt,
      version: t.version,
    },
    createdBy: null,
    dispatchedBy: null,
    closeResolution: t.closeResolution,
    closeReason: t.closeReason,
    closedBy: null,
    cancelledAt: t.cancelledAt,
    cancelReason: t.cancelReason,
    lines,
  };
}

function countBody(inv: MockInventory, c: MockCount, manager: boolean) {
  const hidden = c.isBlind && !manager;
  const lines = c.lines.map((l) => ({
    id: l.id,
    variantId: l.variantId,
    sku: l.sku,
    locationId: l.locationId,
    locationCode: l.locationId ? (inv.locations.find((x) => x.id === l.locationId)?.code ?? null) : null,
    expectedQuantity: hidden ? null : l.expectedQuantity,
    countedQuantity: l.countedQuantity,
    difference: hidden || l.countedQuantity === null ? null : round(l.countedQuantity - l.expectedQuantity),
    snapshotAt: l.snapshotAt,
    countedAt: l.countedAt,
    status: l.status,
    movedSinceSnapshot: manager && l.movedSinceSnapshot,
    appliedDelta: l.appliedDelta,
  }));
  const counted = c.lines.filter((l) => l.countedQuantity !== null);
  return {
    count: {
      id: c.id,
      number: c.number,
      warehouseId: c.warehouseId,
      warehouseName: inv.warehouses.find((w) => w.id === c.warehouseId)?.name ?? null,
      scope: c.scope,
      isBlind: c.isBlind,
      quantitiesHidden: hidden,
      status: c.status,
      note: c.note,
      lineCount: c.lines.length,
      countedLineCount: counted.length,
      snapshotAt: c.snapshotAt,
      submittedAt: c.submittedAt,
      appliedAt: c.appliedAt,
      createdAt: c.createdAt,
      version: c.version,
    },
    locationIds: c.locationIds,
    variantIds: c.variantIds,
    differenceCount: manager ? counted.filter((l) => l.countedQuantity !== l.expectedQuantity).length : null,
    movedCount: manager ? counted.filter((l) => l.movedSinceSnapshot).length : null,
    createdBy: null,
    submittedBy: null,
    appliedBy: null,
    cancelledAt: c.cancelledAt,
    cancelReason: c.cancelReason,
    lines,
  };
}

function page<T>(items: T[], params: URLSearchParams) {
  const limit = Number(params.get("limit") ?? 50);
  const offset = Number((params.get("cursor") ?? "o:0").slice(2));
  const hasMore = offset + limit < items.length;
  return { items: items.slice(offset, offset + limit), page: { nextCursor: hasMore ? `o:${offset + limit}` : null, hasMore, limit } };
}

/** Handles transfers, counts and relocations; false when the path is none of them. */
export async function handleStockDocuments(
  route: Route,
  docs: MockStockDocuments,
  inv: MockInventory,
  catalog: MockCatalog | null,
  role: string,
  rules: Partial<FixtureRules>,
): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  const isTransfer = path.startsWith("/api/v1/stock-transfers");
  const isCount = path.startsWith("/api/v1/stock-counts");
  const isRelocation = path === "/api/v1/inventory/relocations";
  if (!isTransfer && !isCount && !isRelocation) return false;
  const body = (request.postDataJSON?.() ?? null) as Json | null;

  const override = docs.overrides.get(`${method} ${path}`);
  if (override) {
    docs.overrides.delete(`${method} ${path}`);
    await override(route, body);
    return true;
  }

  const grants = grantsForRoleName(role, rules);
  const has = (p: string) => grants.some((g) => g.permission === p && g.scope === "all");
  const any = (...ps: string[]) => ps.some((p) => grants.some((g) => g.permission === p));
  const forbid = (p: string) => json(route, 403, problemBody(403, "auth.forbidden", { title: `Missing permission: ${p}` })).then(() => true);
  const variants = (catalog?.products ?? []).flatMap((p) => p.variants);
  const resolve = (code: string) => variants.find((v) => v.barcode === code)?.id ?? variants.find((v) => v.sku === code)?.id ?? null;
  const shelfByCode = (warehouseId: string, code: string) =>
    inv.locations.find((l) => l.warehouseId === warehouseId && (l.code.toUpperCase() === code.toUpperCase() || l.barcode === code))?.id ?? null;

  const key = request.headers()["idempotency-key"];
  const ifMatch = request.headers()["if-match"];
  if (method === "POST") {
    if (!key) return json(route, 400, problemBody(400, "idempotency.key_required")).then(() => true);
    const replay = docs.idempotency.get(`${path} ${key}`);
    if (replay) return json(route, replay.status, replay.body, { "Idempotent-Replayed": "true", ...(replay.etag ? { ETag: replay.etag } : {}) }).then(() => true);
  }
  const succeed = async (status: number, payload: unknown, version?: number) => {
    const tag = version === undefined ? undefined : etag(version);
    if (key) docs.idempotency.set(`${path} ${key}`, { status, body: payload, etag: tag });
    await json(route, status, payload, tag ? { ETag: tag } : {});
    return true;
  };
  const invalid = (errors: Record<string, string[]>) => json(route, 422, problemBody(422, "validation.failed", { errors })).then(() => true);
  const fail = (status: number, code: string, extra: Json = {}) => json(route, status, problemBody(status, code, extra)).then(() => true);

  // ---- Shelf-to-shelf move ----
  if (isRelocation && method === "POST") {
    // #98: own permission (warehouse staff included); includeReserved also moves reserved stock still on the shelf
    // (this mock treats every reserved unit as still on the shelf, held for one synthetic order).
    if (!has("inventory.relocate")) return forbid("inventory.relocate");
    const warehouseId = String(body?.warehouseId ?? "");
    const lines = (body?.lines as Json[] | undefined) ?? [];
    const errors: Record<string, string[]> = {};
    lines.forEach((l, i) => {
      if (l.fromLocationId === l.toLocationId) errors[`lines[${i}].to`] = ["Source and target must differ."];
    });
    if (Object.keys(errors).length > 0) return invalid(errors);
    for (const l of lines) {
      const row = balanceRow(inv, String(l.variantId), warehouseId, String(l.fromLocationId));
      const offered = row.available + (l.includeReserved === true ? row.reserved : 0);
      if (offered < Number(l.quantity)) {
        const sku = variants.find((v) => v.id === l.variantId)?.sku ?? null;
        return fail(409, "inventory.insufficient_available", {
          shortages: [{ variantId: l.variantId, sku, warehouseId, locationId: l.fromLocationId, requested: Number(l.quantity), available: offered }],
        });
      }
    }
    const transferId = docId("9f", 800 + inv.movements.length);
    const reservationMoves: Json[] = [];
    const movements = lines.flatMap((l, i) => {
      const q = Number(l.quantity);
      const from = balanceRow(inv, String(l.variantId), warehouseId, String(l.fromLocationId));
      const to = balanceRow(inv, String(l.variantId), warehouseId, String(l.toLocationId));
      const reserved = round(Math.max(0, q - from.available));
      if (reserved > 0) {
        from.reserved = round(from.reserved - reserved);
        to.reserved = round(to.reserved + reserved);
        reservationMoves.push({
          orderId: docId("6a", 1),
          orderLineId: docId("6b", i + 1),
          variantId: l.variantId,
          warehouseId,
          fromLocationId: l.fromLocationId,
          toLocationId: l.toLocationId,
          quantity: reserved,
          fromReservationId: docId("6c", i + 1),
          toReservationId: docId("6d", i + 1),
        });
      }
      return [
        move(inv, { variantId: String(l.variantId), warehouseId, locationId: from.locationId, type: "transfer_out", quantity: -q, sourceType: "transfer", sourceId: transferId, reason: (body?.reference as string) ?? null, balance: from }),
        move(inv, { variantId: String(l.variantId), warehouseId, locationId: to.locationId, type: "transfer_in", quantity: q, sourceType: "transfer", sourceId: transferId, reason: (body?.reference as string) ?? null, balance: to }),
      ];
    });
    return succeed(201, { transferId, movements, reservationMoves });
  }

  // ---- Transfers ----
  if (isTransfer) {
    if (!any("inventory.transfer", "inventory.transfer.receive")) return forbid("inventory.transfer");
    if (path === "/api/v1/stock-transfers" && method === "GET") {
      const statuses = (url.searchParams.get("status") ?? "").split(",").filter(Boolean);
      const wh = url.searchParams.get("warehouseId");
      const items = [...docs.transfers]
        .reverse()
        .filter((t) => statuses.length === 0 || statuses.includes(t.status))
        .filter((t) => !wh || t.sourceWarehouseId === wh || t.destinationWarehouseId === wh)
        .map((t) => transferBody(inv, t).transfer);
      return json(route, 200, page(items, url.searchParams)).then(() => true);
    }
    if (path === "/api/v1/stock-transfers" && method === "POST") {
      if (!has("inventory.transfer")) return forbid("inventory.transfer");
      const source = String(body?.sourceWarehouseId ?? "");
      const destination = String(body?.destinationWarehouseId ?? "");
      if (source === destination) return invalid({ destinationWarehouseId: ["Source and destination warehouses must differ."] });
      const lines = (body?.lines as Json[] | undefined) ?? [];
      if (lines.length === 0) return invalid({ lines: ["Between 1 and 500 lines are required."] });
      const n = docs.transfers.length + 1;
      const id = docId("7a", 0x1a2b0000 + n);
      const t: MockTransfer = {
        id,
        number: `TRF-${id.replace(/-/g, "").slice(-8).toUpperCase()}`,
        sourceWarehouseId: source,
        destinationWarehouseId: destination,
        status: "draft",
        note: (body?.note as string) ?? null,
        lines: lines.map((l, i) => ({
          id: docId("7b", n * 100 + i + 1),
          lineNo: i + 1,
          variantId: String(l.variantId),
          sku: variants.find((v) => v.id === l.variantId)?.sku ?? "?",
          sourceLocationId: (l.sourceLocationId as string) ?? null,
          destinationLocationId: (l.destinationLocationId as string) ?? null,
          quantity: Number(l.quantity),
          receivedQuantity: 0,
          returnedQuantity: 0,
          lostQuantity: 0,
        })),
        isOverdue: false,
        dispatchedAt: null,
        lastReceivedAt: null,
        completedAt: null,
        createdAt: now(),
        closeResolution: null,
        closeReason: null,
        cancelledAt: null,
        cancelReason: null,
        version: 1,
      };
      docs.transfers.push(t);
      return succeed(201, transferBody(inv, t), t.version);
    }
    const m = path.match(/^\/api\/v1\/stock-transfers\/([^/]+)(?:\/(dispatch|receive|close|cancel))?$/);
    const t = m ? docs.transfers.find((x) => x.id === m[1]) : undefined;
    if (!m || !t) return fail(404, "resource.not_found");
    if (!m[2] && method === "GET") return json(route, 200, transferBody(inv, t), { ETag: etag(t.version) }).then(() => true);
    if (method !== "POST") return false;
    const action = m[2];
    if (action === "receive" ? !has("inventory.transfer.receive") : !has("inventory.transfer")) return forbid(action === "receive" ? "inventory.transfer.receive" : "inventory.transfer");
    if (ifMatch && versionOf(ifMatch) !== t.version) return fail(412, "concurrency.version_mismatch");
    const done = (movements: MockMovement[]) => {
      t.version += 1;
      return succeed(200, { transfer: transferBody(inv, t), movementIds: movements.map((x) => x.id) }, t.version);
    };
    const open = t.status === "in_transit" || t.status === "partially_received";
    const reason = `Transfer ${t.number}`;

    if (action === "cancel") {
      if (t.status !== "draft") return fail(409, "transfer.invalid_transition");
      t.status = "cancelled";
      t.cancelledAt = now();
      t.cancelReason = String(body?.reason ?? "");
      return done([]);
    }
    if (action === "dispatch") {
      if (t.status !== "draft") return fail(409, "transfer.invalid_transition");
      const shortages = t.lines
        .map((l) => ({ l, row: balanceRow(inv, l.variantId, t.sourceWarehouseId, l.sourceLocationId) }))
        .filter(({ l, row }) => row.available < l.quantity)
        .map(({ l, row }) => ({ variantId: l.variantId, sku: l.sku, warehouseId: t.sourceWarehouseId, locationId: l.sourceLocationId, requested: l.quantity, available: row.available }));
      if (shortages.length > 0) return fail(409, "inventory.insufficient_available", { shortages });
      const movements = t.lines.flatMap((l) => [
        move(inv, { variantId: l.variantId, warehouseId: t.sourceWarehouseId, locationId: l.sourceLocationId, type: "transfer_out", quantity: -l.quantity, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason, balance: balanceRow(inv, l.variantId, t.sourceWarehouseId, l.sourceLocationId) }),
        move(inv, { variantId: l.variantId, warehouseId: t.id, locationId: null, type: "transfer_in", quantity: l.quantity, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason, transitAfter: l.quantity }),
      ]);
      t.status = "in_transit";
      t.dispatchedAt = now();
      return done(movements);
    }
    if (action === "receive") {
      if (!open) return fail(409, "transfer.invalid_transition");
      const destination = inv.warehouses.find((w) => w.id === t.destinationWarehouseId)!;
      const entries = (body?.lines as Json[] | undefined) ?? [];
      const plan: { line: MockTransferLine; quantity: number; shelf: string | null }[] = [];
      for (const [i, e] of entries.entries()) {
        let targets: MockTransferLine[];
        if (e.lineId) targets = t.lines.filter((l) => l.id === e.lineId);
        else {
          const variantId = e.code ? resolve(String(e.code)) : String(e.variantId ?? "");
          if (!variantId) return fail(422, "transfer.unknown_code");
          targets = t.lines.filter((l) => l.variantId === variantId);
          if (targets.length === 0) return fail(422, "transfer.wrong_item");
        }
        let shelf: string | null = (e.locationId as string | null) ?? null;
        if (e.locationCode) {
          shelf = shelfByCode(destination.id, String(e.locationCode));
          if (!shelf) return fail(422, "transfer.unknown_location");
        }
        let left = Number(e.quantity);
        for (const line of targets) {
          const already = plan.filter((p) => p.line === line).reduce((s, p) => s + p.quantity, 0);
          const take = Math.min(left, round(outstanding(line) - already));
          if (take <= 0) continue;
          const target = destination.usesLocations ? (shelf ?? line.destinationLocationId) : null;
          if (destination.usesLocations && !target) return invalid({ [`lines[${i}].locationId`]: ["A destination location is required in this warehouse."] });
          plan.push({ line, quantity: take, shelf: target });
          left = round(left - take);
          if (left === 0) break;
        }
        if (left > 0) return fail(422, "transfer.over_receipt", { errors: { [`lines[${i}]`]: [`Only ${round(Number(e.quantity) - left)} of ${e.quantity} can still be received.`] } });
      }
      const movements = plan.flatMap(({ line, quantity, shelf }) => {
        line.receivedQuantity = round(line.receivedQuantity + quantity);
        return [
          move(inv, { variantId: line.variantId, warehouseId: t.id, locationId: null, type: "transfer_out", quantity: -quantity, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason, transitAfter: outstanding(line) }),
          move(inv, { variantId: line.variantId, warehouseId: destination.id, locationId: shelf, type: "transfer_in", quantity, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason, balance: balanceRow(inv, line.variantId, destination.id, shelf) }),
        ];
      });
      const left = t.lines.reduce((s, l) => s + outstanding(l), 0);
      t.status = left > 0 ? "partially_received" : "received";
      t.lastReceivedAt = now();
      if (left === 0) t.completedAt = now();
      return done(movements);
    }
    if (action === "close") {
      if (!open) return fail(409, "transfer.invalid_transition");
      const resolution = String(body?.resolution ?? "");
      if (resolution !== "lost" && resolution !== "return_to_source") return invalid({ resolution: ["Must be one of: lost, return_to_source."] });
      if (!String(body?.reason ?? "").trim()) return invalid({ reason: ["Required, at most 500 characters."] });
      const movements = t.lines
        .filter((l) => outstanding(l) > 0)
        .flatMap((l) => {
          const q = outstanding(l);
          if (resolution === "lost") {
            l.lostQuantity = round(l.lostQuantity + q);
            return [move(inv, { variantId: l.variantId, warehouseId: t.id, locationId: null, type: "adjustment_out", quantity: -q, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason: `${reason}: ${String(body?.reason)}` })];
          }
          l.returnedQuantity = round(l.returnedQuantity + q);
          return [
            move(inv, { variantId: l.variantId, warehouseId: t.id, locationId: null, type: "transfer_out", quantity: -q, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason }),
            move(inv, { variantId: l.variantId, warehouseId: t.sourceWarehouseId, locationId: l.sourceLocationId, type: "transfer_in", quantity: q, sourceType: "transfer", sourceId: t.id, transferNumber: t.number, reason, balance: balanceRow(inv, l.variantId, t.sourceWarehouseId, l.sourceLocationId) }),
          ];
        });
      t.status = "closed";
      t.closeResolution = resolution;
      t.closeReason = String(body?.reason);
      t.completedAt = now();
      return done(movements);
    }
    return false;
  }

  // ---- Stock counts ----
  if (!any("inventory.count.manage", "inventory.count.perform")) return forbid("inventory.count.perform");
  const manager = has("inventory.count.manage");
  if (path === "/api/v1/stock-counts" && method === "GET") {
    const statuses = (url.searchParams.get("status") ?? "").split(",").filter(Boolean);
    const wh = url.searchParams.get("warehouseId");
    const items = [...docs.counts]
      .reverse()
      .filter((c) => statuses.length === 0 || statuses.includes(c.status))
      .filter((c) => !wh || c.warehouseId === wh)
      .map((c) => countBody(inv, c, manager).count);
    return json(route, 200, page(items, url.searchParams)).then(() => true);
  }
  if (path === "/api/v1/stock-counts" && method === "POST") {
    if (!manager) return forbid("inventory.count.manage");
    const warehouseId = String(body?.warehouseId ?? "");
    if (docs.counts.some((c) => c.warehouseId === warehouseId && (c.status === "open" || c.status === "submitted"))) return fail(409, "count.already_active");
    const scope = String(body?.scope ?? "full");
    const locationIds = (body?.locationIds as string[] | null) ?? [];
    const variantIds = (body?.variantIds as string[] | null) ?? [];
    const snapshotAt = now();
    const n = docs.counts.length + 1;
    const rows = inv.balances
      .filter((b) => b.warehouseId === warehouseId && (b.onHand !== 0 || b.reserved !== 0))
      .filter((b) => scope !== "locations" || (b.locationId !== null && locationIds.includes(b.locationId)))
      .filter((b) => scope !== "variants" || variantIds.includes(b.variantId));
    const c: MockCount = {
      id: docId("8a", 0x5a000000 + n),
      number: "",
      warehouseId,
      scope,
      locationIds,
      variantIds,
      isBlind: body?.blind === true,
      status: "open",
      note: (body?.note as string) ?? null,
      lines: rows.map((b, i) => ({
        id: docId("8b", n * 1000 + i + 1),
        variantId: b.variantId,
        sku: variants.find((v) => v.id === b.variantId)?.sku ?? "?",
        locationId: b.locationId,
        expectedQuantity: b.onHand,
        countedQuantity: null,
        snapshotAt,
        countedAt: null,
        status: "pending",
        movedSinceSnapshot: false,
        appliedDelta: null,
      })),
      snapshotAt,
      submittedAt: null,
      appliedAt: null,
      createdAt: snapshotAt,
      cancelledAt: null,
      cancelReason: null,
      version: 1,
    };
    c.number = `SAY-${c.id.replace(/-/g, "").slice(-8).toUpperCase()}`;
    docs.counts.push(c);
    return succeed(201, countBody(inv, c, manager), c.version);
  }
  const m = path.match(/^\/api\/v1\/stock-counts\/([^/]+)(?:\/(entries|submit|recount|apply|cancel))?$/);
  const c = m ? docs.counts.find((x) => x.id === m[1]) : undefined;
  if (!m || !c) return fail(404, "resource.not_found");
  if (!m[2] && method === "GET") return json(route, 200, countBody(inv, c, manager), { ETag: etag(c.version) }).then(() => true);
  if (method !== "POST") return false;
  const action = m[2];
  if ((action === "entries" || action === "submit") && !has("inventory.count.perform")) return forbid("inventory.count.perform");
  if (action !== "entries" && action !== "submit" && !manager) return forbid("inventory.count.manage");
  if (ifMatch && versionOf(ifMatch) !== c.version) return fail(412, "concurrency.version_mismatch");
  const done = (extra?: (payload: ReturnType<typeof countBody>) => unknown) => {
    c.version += 1;
    const payload = countBody(inv, c, manager);
    return succeed(200, extra ? extra(payload) : payload, c.version);
  };

  if (action === "entries") {
    if (c.status !== "open") return fail(409, "count.invalid_transition");
    for (const [i, e] of ((body?.entries as Json[] | undefined) ?? []).entries()) {
      let line: MockCountLine | undefined;
      if (e.lineId) line = c.lines.find((l) => l.id === e.lineId);
      else {
        const variantId = e.code ? resolve(String(e.code)) : String(e.variantId ?? "");
        if (!variantId) return fail(422, "count.unknown_code");
        let shelf: string | null = (e.locationId as string) ?? null;
        if (e.locationCode) {
          shelf = shelfByCode(c.warehouseId, String(e.locationCode));
          if (!shelf) return fail(422, "count.unknown_location");
        }
        line = c.lines.find((l) => l.variantId === variantId && l.locationId === shelf);
        if (!line) {
          if (c.scope === "variants" && !c.variantIds.includes(variantId)) return fail(422, "count.out_of_scope");
          line = { id: docId("8c", c.lines.length + 1), variantId, sku: variants.find((v) => v.id === variantId)?.sku ?? "?", locationId: shelf, expectedQuantity: 0, countedQuantity: null, snapshotAt: now(), countedAt: null, status: "pending", movedSinceSnapshot: false, appliedDelta: null };
          c.lines.push(line);
        }
      }
      if (!line) return invalid({ [`entries[${i}].lineId`]: ["Line not found on this count."] });
      const q = Number(e.quantity);
      line.countedQuantity = e.mode === "add" ? round((line.countedQuantity ?? 0) + q) : q;
      line.countedAt = new Date(Date.UTC(2026, 10, 9, 9) + c.version * 1000).toISOString();
      line.status = "counted";
    }
    return done();
  }
  if (action === "submit") {
    if (c.status !== "open") return fail(409, "count.invalid_transition");
    if (c.lines.every((l) => l.countedQuantity === null)) return fail(409, "count.nothing_counted");
    c.status = "submitted";
    c.submittedAt = now();
    return done();
  }
  if (action === "recount") {
    if (c.status !== "open" && c.status !== "submitted") return fail(409, "count.invalid_transition");
    for (const id of (body?.lineIds as string[] | undefined) ?? []) {
      const line = c.lines.find((l) => l.id === id);
      if (line) Object.assign(line, { countedQuantity: null, countedAt: null, status: "pending" });
    }
    c.status = "open";
    c.submittedAt = null;
    return done();
  }
  if (action === "cancel") {
    if (c.status !== "open" && c.status !== "submitted") return fail(409, "count.invalid_transition");
    c.status = "cancelled";
    c.cancelledAt = now();
    c.cancelReason = String(body?.reason ?? "");
    return done();
  }
  if (action === "apply") {
    if (c.status !== "submitted") return fail(409, "count.invalid_transition");
    const counted = c.lines.filter((l) => l.countedQuantity !== null);
    const moved = counted.filter((l) => l.movedSinceSnapshot);
    if (moved.length > 0 && body?.acceptMovedLines !== true) {
      return fail(409, "count.lines_moved", { errors: Object.fromEntries(moved.map((l) => [l.id, [`${l.sku}: stock moved after the snapshot.`]])) });
    }
    const below = counted
      .map((l) => ({ l, row: balanceRow(inv, l.variantId, c.warehouseId, l.locationId) }))
      .filter(({ l, row }) => l.countedQuantity! !== l.expectedQuantity && l.countedQuantity! < row.reserved)
      .map(({ l, row }) => ({ variantId: l.variantId, sku: l.sku, warehouseId: c.warehouseId, locationId: l.locationId, requested: row.reserved, available: l.countedQuantity }));
    if (below.length > 0) return fail(409, "count.below_reserved", { shortages: below });
    const movements: MockMovement[] = [];
    for (const line of c.lines) {
      if (line.countedQuantity === null) {
        line.status = "uncounted";
        continue;
      }
      const delta = round(line.countedQuantity - line.expectedQuantity);
      line.appliedDelta = delta;
      line.status = "applied";
      if (delta !== 0) {
        movements.push(move(inv, { variantId: line.variantId, warehouseId: c.warehouseId, locationId: line.locationId, type: "count_correction", quantity: delta, sourceType: "stock_count", sourceId: c.id, reason: `Sayım ${c.number}`, balance: balanceRow(inv, line.variantId, c.warehouseId, line.locationId) }));
      }
    }
    c.status = "applied";
    c.appliedAt = now();
    return done((payload) => ({ count: payload, movementIds: movements.map((x) => x.id) }));
  }
  return false;
}

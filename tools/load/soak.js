// Stock-reservation race soak (#58, test-strategy §4 #7 "200 iterations"). Each iteration, on a fresh product:
//   1. receive S units (1..5) through a goods receipt (ledger),
//   2. create + submit K orders (1..2 units each, Σ demand > S),
//   3. RACE A: approve all K orders in parallel (http.batch) — reservations must never exceed S,
//   4. RACE B: in parallel, cancel half of the approved orders and approve the refused ones again,
//   5. check through the API: balance on_hand = S, reserved = Σ quantities of the orders now approved, available ≥ 0.
// Any violation increments `oversell_violations` (threshold: 0). Afterwards `stack.sh verify` checks the whole ledger.
//
//   ITERATIONS=200 RACERS=8 bash tools/load/run.sh soak
import http from "k6/http";
import { check } from "k6";
import { Counter } from "k6/metrics";
import { BASE_URL, etagOf, json, loadUsers, must, problemCode, seedTenant } from "./lib.js";

const ITERATIONS = Number(__ENV.ITERATIONS || 200);
const RACERS = Number(__ENV.RACERS || 8);

const violations = new Counter("oversell_violations");
const approvedTotal = new Counter("race_approvals");
const refusedTotal = new Counter("race_refusals");
const otherFailures = new Counter("race_unexpected");

export const options = {
  setupTimeout: "15m",
  batchPerHost: 32,
  summaryTrendStats: ["avg", "med", "p(95)", "p(99)", "max"],
  scenarios: { soak: { executor: "per-vu-iterations", vus: 1, iterations: ITERATIONS, maxDuration: "2h" } },
  thresholds: { oversell_violations: ["count==0"], race_unexpected: ["count==0"], checks: ["rate==1"] },
};

export function setup() {
  const users = loadUsers();
  // One product per iteration, no stock yet: each race starts from a known, empty balance.
  const world = seedTenant(users, { slug: `load-soak-${Date.now()}`, products: ITERATIONS, hot: 0, customers: 10, stock: false });
  console.log(`soak tenant ${world.tenantId}: ${ITERATIONS} products, ${RACERS} racers per iteration`);
  return world;
}

function headers(user, tenantId, ifMatch) {
  return {
    "Content-Type": "application/json",
    Accept: "application/json",
    Authorization: `Bearer ${user.token}`,
    "X-Tenant-Id": tenantId,
    "Idempotency-Key": crypto.randomUUID(),
    "If-Match": ifMatch,
  };
}

const actionRequest = (user, tenantId, order, action, body) => ({
  method: "POST",
  url: `${BASE_URL}/api/v1/orders/${order.id}/${action}`,
  body: JSON.stringify(body),
  params: {
    headers: headers(user, tenantId, etagOf(order.version)),
    tags: { name: `POST /api/v1/orders/{id}/${action} (race)` },
    responseCallback: http.expectedStatuses(200, 409, 422),
  },
});

export default function (world) {
  const i = __ITER;
  const t = { tenantId: world.tenantId };
  const { owner, sales, manager } = world.users;
  const variant = world.variants[i];
  const stock = 1 + (i % 5);
  must(owner, "POST", "/api/v1/inventory/receipts", {
    warehouseId: world.warehouseId, reference: `SOAK-${i}`, lines: [{ variantId: variant.id, locationId: null, quantity: stock, unitCost: 5 }],
  }, t);

  const orders = [];
  for (let k = 0; k < RACERS; k++) {
    const quantity = 1 + ((i + k) % 2);
    let detail = must(sales, "POST", "/api/v1/orders", {
      customerId: world.customerIds[k % world.customerIds.length], warehouseId: world.warehouseId, source: "manual",
      sourceReferenceId: null, currency: null, pricesIncludeTax: null, requestedDeliveryDate: null, notes: `soak ${i}/${k}`,
      lines: [{ id: null, variantId: variant.id, quantity, unitPrice: null, discountPercent: null, discountAmount: null }],
    }, t);
    detail = must(sales, "POST", `/api/v1/orders/${detail.order.id}/submit`, { confirmNegativeStock: false }, { ...t, ifMatch: etagOf(detail.order.version) });
    orders.push({ id: detail.order.id, version: detail.order.version, quantity, approved: false });
  }

  // RACE A: every approval at once.
  const raceA = http.batch(orders.map((o) => actionRequest(manager, world.tenantId, o, "approve", { confirmNegativeStock: false })));
  raceA.forEach((r, k) => settle(orders[k], r, "approve"));

  // RACE B: release (cancel half of the approved) while the refused ones try again.
  const approvedNow = orders.filter((o) => o.approved);
  const toCancel = approvedNow.filter((_, k) => k % 2 === 0);
  const retry = orders.filter((o) => !o.approved && !o.failed);
  const batch = [
    ...toCancel.map((o) => ({ o, action: "cancel", req: actionRequest(manager, world.tenantId, o, "cancel", { reason: "soak release" }) })),
    ...retry.map((o) => ({ o, action: "approve", req: actionRequest(manager, world.tenantId, o, "approve", { confirmNegativeStock: false }) })),
  ];
  const raceB = http.batch(batch.map((b) => b.req));
  raceB.forEach((r, k) => settle(batch[k].o, r, batch[k].action));

  const reservedExpected = orders.filter((o) => o.approved).reduce((sum, o) => sum + o.quantity, 0);
  const balances = must(owner, "GET", `/api/v1/inventory/balances?variantId=${variant.id}`, null, t).items;
  const onHand = balances.reduce((s, b) => s + Number(b.onHand), 0);
  const reserved = balances.reduce((s, b) => s + Number(b.reserved), 0);
  const ok = check(null, {
    "on_hand = received": () => onHand === stock,
    "reserved = Σ approved": () => reserved === reservedExpected,
    "never oversold": () => reserved <= onHand && reservedExpected <= stock,
  });
  if (!ok) {
    violations.add(1);
    console.error(`iteration ${i}: stock ${stock} on_hand ${onHand} reserved ${reserved} expected ${reservedExpected} ${JSON.stringify(orders)}`);
  }
}

function settle(order, response, action) {
  if (response.status === 200) {
    const detail = json(response);
    order.version = detail.order.version;
    order.approved = action === "approve";
    if (action === "approve") approvedTotal.add(1);
    if (action === "cancel") order.cancelled = true;
    return;
  }
  const code = problemCode(response);
  if (action === "approve" && code.startsWith("inventory.insufficient")) {
    refusedTotal.add(1);
    return;
  }
  order.failed = true;
  otherFailures.add(1, { action, status: String(response.status), code });
  console.error(`${action} ${order.id} -> ${response.status} ${code} ${String(response.body).slice(0, 200)}`);
}

export function handleSummary(data) {
  const m = data.metrics;
  const count = (name) => (m[name] ? m[name].values.count : 0);
  const text = [
    "",
    `iterations: ${count("iterations")}  racers/iteration: ${RACERS}`,
    `race approvals: ${count("race_approvals")}  refused (insufficient stock): ${count("race_refusals")}`,
    `oversell violations: ${count("oversell_violations")}  unexpected race responses: ${count("race_unexpected")}`,
    `checks: ${(m.checks.values.rate * 100).toFixed(2)} %  http p95: ${m.http_req_duration.values["p(95)"].toFixed(1)} ms  p99: ${m.http_req_duration.values["p(99)"].toFixed(1)} ms`,
    "",
  ].join("\n");
  return { [__ENV.SUMMARY || "/state/soak-summary.json"]: JSON.stringify(data, null, 2), stdout: text };
}

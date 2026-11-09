// Realistic mixed load (#58): catalog search, order create/submit/approve with reservation contention, full picking
// + shipment, reports. Open model (arrival rate) stepped through plateaus to find p95/p99 and error rates per step.
//
//   STEPS="5,10,20,40" STEP_SECONDS=60 bash tools/load/run.sh mix
//
// Per-step results are tagged `step:<total it/s>`; ramp windows between plateaus are tagged `step:ramp` and ignored.
import http from "k6/http";
import { check } from "k6";
import exec from "k6/execution";
import { Trend } from "k6/metrics";
import { call, createAndApprove, fulfil, json, loadUsers, pick, seedTenant } from "./lib.js";

const STEPS = (__ENV.STEPS || "5,10,20,40").split(",").map(Number);
const STEP_SECONDS = Number(__ENV.STEP_SECONDS || 60);
const RAMP_SECONDS = Number(__ENV.RAMP_SECONDS || 10);
// Share of the total arrival rate per flow (iterations, not requests).
const MIX = { browse: 0.5, order: 0.3, fulfil: 0.1, reports: 0.1 };

const flowDuration = new Trend("flow_duration", true);

function stages(share) {
  const out = [];
  for (const total of STEPS) {
    const rate = Math.max(1, Math.round(total * share * 10)); // per 10 s, so small shares still get traffic
    out.push({ target: rate, duration: `${RAMP_SECONDS}s` }, { target: rate, duration: `${STEP_SECONDS}s` });
  }
  return out;
}

function scenario(fn, share) {
  const maxVUs = Math.ceil(Math.max(...STEPS) * share * 30) + 10;
  return {
    executor: "ramping-arrival-rate",
    exec: fn,
    startRate: 0,
    timeUnit: "10s",
    preAllocatedVUs: Math.min(maxVUs, 50),
    maxVUs,
    stages: stages(share),
  };
}

// Real gates: no response the flows did not expect, no failed HTTP request, every check passes. Latency is reported,
// not gated (machine-dependent); the step table is the result.
const stepThresholds = {
  unexpected_responses: ["count==0"],
  http_req_failed: ["rate==0"],
  checks: ["rate==1"],
};
for (const total of STEPS) {
  // Placeholder thresholds make k6 report each step as its own sub-metric in the summary.
  stepThresholds[`http_req_duration{step:${total}}`] = ["max>=0"];
  stepThresholds[`http_req_failed{step:${total}}`] = ["rate>=0"];
  stepThresholds[`http_reqs{step:${total}}`] = ["count>=0"];
  for (const flow of Object.keys(MIX)) stepThresholds[`flow_duration{step:${total},flow:${flow}}`] = ["max>=0"];
}

export const options = {
  setupTimeout: "10m",
  discardResponseBodies: false,
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
  scenarios: {
    browse: scenario("browse", MIX.browse),
    order: scenario("order", MIX.order),
    fulfil: scenario("fulfilFlow", MIX.fulfil),
    reports: scenario("reports", MIX.reports),
  },
  thresholds: stepThresholds,
};

/** Which plateau the test is in (by elapsed time); "ramp" between plateaus. */
function currentStep() {
  const elapsed = (Date.now() - exec.scenario.startTime) / 1000;
  const period = RAMP_SECONDS + STEP_SECONDS;
  const index = Math.floor(elapsed / period);
  if (index >= STEPS.length) return String(STEPS[STEPS.length - 1]);
  return elapsed - index * period < RAMP_SECONDS ? "ramp" : String(STEPS[index]);
}

function tagged(flow, body) {
  const step = currentStep();
  exec.vu.metrics.tags.step = step;
  exec.vu.metrics.tags.flow = flow;
  const start = Date.now();
  body();
  flowDuration.add(Date.now() - start, { step, flow });
}

export function setup() {
  const users = loadUsers();
  const world = seedTenant(users, { slug: `load-mix-${Date.now()}`, products: Number(__ENV.PRODUCTS || 300), hot: 10, hotStock: 40 });
  console.log(`seeded tenant ${world.tenantId}: ${world.variants.length} products, ${world.customerIds.length} customers`);
  return world;
}

export function browse(world) {
  tagged("browse", () => {
    const t = { tenantId: world.tenantId };
    const v = pick(world.variants);
    const products = call(world.users.sales, "GET", `/api/v1/products?q=${encodeURIComponent(v.word)}&limit=50`, null, t);
    check(products, { "search ok": (r) => r.status === 200 && json(r).items.length > 0 });
    const lookup = call(world.users.staff, "GET", `/api/v1/variants/lookup?barcode=${v.barcode}`, null, t);
    check(lookup, { "barcode lookup ok": (r) => r.status === 200 });
    call(world.users.sales, "GET", `/api/v1/inventory/balances?q=${encodeURIComponent(v.sku)}&limit=50`, null, t);
  });
}

export function order(world) {
  tagged("order", () => {
    const hot = world.variants.filter((v) => v.hot);
    const cold = world.variants.filter((v) => !v.hot);
    // Half of the orders compete for the 10 low-stock products: approvals contend for the same balance rows.
    const lines = Math.random() < 0.5 ? [[pick(hot).id, 1 + Math.floor(Math.random() * 3)], [pick(cold).id, 2]] : [[pick(cold).id, 3], [pick(cold).id, 1]];
    if (lines.length === 2 && lines[0][0] === lines[1][0]) lines.pop();
    createAndApprove(world, lines);
  });
}

export function fulfilFlow(world) {
  tagged("fulfil", () => {
    const cold = world.variants.filter((v) => !v.hot);
    const first = pick(cold);
    let second = pick(cold);
    while (second.id === first.id) second = pick(cold);
    const result = createAndApprove(world, [[first.id, 2], [second.id, 1]]);
    if (result.approved) fulfil(world, result.detail);
  });
}

export function reports(world) {
  tagged("reports", () => {
    const t = { tenantId: world.tenantId };
    const today = new Date().toISOString().slice(0, 10);
    const from = new Date(Date.now() - 7 * 86400000).toISOString().slice(0, 10);
    const user = world.users.manager;
    const which = Math.floor(Math.random() * 4);
    if (which === 0) call(user, "GET", `/api/v1/reports/orders?from=${from}&to=${today}`, null, t);
    if (which === 1) call(user, "GET", "/api/v1/reports/critical-stock?limit=100", null, t);
    if (which === 2) call(user, "GET", `/api/v1/reports/top-products?from=${from}&to=${today}&limit=20`, null, t);
    if (which === 3) call(user, "GET", `/api/v1/reports/stock-movements?from=${from}&to=${today}`, null, t);
  });
}

export function handleSummary(data) {
  const out = __ENV.SUMMARY || "/state/mix-summary.json";
  return { [out]: JSON.stringify(data, null, 2), stdout: textSummary(data) };
}

function textSummary(data) {
  const m = data.metrics;
  const lines = ["", "step(it/s) | reqs | req/s | p50 ms | p95 ms | p99 ms | max ms | failed %"];
  for (const total of STEPS) {
    const d = m[`http_req_duration{step:${total}}`];
    const f = m[`http_req_failed{step:${total}}`];
    const r = m[`http_reqs{step:${total}}`];
    if (!d) continue;
    const v = d.values;
    lines.push(`${total} | ${r.values.count} | ${(r.values.count / STEP_SECONDS).toFixed(1)} | ${v.med.toFixed(1)} | ${v["p(95)"].toFixed(1)} | ${v["p(99)"].toFixed(1)} | ${v.max.toFixed(1)} | ${(f.values.rate * 100).toFixed(2)}`);
  }
  for (const name of ["business_rejections", "busy_retries", "unexpected_responses"]) {
    lines.push(`${name}: ${m[name] ? m[name].values.count : 0}`);
  }
  return lines.join("\n") + "\n";
}

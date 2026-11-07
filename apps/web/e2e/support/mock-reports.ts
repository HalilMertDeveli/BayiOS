/**
 * MOCK REPORTS API FOR PLAYWRIGHT — SYNTHETIC NUMBERS, NOT THE REAL API.
 *
 * Stand-in for /api/v1/reports/* following the contract shapes in packages/contracts/openapi.json and api-strategy
 * §12.13: reports.read / reports.export (sales: order reports only → 403 reports.orders_and_customers_only;
 * warehouse_staff → 403 auth.forbidden); AI usage needs billing.read; `amountsVisible` = unconditional prices.read.sell;
 * date range default last 30 days, max 366 (422); critical stock keyset paging (limit, cursor). Exports: POST with
 * Idempotency-Key, file body + Content-Disposition, hourly limit → 429 + Retry-After, too many rows → 422
 * reports.export_too_large. Like the real API (Program.cs CORS), Content-Disposition is NOT exposed to the browser
 * unless `exposeContentDisposition` is set. All numbers and names are invented.
 */
import type { Route } from "@playwright/test";
import { grantsForRoleName, type FixtureRules } from "../../src/test/me-fixtures";

export type MockReportId = "critical-stock" | "orders" | "top-products" | "stock-movements" | "picking-tasks" | "ai-usage";

export type MockReports = {
  /** Critical stock rows (all of them; the mock pages them). */
  criticalCount: number;
  /** Reports that answer with no data. */
  empty: Set<MockReportId>;
  /** Exports allowed before 429 (the real limit is 10 per hour per tenant). */
  exportLimit: number;
  exportsDone: number;
  retryAfterSeconds: number;
  /** Exports that answer 422 reports.export_too_large. */
  tooLarge: Set<MockReportId>;
  exposeContentDisposition: boolean;
  /** Answers amountsVisible: false whatever the role (the real API: no unconditional prices.read.sell). */
  hideAmounts: boolean;
  /** Critical stock (#137): no variant has a minimum stock threshold (thresholdsConfigured: false). */
  noThresholds?: boolean;
  /** Fails the next read of these reports with 500 (error state). */
  failNext: Set<MockReportId>;
  requests: { method: string; path: string; search: string; headers: Record<string, string>; body: unknown }[];
};

export function createMockReports(options: Partial<Omit<MockReports, "requests" | "exportsDone">> = {}): MockReports {
  return {
    criticalCount: options.criticalCount ?? 130,
    empty: options.empty ?? new Set(),
    exportLimit: options.exportLimit ?? 10,
    exportsDone: 0,
    retryAfterSeconds: options.retryAfterSeconds ?? 1200,
    tooLarge: options.tooLarge ?? new Set(),
    exposeContentDisposition: options.exposeContentDisposition ?? false,
    hideAmounts: options.hideAmounts ?? false,
    failNext: options.failNext ?? new Set(),
    noThresholds: options.noThresholds ?? false,
    requests: [],
  };
}

const DAY_MS = 24 * 60 * 60 * 1000;
const istanbulToday = () => new Date(Date.now() + 3 * 60 * 60 * 1000).toISOString().slice(0, 10);
const addDays = (day: string, n: number) => new Date(Date.parse(`${day}T00:00:00Z`) + n * DAY_MS).toISOString().slice(0, 10);
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const uuid = (prefix: string, n: number) => `00000000-0000-4000-8000-${prefix}${String(n).padStart(12 - prefix.length, "0")}`;

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? "application/problem+json" : "application/json",
    headers: { ...HEADERS, ...headers },
    body: JSON.stringify(body),
  });
const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => ({
  type: `https://bayios.app/problems/${code}`,
  title: code,
  status,
  code,
  ...extra,
});

type Period = { from: string; to: string };

function period(from: string | null | undefined, to: string | null | undefined): Period | { error: Record<string, string[]> } {
  const today = istanbulToday();
  if ((from && !DATE.test(from)) || (to && !DATE.test(to))) return { error: { from: ["from and to must be dates (yyyy-MM-dd)"] } };
  const end = to ?? (from ? addDays(from, 29) : today);
  const start = from ?? addDays(end, -29);
  if (start > end) return { error: { from: ["from must not be after to"] } };
  const days = Math.round((Date.parse(end) - Date.parse(start)) / DAY_MS) + 1;
  if (days > 366) return { error: { to: ["The range may cover at most 366 days"] } };
  return { from: start, to: end };
}

/** The last `n` days of the period that fall on or before today (synthetic activity). */
function activeDays(p: Period, n: number): string[] {
  const days: string[] = [];
  for (let d = p.to; d >= p.from && days.length < n; d = addDays(d, -1)) days.push(d);
  return days.reverse();
}

function criticalItems(count: number) {
  return Array.from({ length: count }, (_, i) => {
    const onHand = i % 4;
    const reserved = i % 3 === 0 ? 1 : 0;
    return {
      id: uuid("c5", i + 1),
      productId: uuid("c6", i + 1),
      sku: `KRT-${String(i + 1).padStart(4, "0")}`,
      name: `Kritik ürün ${i + 1} (mock)`,
      unit: "ADET",
      onHand,
      reserved,
      available: onHand - reserved,
      minQuantity: 3, // #137: each variant's own minimum (thresholds mode)
    };
  });
}

function build(id: MockReportId, reports: MockReports, input: Record<string, unknown>, amountsVisible: boolean): unknown {
  const empty = reports.empty.has(id);
  const p = input.period as Period;
  if (id === "critical-stock") {
    const all = empty ? [] : criticalItems(reports.criticalCount);
    const limit = Number(input.limit ?? 100);
    const after = typeof input.cursor === "string" ? Buffer.from(input.cursor, "base64url").toString() : null;
    const start = after ? all.findIndex((x) => x.sku > after) : 0;
    const items = start < 0 ? [] : all.slice(start, start + limit);
    const hasMore = start >= 0 && start + limit < all.length;
    const fixed = input.threshold !== undefined && input.threshold !== null && input.threshold !== "";
    return {
      // #137: no threshold = each variant against its own minimum ("thresholds").
      mode: fixed ? "fixed" : "thresholds",
      threshold: fixed ? Number(input.threshold) : null,
      includeOutOfStock: !fixed && (input.includeOutOfStock === true || input.includeOutOfStock === "true"),
      thresholdsConfigured: !reports.noThresholds,
      warehouseId: input.warehouseId ?? null,
      items,
      page: { nextCursor: hasMore ? Buffer.from(items[items.length - 1].sku).toString("base64url") : null, hasMore, limit },
    };
  }
  const days = empty ? [] : activeDays(p, 4);
  if (id === "orders") {
    const statuses = (input.statuses as string[] | undefined)?.length ? (input.statuses as string[]) : ["pending_approval", "approved", "shipped"];
    const dayRows = days.flatMap((day, i) =>
      statuses.map((status, j) => ({ day, status, currency: "TRY", orderCount: i + j + 1, grandTotal: amountsVisible ? (i + j + 1) * 1250.5 : null })),
    );
    const byStatus = statuses
      .map((status) => {
        const rows = dayRows.filter((r) => r.status === status);
        const orderCount = rows.reduce((s, r) => s + r.orderCount, 0);
        return { status, currency: "TRY", orderCount, grandTotal: amountsVisible ? orderCount * 1250.5 : null };
      })
      .filter((r) => r.orderCount > 0);
    return { from: p.from, to: p.to, amountsVisible, byStatus, days: dayRows };
  }
  if (id === "top-products") {
    const limit = Number(input.limit ?? 20);
    const items = empty
      ? []
      : Array.from({ length: Math.min(limit, 12) }, (_, i) => ({
          rank: i + 1,
          id: uuid("d7", i + 1),
          sku: `TOP-${String(i + 1).padStart(3, "0")}`,
          name: `Çok satan ürün ${i + 1} (mock)`,
          unit: "ADET",
          currency: "TRY",
          quantity: 120 - i * 7,
          orderCount: 30 - i,
          revenue: amountsVisible ? (120 - i * 7) * 45.75 : null,
        }));
    return { from: p.from, to: p.to, amountsVisible, items };
  }
  if (id === "stock-movements") {
    const types = ["receipt", "shipment_out", "adjustment_in"];
    const dayRows = days.flatMap((day, i) =>
      types.map((type, j) => ({
        day,
        type,
        movementCount: i + j + 1,
        quantityIn: type === "shipment_out" ? 0 : (i + 1) * 10 + j,
        quantityOut: type === "shipment_out" ? (i + 1) * 4 : 0,
      })),
    );
    const totals = types
      .map((type) => {
        const rows = dayRows.filter((r) => r.type === type);
        return {
          type,
          movementCount: rows.reduce((s, r) => s + r.movementCount, 0),
          quantityIn: rows.reduce((s, r) => s + r.quantityIn, 0),
          quantityOut: rows.reduce((s, r) => s + r.quantityOut, 0),
        };
      })
      .filter((r) => r.movementCount > 0);
    return { from: p.from, to: p.to, warehouseId: input.warehouseId ?? null, totals, days: dayRows };
  }
  if (id === "picking-tasks") {
    const dayRows = days.map((day, i) => ({ day, created: 5 + i, completed: 4 + i, completedWithShortage: i % 2, cancelled: i === 0 ? 1 : 0, averagePickMinutes: 12.5 + i }));
    const sum = (k: "created" | "completed" | "completedWithShortage" | "cancelled") => dayRows.reduce((s, r) => s + r[k], 0);
    return {
      from: p.from,
      to: p.to,
      warehouseId: input.warehouseId ?? null,
      totals: {
        created: sum("created"),
        completed: sum("completed"),
        completedWithShortage: sum("completedWithShortage"),
        cancelled: sum("cancelled"),
        averagePickMinutes: dayRows.length ? 14 : null,
      },
      days: dayRows,
    };
  }
  const dayRows = days.map((day, i) => ({
    day,
    parseRequests: 3 + i,
    aiCalls: 3 + i,
    aiFailures: i === 1 ? 1 : 0,
    fallbacks: i === 1 ? 1 : 0,
    inputTokens: 1500 * (i + 1),
    outputTokens: 400 * (i + 1),
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
  }));
  const total = (k: keyof (typeof dayRows)[number]) => dayRows.reduce((s, r) => s + Number(r[k]), 0);
  return {
    from: p.from,
    to: p.to,
    totals: {
      parseRequests: total("parseRequests"),
      aiCalls: total("aiCalls"),
      aiFailures: total("aiFailures"),
      fallbacks: total("fallbacks"),
      inputTokens: total("inputTokens"),
      outputTokens: total("outputTokens"),
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
    },
    days: dayRows,
  };
}

const STEMS: Record<MockReportId, string> = {
  "critical-stock": "kritik-stok",
  orders: "siparisler",
  "top-products": "en-cok-siparis-edilenler",
  "stock-movements": "stok-hareketleri",
  "picking-tasks": "toplama-gorevleri",
  "ai-usage": "ai-kullanimi",
};

const FILTERS: Record<MockReportId, string[]> = {
  "critical-stock": ["threshold", "warehouseId", "includeOutOfStock"],
  orders: ["from", "to", "statuses"],
  "top-products": ["from", "to", "limit"],
  "stock-movements": ["from", "to", "warehouseId"],
  "picking-tasks": ["from", "to", "warehouseId"],
  "ai-usage": ["from", "to"],
};

/** Handles /api/v1/reports/*; returns false for other paths. */
export async function handleReports(route: Route, reports: MockReports, role: string, rules: Partial<FixtureRules>): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const match = url.pathname.match(/^\/api\/v1\/reports\/([a-z-]+)(\/export)?$/);
  if (!match) return false;
  const id = match[1] as MockReportId;
  const isExport = Boolean(match[2]);
  const method = request.method();
  const body = (request.postDataJSON?.() ?? null) as Record<string, unknown> | null;
  reports.requests.push({ method, path: url.pathname, search: url.search, headers: request.headers(), body });
  if (!(id in STEMS) || (isExport ? method !== "POST" : method !== "GET")) return json(route, 404, problem(404, "resource.not_found")).then(() => true);

  // Permission first (as the API): reports.read / reports.export, sales order reports only, AI usage = billing.read.
  const grants = grantsForRoleName(role, rules);
  const grant = (permission: string) => grants.find((g) => g.permission === permission);
  const needed = id === "ai-usage" ? "billing.read" : isExport ? "reports.export" : "reports.read";
  const g = grant(needed);
  if (!g) return json(route, 403, problem(403, "auth.forbidden", { title: `Missing permission: ${needed}` })).then(() => true);
  if (g.scope !== "all" && g.condition === "reports.orders_and_customers_only" && id !== "orders" && id !== "top-products") {
    return json(route, 403, problem(403, "reports.orders_and_customers_only", { permission: needed })).then(() => true);
  }
  const amountsVisible = !reports.hideAmounts && grant("prices.read.sell")?.scope === "all";

  if (!isExport && reports.failNext.has(id)) {
    reports.failNext.delete(id);
    return json(route, 500, problem(500, "server.error")).then(() => true);
  }

  const input: Record<string, unknown> = isExport ? { ...(body ?? {}) } : Object.fromEntries(url.searchParams.entries());
  if (!isExport && typeof input.status === "string") input.statuses = (input.status as string).split(",").filter(Boolean);
  if (isExport) {
    if (!request.headers()["idempotency-key"]) return json(route, 428, problem(428, "precondition.required")).then(() => true);
    const unused = Object.keys(input).find((k) => k !== "format" && input[k] !== null && input[k] !== undefined && !FILTERS[id].includes(k));
    if (unused) return json(route, 422, problem(422, "validation.failed", { errors: { [unused]: [`${unused} is not a filter of this report`] } })).then(() => true);
  }
  if (id !== "critical-stock") {
    const p = period(input.from as string | undefined, input.to as string | undefined);
    if ("error" in p) return json(route, 422, problem(422, "validation.failed", { errors: p.error })).then(() => true);
    input.period = p;
  }

  if (!isExport) return json(route, 200, build(id, reports, input, amountsVisible)).then(() => true);

  if (reports.exportsDone >= reports.exportLimit) {
    return json(route, 429, problem(429, "rate_limit.exceeded"), { "Retry-After": String(reports.retryAfterSeconds) }).then(() => true);
  }
  if (reports.tooLarge.has(id)) return json(route, 422, problem(422, "reports.export_too_large")).then(() => true);
  reports.exportsDone += 1;
  const format = input.format === "xlsx" ? "xlsx" : "csv";
  const p = input.period as Period | undefined;
  const filename = `${STEMS[id]}-${p ? `${p.from}_${p.to}` : istanbulToday()}.${format}`;
  const content =
    format === "csv"
      ? "﻿Rapor;Satır\r\nMOCK;1\r\n"
      : Buffer.from("PK mock xlsx (not a real workbook)");
  return route
    .fulfill({
      status: 200,
      headers: {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Expose-Headers": reports.exposeContentDisposition ? "Retry-After, Content-Disposition" : "ETag, Retry-After, Idempotent-Replayed",
        "Content-Type": format === "csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "Content-Disposition": `attachment; filename=${filename}; filename*=UTF-8''${reports.exposeContentDisposition ? `MOCK-${filename}` : filename}`,
      },
      body: content,
    })
    .then(() => true);
}

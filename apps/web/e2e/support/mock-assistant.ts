/**
 * MOCK WHATSAPP ORDER ASSISTANT FOR PLAYWRIGHT — IN-MEMORY, SYNTHETIC DATA, NOT THE REAL API, NO AI.
 *
 * Stand-in for /api/v1/ai/order-parses following packages/contracts/openapi.json (Integrations, ADR 0010):
 * - POST submit (Idempotency-Key required, replayed on reuse; 422 `text` limits; 429 `ai.rate_limited` + Retry-After
 *   when `rateLimited` is set). A tiny scripted "parser" reads one order line per message line: a line naming a
 *   catalog SKU is `matched` (one proposal), a line containing "ampul" is `ambiguous` (two candidates, no proposal),
 *   a line that looks like an instruction ("talimat", "onayla", "yok say") is dropped with `instruction_ignored`,
 *   anything else with a number is `unmatched`. Parser = rules with fallbackReason `disabled` (AI off), or `parser`.
 * - GET list / GET one (ETag); POST approve (If-Match → 412, every line listed once, explicit variant + quantity,
 *   orders.create needed → 403 auth.forbidden + `permission`) creates a `whatsapp_ai` draft in the Orders mock
 *   (prices from the mock default list, never auto-approved); POST reject.
 * Permissions come from the same MOCK /me fixtures the web receives. No price or stock is ever part of a parse.
 */
import type { Route } from "@playwright/test";
import { fulfillJson, problemBody, type MockCatalog } from "./mock-catalog";
import type { MockCommerce } from "./mock-commerce";
import { MOCK_USER_ID, makeOrder, type MockOrders } from "./mock-orders";
import { grantsForRoleName, type FixtureRules } from "../../src/test/me-fixtures";

type Json = Record<string, unknown>;

export type MockAssistant = {
  parses: Json[];
  /** 429 ai.rate_limited on submit (Retry-After seconds). */
  rateLimitedFor: number | null;
  /** Which parser "ran" and why (defaults: rules / disabled, as with AI switched off). */
  parser: { parser: "ai" | "rules"; fallbackReason: string | null; model: string | null };
  idempotency: Map<string, { status: number; body: unknown }>;
  counter: number;
};

export function createMockAssistant(options: { parses?: Json[]; parser?: MockAssistant["parser"]; rateLimitedFor?: number } = {}): MockAssistant {
  return {
    parses: options.parses ?? [],
    rateLimitedFor: options.rateLimitedFor ?? null,
    parser: options.parser ?? { parser: "rules", fallbackReason: "disabled", model: null },
    idempotency: new Map(),
    counter: 0,
  };
}

export const parseId = (n: number) => `00000000-0000-4000-8000-5a${String(n).padStart(10, "0")}`;

const HEADERS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Idempotent-Replayed" };
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) => fulfillJson(route, status, body, { ...HEADERS, ...headers });
const done = (p: Promise<unknown>) => p.then(() => true);

type Ctx = { catalog: MockCatalog | null; commerce: MockCommerce | null; orders: MockOrders | null; rules?: Partial<FixtureRules> };

function catalogEntries(catalog: MockCatalog | null) {
  return (catalog?.products ?? []).flatMap((p) => p.variants.map((v) => ({ variantId: v.id, productId: p.id, sku: v.sku, name: p.name, unit: "ADET" })));
}

const INSTRUCTION = /talimat|onayla|yok say|ignore/i;

/** The scripted parser (see the file comment). Deterministic, synthetic, catalog-only. */
function parseText(text: string, catalog: MockCatalog | null) {
  const entries = catalogEntries(catalog);
  const warnings: string[] = [];
  const lines: Json[] = [];
  const unparsedFragments: string[] = [];
  for (const raw of text.split("\n").map((l) => l.trim()).filter(Boolean)) {
    if (INSTRUCTION.test(raw)) {
      if (!warnings.includes("instruction_ignored")) warnings.push("instruction_ignored");
      continue;
    }
    const qty = /^(\d+(?:[.,]\d+)?)\s*(adet|koli|kutu|paket)?\s*/i.exec(raw);
    const sku = entries.find((e) => raw.toUpperCase().includes(e.sku));
    if (!qty && !sku) {
      unparsedFragments.push(raw);
      continue;
    }
    const lineNo = lines.length + 1;
    const productText = qty ? raw.slice(qty[0].length).trim() : raw;
    const base = {
      lineNo,
      rawText: raw,
      productCode: sku ? sku.sku : null,
      productText,
      quantity: qty ? Number(qty[1].replace(",", ".")) : null,
      unit: qty?.[2]?.toLowerCase() ?? null,
      brandModel: null,
      parseConfidence: 0.9,
      notes: null,
      unitMismatch: false,
      selectedVariantId: null,
      selectedQuantity: null,
      selection: null,
    };
    if (sku) {
      lines.push({ ...base, matchStatus: "matched", matchConfidence: 1, proposedVariantId: sku.variantId, candidates: [{ ...sku, method: "sku", score: 1 }] });
    } else if (/ampul/i.test(raw) && entries.length >= 3) {
      lines.push({
        ...base,
        matchStatus: "ambiguous",
        matchConfidence: 0.4,
        proposedVariantId: null,
        candidates: [
          { ...entries[1], method: "similarity", score: 0.72 },
          { ...entries[2], method: "similarity", score: 0.7 },
        ],
      });
    } else {
      lines.push({ ...base, matchStatus: "unmatched", matchConfidence: 0, proposedVariantId: null, candidates: [] });
    }
    if (base.quantity === null && !warnings.includes("quantity_missing")) warnings.push("quantity_missing");
  }
  return { lines, warnings, unparsedFragments };
}

function summary(lines: Json[]) {
  const count = (s: string) => lines.filter((l) => l.matchStatus === s).length;
  return { lines: lines.length, matched: count("matched"), ambiguous: count("ambiguous"), unmatched: count("unmatched"), overallConfidence: 0.7 };
}

const etag = (p: Json) => `W/"${p.version}"`;

/** Handles /api/v1/ai/*; returns false for other paths. */
export async function handleAssistant(route: Route, mock: MockAssistant, ctx: Ctx, role: string): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  const method = request.method();
  if (!path.startsWith("/api/v1/ai/")) return false;
  const grants = grantsForRoleName(role, ctx.rules);
  if (!grants.some((g) => g.permission === "whatsapp.parse")) {
    return done(json(route, 403, problemBody(403, "auth.forbidden", { title: "Missing permission: whatsapp.parse", permission: "whatsapp.parse" })));
  }
  const body = (request.postDataJSON?.() ?? null) as Json | null;
  const idemKey = request.headers()["idempotency-key"];

  if (method === "POST" && path === "/api/v1/ai/order-parses") {
    if (!idemKey) return done(json(route, 400, problemBody(400, "idempotency.key_required")));
    const replay = mock.idempotency.get(`submit:${idemKey}`);
    if (replay) return done(json(route, replay.status, replay.body, { "Idempotent-Replayed": "true" }));
    const text = typeof body?.text === "string" ? body.text.trim() : "";
    if (!text) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { text: ["Required."] } })));
    if (text.length > 4000) return done(json(route, 422, problemBody(422, "validation.failed", { errors: { text: ["At most 4000 characters."] } })));
    if (mock.rateLimitedFor !== null) {
      return done(json(route, 429, problemBody(429, "ai.rate_limited"), { "Retry-After": String(mock.rateLimitedFor) }));
    }
    const parsed = parseText(text, ctx.catalog);
    const fallbackWarning = mock.parser.parser === "ai" ? [] : mock.parser.fallbackReason === "quota_exceeded" ? ["ai_quota_exceeded"] : mock.parser.fallbackReason === "disabled" ? ["ai_disabled"] : ["ai_unavailable"];
    const parse: Json = {
      id: parseId(++mock.counter),
      status: "needs_review",
      channel: "whatsapp_paste",
      customerId: body?.customerId ?? null,
      parser: mock.parser.parser,
      model: mock.parser.model,
      promptVersion: "mock-v1",
      fallbackReason: mock.parser.fallbackReason,
      messageText: text,
      textPurged: false,
      lines: parsed.lines,
      unparsedFragments: parsed.unparsedFragments,
      warnings: [...fallbackWarning, ...parsed.warnings],
      summary: summary(parsed.lines),
      orderId: null,
      createdAt: new Date(Date.UTC(2026, 10, 5, 9, mock.counter)).toISOString(),
      createdBy: MOCK_USER_ID,
      reviewedAt: null,
      reviewedBy: null,
      version: 1,
    };
    mock.parses.unshift(parse);
    mock.idempotency.set(`submit:${idemKey}`, { status: 201, body: parse });
    return done(json(route, 201, parse, { ETag: etag(parse) }));
  }

  if (method === "GET" && path === "/api/v1/ai/order-parses") {
    const status = url.searchParams.get("status");
    const items = mock.parses
      .filter((p) => !status || p.status === status)
      .slice(0, Number(url.searchParams.get("limit") ?? 20))
      .map((p) => ({ id: p.id, status: p.status, parser: p.parser, fallbackReason: p.fallbackReason, summary: p.summary, customerId: p.customerId, orderId: p.orderId, createdAt: p.createdAt, createdBy: p.createdBy }));
    return done(json(route, 200, { items }));
  }

  const match = /^\/api\/v1\/ai\/order-parses\/([^/]+)(?:\/(approve|reject))?$/.exec(path);
  if (!match) return done(json(route, 404, problemBody(404, "resource.not_found")));
  const parse = mock.parses.find((p) => p.id === match[1]);
  if (!parse) return done(json(route, 404, problemBody(404, "resource.not_found")));
  const action = match[2];
  if (!action && method === "GET") return done(json(route, 200, parse, { ETag: etag(parse) }));
  if (method !== "POST" || !action) return done(json(route, 404, problemBody(404, "resource.not_found")));

  if (!idemKey) return done(json(route, 400, problemBody(400, "idempotency.key_required")));
  const replay = mock.idempotency.get(`${action}:${idemKey}`);
  if (replay) return done(json(route, replay.status, replay.body, { "Idempotent-Replayed": "true" }));
  const ifMatch = request.headers()["if-match"];
  if (ifMatch && ifMatch !== etag(parse)) return done(json(route, 412, problemBody(412, "concurrency.version_mismatch")));
  if (parse.status !== "needs_review") return done(json(route, 409, problemBody(409, "ai.parse_not_reviewable")));

  if (action === "reject") {
    Object.assign(parse, { status: "rejected", reviewedAt: "2026-11-05T10:00:00Z", reviewedBy: MOCK_USER_ID, version: Number(parse.version) + 1 });
    mock.idempotency.set(`${action}:${idemKey}`, { status: 200, body: parse });
    return done(json(route, 200, parse, { ETag: etag(parse) }));
  }

  // approve: whatsapp.parse opens the route; orders.create (unconditional) is needed as well.
  if (grants.find((g) => g.permission === "orders.create")?.scope !== "all") {
    return done(json(route, 403, problemBody(403, "auth.forbidden", { title: "Missing permission: orders.create", permission: "orders.create" })));
  }
  const lines = parse.lines as Json[];
  const input = (Array.isArray(body?.lines) ? body.lines : []) as Json[];
  const errors: Record<string, string[]> = {};
  const customerId = (body?.customerId as string | null) ?? (parse.customerId as string | null);
  if (!customerId) errors.customerId = ["Required (not given on submit either)."];
  const seen = new Set<number>();
  input.forEach((item, i) => {
    const line = lines.find((l) => l.lineNo === item.lineNo);
    if (!line) errors[`lines[${i}].lineNo`] = ["Unknown review line."];
    else if (seen.has(Number(item.lineNo))) errors[`lines[${i}].lineNo`] = ["Listed more than once."];
    seen.add(Number(item.lineNo));
    if (item.include === true) {
      if (!item.variantId) errors[`lines[${i}].variantId`] = ["Choose the product variant for this line."];
      const q = Number(item.quantity ?? line?.quantity);
      if (!(q > 0)) errors[`lines[${i}].quantity`] = ["Required: a positive quantity with at most 3 decimals."];
    }
  });
  if (lines.some((l) => !seen.has(Number(l.lineNo)))) errors.lines = ["Every review line must be listed; missing."];
  else if (!Object.keys(errors).length && !input.some((l) => l.include === true)) errors.lines = ["Include at least one line (or reject the parse)."];
  if (Object.keys(errors).length) return done(json(route, 422, problemBody(422, "validation.failed", { errors })));

  if (!ctx.orders) return done(json(route, 500, problemBody(500, "server.error")));
  const n = 900 + ctx.orders.orders.length;
  const order = makeOrder(ctx, n, {
    customerId: String(customerId),
    source: "whatsapp_ai",
    createdBy: MOCK_USER_ID,
    createdAt: "2026-11-05T10:00:00Z",
    lines: input.filter((l) => l.include === true).map((l) => ({ variantId: String(l.variantId), quantity: Number(l.quantity) })),
  });
  ctx.orders.orders.push(order);
  for (const item of input) {
    const line = lines.find((l) => l.lineNo === item.lineNo)!;
    line.selectedVariantId = item.include ? item.variantId : null;
    line.selectedQuantity = item.include ? item.quantity : null;
    line.selection = !item.include ? "skipped" : item.variantId === line.proposedVariantId ? "proposed" : "candidate";
  }
  Object.assign(parse, { status: "approved", orderId: order.id, customerId, reviewedAt: "2026-11-05T10:00:00Z", reviewedBy: MOCK_USER_ID, version: Number(parse.version) + 1 });
  mock.idempotency.set(`${action}:${idemKey}`, { status: 200, body: parse });
  return done(json(route, 200, parse, { ETag: etag(parse) }));
}

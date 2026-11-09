import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";
import { parseDecimalInput, toDecimalInput, type Decimal } from "@/lib/pricing/money";

type S = components["schemas"];

/**
 * Minimum stock thresholds (#137, GET/POST /inventory/thresholds). `warehouseId` null = the variant's general
 * (tenant-wide) minimum; a warehouse row overrides it in that warehouse. Read with inventory.read, written with
 * catalog.write.
 */
export type StockThreshold = S["StockThresholdResponse"];
export type StockThresholdPage = { items: StockThreshold[]; page: S["InventoryPageInfo"] };
export type BulkThresholdItem = S["BulkStockThresholdItem"];
export type BulkThresholdResult = S["BulkStockThresholdResponse"];

const BASE = "/api/v1/inventory/thresholds";

/** GET /inventory/thresholds takes at most 100 variant ids per call; the API page maximum is 200. */
export const THRESHOLD_VARIANT_LIMIT = 100;
export const THRESHOLD_PAGE_SIZE = 200;
export const THRESHOLD_BULK_LIMIT = 500;

/** Every threshold of the given variants (default + warehouse overrides), following pages. */
export async function listThresholds(api: ApiClient, variantIds: readonly string[], signal?: AbortSignal): Promise<StockThreshold[]> {
  const all: StockThreshold[] = [];
  for (let i = 0; i < variantIds.length; i += THRESHOLD_VARIANT_LIMIT) {
    const chunk = variantIds.slice(i, i + THRESHOLD_VARIANT_LIMIT);
    let cursor: string | null = null;
    do {
      const params = new URLSearchParams({ variantIds: chunk.join(","), limit: String(THRESHOLD_PAGE_SIZE) });
      if (cursor) params.set("cursor", cursor);
      const page: StockThresholdPage = (await api.request<StockThresholdPage>(`${BASE}?${params}`, { tenant: true, signal })).data;
      all.push(...page.items);
      cursor = page.page.hasMore ? page.page.nextCursor : null;
    } while (cursor);
  }
  return all;
}

/** Create or replace the threshold of (variant, warehouse); warehouseId null = general minimum. */
export async function setThreshold(
  api: ApiClient,
  body: { variantId: string; warehouseId: string | null; minQuantity: string },
  idempotencyKey: string,
): Promise<StockThreshold> {
  return (await api.request<StockThreshold>(BASE, { method: "POST", tenant: true, body, idempotencyKey })).data;
}

/** Several sets/removals in one all-or-nothing request (≤ 500 rows). */
export async function bulkThresholds(api: ApiClient, items: BulkThresholdItem[], idempotencyKey: string): Promise<BulkThresholdResult> {
  return (await api.request<BulkThresholdResult>(`${BASE}/bulk`, { method: "POST", tenant: true, body: { items }, idempotencyKey })).data;
}

/** PUT with If-Match: a concurrent change answers 412. */
export async function updateThreshold(api: ApiClient, threshold: StockThreshold, minQuantity: string): Promise<StockThreshold> {
  return (
    await api.request<StockThreshold>(`${BASE}/${encodeURIComponent(threshold.id)}`, {
      method: "PUT",
      tenant: true,
      body: { minQuantity },
      ifMatch: etagOf(threshold.version),
    })
  ).data;
}

export async function deleteThreshold(api: ApiClient, threshold: StockThreshold): Promise<void> {
  await api.request<void>(`${BASE}/${encodeURIComponent(threshold.id)}`, { method: "DELETE", tenant: true, ifMatch: etagOf(threshold.version) });
}

// ---- Pure helpers ----

/** General (warehouse-less) threshold of each variant. */
export function defaultThresholds(thresholds: readonly StockThreshold[]): Map<string, StockThreshold> {
  return new Map(thresholds.filter((t) => !t.warehouseId).map((t) => [t.variantId, t]));
}

/**
 * The effective minimum of a variant in one warehouse (override, else general) — the rule the API applies to the
 * stock list and the critical-stock report. Null when neither exists.
 */
export function effectiveMinimum(thresholds: readonly StockThreshold[], variantId: string, warehouseId: string): Decimal | null {
  const own = thresholds.find((t) => t.variantId === variantId && t.warehouseId === warehouseId);
  if (own) return own.minQuantity;
  return thresholds.find((t) => t.variantId === variantId && !t.warehouseId)?.minQuantity ?? null;
}

/** Text for a threshold input ("" when none). */
export function thresholdInput(value: Decimal | null | undefined): string {
  return toDecimalInput(value ?? null);
}

export type ThresholdParse = { ok: true; value: string | null } | { ok: false; message: string };

/**
 * Typed minimum → API decimal. Empty = no threshold (remove). Whole numbers only when the unit has no fractions.
 * The API checks again (422 with the same rule).
 */
export function parseThreshold(text: string, allowsFraction: boolean): ThresholdParse {
  if (text.trim() === "") return { ok: true, value: null };
  const parsed = parseDecimalInput(text, allowsFraction ? 3 : 0);
  if (!parsed.ok) {
    return {
      ok: false,
      message: allowsFraction ? "0 veya daha büyük, en fazla 3 ondalıklı bir sayı girin." : "Bu birim kesirli olamaz; 0 veya daha büyük bir tam sayı girin.",
    };
  }
  if (Number(parsed.value) > 1_000_000_000) return { ok: false, message: "En fazla 1.000.000.000 girilebilir." };
  return { ok: true, value: parsed.value };
}

function sameDecimal(a: Decimal, b: string): boolean {
  return Number(a) === Number(b);
}

/** One threshold input: (variant, warehouse or null = general) and the typed text. */
export type ThresholdRow = { variantId: string; warehouseId?: string | null; text: string; allowsFraction: boolean };

/** Stable key of a (variant, warehouse) pair for error maps: "variantId" or "variantId@warehouseId". */
export function thresholdKey(variantId: string, warehouseId?: string | null): string {
  return warehouseId ? `${variantId}@${warehouseId}` : variantId;
}

/**
 * The bulk request for a set of threshold inputs: only rows whose value changed (set) or was cleared (remove).
 * `errors` is keyed by {@link thresholdKey}; no request should be sent while it is non-empty.
 */
export function planThresholds(
  rows: readonly ThresholdRow[],
  current: readonly StockThreshold[],
): { items: BulkThresholdItem[]; errors: Record<string, string> } {
  const items: BulkThresholdItem[] = [];
  const errors: Record<string, string> = {};
  for (const row of rows) {
    const warehouseId = row.warehouseId ?? null;
    const parsed = parseThreshold(row.text, row.allowsFraction);
    if (!parsed.ok) {
      errors[thresholdKey(row.variantId, warehouseId)] = parsed.message;
      continue;
    }
    const existing = current.find((t) => t.variantId === row.variantId && (t.warehouseId ?? null) === warehouseId);
    if (parsed.value === null) {
      if (existing) items.push({ variantId: row.variantId, warehouseId, minQuantity: null, remove: true });
    } else if (!existing || !sameDecimal(existing.minQuantity, parsed.value)) {
      items.push({ variantId: row.variantId, warehouseId, minQuantity: parsed.value, remove: null });
    }
  }
  return { items, errors };
}

/** Maps a bulk 422 (`items[i].minQuantity`, …) back to the inputs that produced item i. */
export function bulkErrorsByKey(items: readonly BulkThresholdItem[], fieldErrors: Record<string, string>): Record<string, string> {
  const mapped: Record<string, string> = {};
  for (const [key, message] of Object.entries(fieldErrors)) {
    const match = /^items\[(\d+)\]/.exec(key);
    const item = match ? items[Number(match[1])] : undefined;
    if (item?.variantId) mapped[thresholdKey(item.variantId, item.warehouseId)] = message;
  }
  return mapped;
}

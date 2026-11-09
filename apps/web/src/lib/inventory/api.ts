import { ApiError, type ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf, type Variant } from "@/lib/catalog/api";

type S = components["schemas"];

export type Warehouse = S["WarehouseResponse"];
export type Location = S["LocationResponse"];
export type Balance = S["BalanceResponse"];
export type Movement = S["MovementResult"];
export type InventoryPageInfo = S["InventoryPageInfo"];
export type BalancePage = { items: Balance[]; page: InventoryPageInfo };
export type MovementPage = { items: Movement[]; page: InventoryPageInfo };
export type ReceiptResult = S["ReceiptResult"];
export type CreateReceiptRequest = S["CreateReceiptRequest"];
export type ReceiptLineRequest = S["ReceiptLineRequest"];
export type CreateAdjustmentRequest = S["CreateAdjustmentRequest"];
export type CreateWarehouseRequest = S["CreateWarehouseRequest"];
export type UpdateWarehouseRequest = S["UpdateWarehouseRequest"];
export type CreateLocationRequest = S["CreateLocationRequest"];
export type UpdateLocationRequest = S["UpdateLocationRequest"];

export { etagOf };

const MERGE_PATCH = "application/merge-patch+json";

export const BALANCE_PAGE_SIZE = 50;
export const MOVEMENT_PAGE_SIZE = 50;

// ---- Warehouses and locations (Warehouse module) ----

/** All warehouses, default first (the API returns ≤ 1000, unpaged). */
export async function listWarehouses(api: ApiClient, options: { includeInactive?: boolean; signal?: AbortSignal } = {}): Promise<Warehouse[]> {
  const query = options.includeInactive ? "?includeInactive=true" : "";
  return (await api.request<{ items: Warehouse[] }>(`/api/v1/warehouses${query}`, { tenant: true, signal: options.signal })).data.items;
}

export async function createWarehouse(api: ApiClient, body: CreateWarehouseRequest, idempotencyKey: string): Promise<Warehouse> {
  return (await api.request<Warehouse>("/api/v1/warehouses", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

/** JSON merge patch with If-Match `W/"<version>"`. */
export async function updateWarehouse(api: ApiClient, warehouse: Warehouse, patch: Partial<UpdateWarehouseRequest>): Promise<Warehouse> {
  return (
    await api.request<Warehouse>(`/api/v1/warehouses/${encodeURIComponent(warehouse.id)}`, {
      method: "PATCH",
      tenant: true,
      body: patch,
      ifMatch: etagOf(warehouse.version),
      contentType: MERGE_PATCH,
    })
  ).data;
}

/** Locations of a warehouse in pick order. */
export async function listLocations(
  api: ApiClient,
  warehouseId: string,
  options: { includeInactive?: boolean; signal?: AbortSignal } = {},
): Promise<Location[]> {
  const query = options.includeInactive ? "?includeInactive=true" : "";
  return (
    await api.request<{ items: Location[] }>(`/api/v1/warehouses/${encodeURIComponent(warehouseId)}/locations${query}`, {
      tenant: true,
      signal: options.signal,
    })
  ).data.items;
}

export async function createLocation(api: ApiClient, warehouseId: string, body: CreateLocationRequest, idempotencyKey: string): Promise<Location> {
  return (
    await api.request<Location>(`/api/v1/warehouses/${encodeURIComponent(warehouseId)}/locations`, {
      method: "POST",
      tenant: true,
      body,
      idempotencyKey,
    })
  ).data;
}

export async function updateLocation(api: ApiClient, location: Location, patch: Partial<UpdateLocationRequest>): Promise<Location> {
  return (
    await api.request<Location>(`/api/v1/locations/${encodeURIComponent(location.id)}`, {
      method: "PATCH",
      tenant: true,
      body: patch,
      ifMatch: etagOf(location.version),
      contentType: MERGE_PATCH,
    })
  ).data;
}

// ---- Balances and movements (Inventory module) ----

/** `q`: free text (SKU, barcode or product name) matched by the API; 422 inventory.search_too_broad when too broad. */
export type BalanceFilter = { variantId?: string | null; warehouseId?: string | null; locationId?: string | null; q?: string | null };

export function balanceQuery(filter: BalanceFilter, cursor?: string | null, limit: number = BALANCE_PAGE_SIZE): string {
  const params = new URLSearchParams();
  if (filter.variantId) params.set("variantId", filter.variantId);
  if (filter.warehouseId) params.set("warehouseId", filter.warehouseId);
  if (filter.locationId) params.set("locationId", filter.locationId);
  if (filter.q?.trim()) params.set("q", filter.q.trim());
  params.set("limit", String(limit));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

export async function listBalances(api: ApiClient, query: string, signal?: AbortSignal): Promise<BalancePage> {
  return (await api.request<BalancePage>(`/api/v1/inventory/balances?${query}`, { tenant: true, signal })).data;
}

export type MovementFilter = { variantId?: string | null; warehouseId?: string | null; type?: string | null; sourceId?: string | null };

export function movementQuery(filter: MovementFilter, cursor?: string | null, limit: number = MOVEMENT_PAGE_SIZE): string {
  const params = new URLSearchParams();
  if (filter.variantId) params.set("variantId", filter.variantId);
  if (filter.warehouseId) params.set("warehouseId", filter.warehouseId);
  if (filter.type) params.set("type", filter.type);
  if (filter.sourceId) params.set("sourceId", filter.sourceId);
  params.set("limit", String(limit));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

export async function listMovements(api: ApiClient, query: string, signal?: AbortSignal): Promise<MovementPage> {
  return (await api.request<MovementPage>(`/api/v1/inventory/movements?${query}`, { tenant: true, signal })).data;
}

export async function createReceipt(api: ApiClient, body: CreateReceiptRequest, idempotencyKey: string): Promise<ReceiptResult> {
  return (await api.request<ReceiptResult>("/api/v1/inventory/receipts", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

export async function createAdjustment(api: ApiClient, body: CreateAdjustmentRequest, idempotencyKey: string): Promise<Movement> {
  return (await api.request<Movement>("/api/v1/inventory/adjustments", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

// ---- Variant lookups (Catalog module, read-only) ----

export async function getVariant(api: ApiClient, id: string, signal?: AbortSignal): Promise<Variant> {
  return (await api.request<Variant>(`/api/v1/variants/${encodeURIComponent(id)}`, { tenant: true, signal })).data;
}

export type VariantSummary = components["schemas"]["VariantSummaryResponse"];

/** GET /variants/batch accepts 1–100 ids per call. */
export const VARIANT_BATCH_LIMIT = 100;

/**
 * SKU, name, product name, barcode and status for up to 100 variant ids in one call (GET /variants/batch). Foreign or
 * unknown ids are simply absent from the result.
 */
export async function getVariantsBatch(api: ApiClient, ids: readonly string[], signal?: AbortSignal): Promise<VariantSummary[]> {
  if (ids.length === 0) return [];
  if (ids.length > VARIANT_BATCH_LIMIT) throw new Error(`getVariantsBatch: at most ${VARIANT_BATCH_LIMIT} ids per call`);
  const params = new URLSearchParams({ ids: ids.join(",") });
  return (await api.request<{ items: VariantSummary[] }>(`/api/v1/variants/batch?${params}`, { tenant: true, signal })).data.items;
}

/** Splits ids into GET /variants/batch sized chunks. */
export function variantBatches(ids: readonly string[], size: number = VARIANT_BATCH_LIMIT): string[][] {
  const chunks: string[][] = [];
  for (let i = 0; i < ids.length; i += size) chunks.push(ids.slice(i, i + size));
  return chunks;
}

/** Variants matching a search term (SKU contains, exact barcode, name contains), SKU order. */
export async function searchVariants(api: ApiClient, term: string, options: { limit?: number; signal?: AbortSignal } = {}): Promise<Variant[]> {
  const params = new URLSearchParams({ q: term, limit: String(options.limit ?? 10) });
  return (await api.request<{ items: Variant[] }>(`/api/v1/variants?${params}`, { tenant: true, signal: options.signal })).data.items;
}

/**
 * Exact match for a scanned or typed code: barcode first (GET /variants/lookup), then SKU (GET /variants?sku=).
 * Returns null when neither matches. A keyboard-wedge scanner types the code and presses Enter.
 */
export async function findVariantByCode(api: ApiClient, code: string, signal?: AbortSignal): Promise<Variant | null> {
  const value = code.trim();
  if (!value) return null;
  try {
    const params = new URLSearchParams({ barcode: value });
    return (await api.request<Variant>(`/api/v1/variants/lookup?${params}`, { tenant: true, signal })).data;
  } catch (error) {
    if (!(error instanceof ApiError) || (error.status !== 404 && error.status !== 400)) throw error;
  }
  const params = new URLSearchParams({ sku: value, limit: "2" });
  const { items } = (await api.request<{ items: Variant[] }>(`/api/v1/variants?${params}`, { tenant: true, signal })).data;
  return items.length === 1 ? items[0] : null;
}

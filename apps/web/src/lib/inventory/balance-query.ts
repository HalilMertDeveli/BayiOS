/** /stok list state as it lives in the page URL (?warehouseId=…&q=…). */
export type BalanceListQuery = {
  warehouseId: string;
  /** Exact SKU or barcode; resolved to one variant before the balances are queried. */
  q: string;
};

type ParamsLike = { get(name: string): string | null };

export function parseBalanceQuery(params: ParamsLike): BalanceListQuery {
  return { warehouseId: params.get("warehouseId") ?? "", q: (params.get("q") ?? "").trim() };
}

export function toBalanceSearch(query: BalanceListQuery): string {
  const params = new URLSearchParams();
  if (query.warehouseId) params.set("warehouseId", query.warehouseId);
  if (query.q.trim()) params.set("q", query.q.trim());
  return params.toString();
}

/** Page for one variant's stock and movement history. */
export function variantStockHref(variantId: string): string {
  return `/stok/varyant/${encodeURIComponent(variantId)}`;
}

/** Adjustment form, optionally pre-filled from a balance row. */
export function adjustmentHref(prefill: { variantId?: string; warehouseId?: string; locationId?: string | null } = {}): string {
  const params = new URLSearchParams();
  if (prefill.variantId) params.set("variantId", prefill.variantId);
  if (prefill.warehouseId) params.set("warehouseId", prefill.warehouseId);
  if (prefill.locationId) params.set("locationId", prefill.locationId);
  const search = params.toString();
  return search ? `/stok/duzeltme?${search}` : "/stok/duzeltme";
}

export const RECEIPT_HREF = "/depo/mal-kabul";

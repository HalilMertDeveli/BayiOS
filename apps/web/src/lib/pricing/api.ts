import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";

type S = components["schemas"];

export type PriceList = S["PriceListResponse"];
export type PriceListItem = S["PriceListItemResponse"];
export type PriceListItemPage = S["PriceListItemPageResponse"];
export type CreatePriceListRequest = S["CreatePriceListRequest"];
export type UpdatePriceListRequest = S["UpdatePriceListRequest"];
export type PriceListItemInput = S["PriceListItemInput"];
export type UpsertPriceListItemsResponse = S["UpsertPriceListItemsResponse"];
export type PriceQuoteRequest = S["PriceQuoteApiRequest"];
export type PriceQuote = S["PriceQuote"];
export type ResolvedPrice = S["ResolvedPrice"];

export { etagOf };

const MERGE_PATCH = "application/merge-patch+json";

export const ITEM_PAGE_SIZE = 100;
/** PUT /price-lists/{id}/items accepts at most 500 rows per call. */
export const MAX_UPSERT_ITEMS = 500;

export const PRICE_LISTS_HREF = "/fiyat-listeleri";
export const QUOTE_HREF = "/fiyat-listeleri/sorgula";

export function priceListHref(id: string): string {
  return `${PRICE_LISTS_HREF}/${encodeURIComponent(id)}`;
}

/** All price lists (default first; ≤ 1000, unpaged). Purchase lists only come back for roles with prices.read.cost. */
export async function listPriceLists(api: ApiClient, options: { includeArchived?: boolean; signal?: AbortSignal } = {}): Promise<PriceList[]> {
  const query = options.includeArchived ? "?includeArchived=true" : "";
  return (await api.request<{ items: PriceList[] }>(`/api/v1/price-lists${query}`, { tenant: true, signal: options.signal })).data.items;
}

export async function getPriceList(api: ApiClient, id: string, signal?: AbortSignal): Promise<{ priceList: PriceList; etag: string }> {
  const response = await api.request<PriceList>(`/api/v1/price-lists/${encodeURIComponent(id)}`, { tenant: true, signal });
  return { priceList: response.data, etag: response.etag ?? etagOf(response.data.version) };
}

export async function createPriceList(api: ApiClient, body: CreatePriceListRequest, idempotencyKey: string): Promise<PriceList> {
  return (await api.request<PriceList>("/api/v1/price-lists", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

/** JSON merge patch (changed members only) with If-Match. Archive = `{ status: "archived" }`. */
export async function updatePriceList(
  api: ApiClient,
  id: string,
  patch: Partial<UpdatePriceListRequest>,
  etag: string,
): Promise<{ priceList: PriceList; etag: string }> {
  const response = await api.request<PriceList>(`/api/v1/price-lists/${encodeURIComponent(id)}`, {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: MERGE_PATCH,
  });
  return { priceList: response.data, etag: response.etag ?? etagOf(response.data.version) };
}

export async function listPriceListItems(
  api: ApiClient,
  priceListId: string,
  options: { cursor?: string | null; variantId?: string | null; limit?: number; signal?: AbortSignal } = {},
): Promise<PriceListItemPage> {
  const params = new URLSearchParams({ limit: String(options.limit ?? ITEM_PAGE_SIZE) });
  if (options.variantId) params.set("variantId", options.variantId);
  if (options.cursor) params.set("cursor", options.cursor);
  return (
    await api.request<PriceListItemPage>(`/api/v1/price-lists/${encodeURIComponent(priceListId)}/items?${params}`, {
      tenant: true,
      signal: options.signal,
    })
  ).data;
}

/** Bulk insert/update keyed by (variantId, minQuantity). The whole batch fails on any invalid row (422 per row). */
export async function upsertPriceListItems(
  api: ApiClient,
  priceListId: string,
  items: PriceListItemInput[],
  idempotencyKey: string,
): Promise<UpsertPriceListItemsResponse> {
  return (
    await api.request<UpsertPriceListItemsResponse>(`/api/v1/price-lists/${encodeURIComponent(priceListId)}/items`, {
      method: "PUT",
      tenant: true,
      body: { items },
      idempotencyKey,
    })
  ).data;
}

export async function deletePriceListItem(api: ApiClient, priceListId: string, itemId: string): Promise<void> {
  await api.request<void>(`/api/v1/price-lists/${encodeURIComponent(priceListId)}/items/${encodeURIComponent(itemId)}`, {
    method: "DELETE",
    tenant: true,
  });
}

/** Side-effect-free price resolution (customer list → dealer group list → default list; else `no_price`). */
export async function quotePrices(api: ApiClient, body: PriceQuoteRequest, signal?: AbortSignal): Promise<PriceQuote> {
  return (await api.request<PriceQuote>("/api/v1/pricing/quote", { method: "POST", tenant: true, body, signal })).data;
}

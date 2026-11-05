import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";

type S = components["schemas"];

export type OrderSummary = S["OrderSummaryResponse"];
export type OrderPage = S["OrderPageResponse"];
export type Order = S["OrderResponse"];
export type OrderLine = S["OrderLineResponse"];
export type OrderHistoryEntry = S["OrderHistoryResponse"];
export type OrderShipment = S["OrderShipmentResponse"];
export type OrderTotals = S["OrderTotalsResponse"];
export type OrderDetail = S["OrderDetailResponse"];
export type OrderLineInput = S["OrderLineInput"];
export type CreateOrderRequest = S["CreateOrderRequest"];
export type UpdateOrderRequest = S["UpdateOrderRequest"];

export { etagOf };

const MERGE_PATCH = "application/merge-patch+json";

export const ORDERS_HREF = "/siparisler";
export const NEW_ORDER_HREF = "/siparisler/yeni";

export function orderHref(id: string): string {
  return `${ORDERS_HREF}/${encodeURIComponent(id)}`;
}

/** A loaded order: the detail plus the ETag (`W/"<version>"`) used as If-Match on the next write. */
export type LoadedOrder = { detail: OrderDetail; etag: string };

const loaded = (data: OrderDetail, etag: string | null): LoadedOrder => ({ detail: data, etag: etag ?? etagOf(data.order.version) });

/** GET /api/v1/orders with an already-built query string (see list-query.ts). Newest first, keyset paged. */
export async function listOrders(api: ApiClient, query: string, signal?: AbortSignal): Promise<OrderPage> {
  const suffix = query ? `?${query}` : "";
  return (await api.request<OrderPage>(`/api/v1/orders${suffix}`, { tenant: true, signal })).data;
}

/** Lines, totals with the VAT breakdown, the status timeline and shipments. */
export async function getOrder(api: ApiClient, id: string, signal?: AbortSignal): Promise<LoadedOrder> {
  const response = await api.request<OrderDetail>(`/api/v1/orders/${encodeURIComponent(id)}`, { tenant: true, signal });
  return loaded(response.data, response.etag);
}

/** POST /api/v1/orders: a draft. Prices are resolved by the API; `unitPrice`/discounts need orders.price.override. */
export async function createOrder(api: ApiClient, body: CreateOrderRequest, idempotencyKey: string): Promise<LoadedOrder> {
  const response = await api.request<OrderDetail>("/api/v1/orders", { method: "POST", tenant: true, body, idempotencyKey });
  return loaded(response.data, response.etag);
}

/** Draft edit: JSON merge patch (changed members only) with If-Match. `lines`, when sent, replaces every line. */
export async function updateOrder(api: ApiClient, id: string, patch: Partial<UpdateOrderRequest>, etag: string): Promise<LoadedOrder> {
  const response = await api.request<OrderDetail>(`/api/v1/orders/${encodeURIComponent(id)}`, {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: MERGE_PATCH,
  });
  return loaded(response.data, response.etag);
}

/**
 * Order-desk transitions built in this screen. Fulfilment (picking tasks, packing, shipments, close-remaining) goes
 * through the Warehouse picking-task and shipment APIs and is deliberately absent here (read-only on the detail page).
 */
export type OrderTransition = "submit" | "withdraw" | "reject" | "approve" | "cancel";

export type TransitionBody = { reason: string } | { confirmNegativeStock: boolean } | undefined;

/** POST /api/v1/orders/{id}/{action} with If-Match and an Idempotency-Key (required by every transition). */
export async function transitionOrder(
  api: ApiClient,
  id: string,
  action: OrderTransition,
  options: { etag: string; idempotencyKey: string; body?: TransitionBody },
): Promise<LoadedOrder> {
  const response = await api.request<OrderDetail>(`/api/v1/orders/${encodeURIComponent(id)}/${action}`, {
    method: "POST",
    tenant: true,
    body: options.body,
    ifMatch: options.etag,
    idempotencyKey: options.idempotencyKey,
  });
  return loaded(response.data, response.etag);
}

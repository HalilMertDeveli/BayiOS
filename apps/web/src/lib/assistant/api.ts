import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";

type S = components["schemas"];

export type OrderParse = S["OrderParseResponse"];
export type OrderParseLine = S["OrderParseLineResponse"];
export type OrderParseCandidate = S["OrderParseCandidateResponse"];
export type OrderParseListItem = S["OrderParseListItem"];
export type SubmitOrderParseRequest = S["SubmitOrderParseRequest"];
export type ApproveOrderParseRequest = S["ApproveOrderParseRequest"];
export type ApproveOrderParseLine = S["ApproveOrderParseLine"];

export const WHATSAPP_ORDER_HREF = "/siparisler/whatsapp";

export function orderParseHref(id: string): string {
  return `${WHATSAPP_ORDER_HREF}/${encodeURIComponent(id)}`;
}

/**
 * Input limits of POST /ai/order-parses (services/api AiOptions defaults: MaxInputChars 4000, MaxInputLines 150). The API
 * does not publish them (contract gap); they are configurable on the server, which stays authoritative (422 `text`).
 */
export const MAX_MESSAGE_CHARS = 4000;
export const MAX_MESSAGE_LINES = 150;

/** A loaded parse plus the ETag (`W/"<version>"`) used as If-Match on approve / reject. */
export type LoadedParse = { parse: OrderParse; etag: string };

const loaded = (parse: OrderParse, etag: string | null): LoadedParse => ({ parse, etag: etag ?? etagOf(parse.version) });

/** POST /api/v1/ai/order-parses: parse + catalog matching; 201 with the review payload (no prices, no stock). */
export async function submitOrderParse(api: ApiClient, body: SubmitOrderParseRequest, idempotencyKey: string): Promise<LoadedParse> {
  const response = await api.request<OrderParse>("/api/v1/ai/order-parses", { method: "POST", tenant: true, body, idempotencyKey });
  return loaded(response.data, response.etag);
}

export async function getOrderParse(api: ApiClient, id: string, signal?: AbortSignal): Promise<LoadedParse> {
  const response = await api.request<OrderParse>(`/api/v1/ai/order-parses/${encodeURIComponent(id)}`, { tenant: true, signal });
  return loaded(response.data, response.etag);
}

/** Recent parses (newest first, no message text). */
export async function listOrderParses(
  api: ApiClient,
  options: { status?: string; limit?: number; signal?: AbortSignal } = {},
): Promise<OrderParseListItem[]> {
  const params = new URLSearchParams();
  if (options.status) params.set("status", options.status);
  if (options.limit) params.set("limit", String(options.limit));
  const query = params.toString();
  const response = await api.request<{ items: OrderParseListItem[] }>(`/api/v1/ai/order-parses${query ? `?${query}` : ""}`, {
    tenant: true,
    signal: options.signal,
  });
  return response.data.items;
}

/** Creates the whatsapp_ai draft order from the reviewed lines (every line listed; the response carries `orderId`). */
export async function approveOrderParse(
  api: ApiClient,
  id: string,
  body: ApproveOrderParseRequest,
  options: { etag: string; idempotencyKey: string },
): Promise<LoadedParse> {
  const response = await api.request<OrderParse>(`/api/v1/ai/order-parses/${encodeURIComponent(id)}/approve`, {
    method: "POST",
    tenant: true,
    body,
    ifMatch: options.etag,
    idempotencyKey: options.idempotencyKey,
  });
  return loaded(response.data, response.etag);
}

/** Rejects the parse: no order is created. */
export async function rejectOrderParse(api: ApiClient, id: string, options: { etag: string; idempotencyKey: string }): Promise<LoadedParse> {
  const response = await api.request<OrderParse>(`/api/v1/ai/order-parses/${encodeURIComponent(id)}/reject`, {
    method: "POST",
    tenant: true,
    ifMatch: options.etag,
    idempotencyKey: options.idempotencyKey,
  });
  return loaded(response.data, response.etag);
}

import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";

type S = components["schemas"];

export type TransferSummary = S["StockTransferSummaryResponse"];
export type Transfer = S["StockTransferResponse"];
export type TransferLine = S["StockTransferLineResponse"];
export type TransferPage = S["StockTransferPageResponse"];
export type TransferActionResponse = S["StockTransferActionResponse"];
export type CreateTransferRequest = S["CreateStockTransferRequest"];
export type ReceiveTransferLineRequest = S["ReceiveStockTransferLineRequest"];
export type CountSummary = S["StockCountSummaryResponse"];
export type Count = S["StockCountResponse"];
export type CountLine = S["StockCountLineResponse"];
export type CountPage = S["StockCountPageResponse"];
export type CreateCountRequest = S["CreateStockCountRequest"];
export type CountEntryRequest = S["StockCountEntryRequest"];
export type ApplyCountResponse = S["ApplyStockCountResponse"];
export type CreateRelocationRequest = S["CreateRelocationRequest"];
export type TransferResult = S["TransferResult"];

export { etagOf };

export const TRANSFERS_HREF = "/depo/transferler";
export const NEW_TRANSFER_HREF = "/depo/transferler/yeni";
export const COUNTS_HREF = "/depo/sayimlar";
export const NEW_COUNT_HREF = "/depo/sayimlar/yeni";
export const RELOCATION_HREF = "/stok/raf-tasima";
export const transferHref = (id: string) => `${TRANSFERS_HREF}/${encodeURIComponent(id)}`;
export const countHref = (id: string) => `${COUNTS_HREF}/${encodeURIComponent(id)}`;

/** Shelf move form, optionally pre-filled from a balance row. */
export function relocationHref(prefill: { variantId?: string; warehouseId?: string; locationId?: string | null } = {}): string {
  const params = new URLSearchParams();
  if (prefill.warehouseId) params.set("warehouseId", prefill.warehouseId);
  if (prefill.variantId) params.set("variantId", prefill.variantId);
  if (prefill.locationId) params.set("fromLocationId", prefill.locationId);
  const search = params.toString();
  return search ? `${RELOCATION_HREF}?${search}` : RELOCATION_HREF;
}

/** Page size of the keyset lists (API max 200). */
export const DOCUMENT_PAGE_SIZE = 50;

/** A loaded document + its ETag (`W/"<version>"`), sent as If-Match with the next command. */
export type LoadedTransfer = { transfer: Transfer; etag: string };
export type LoadedCount = { count: Count; etag: string };

const loadedTransfer = (data: Transfer, etag: string | null): LoadedTransfer => ({ transfer: data, etag: etag ?? etagOf(data.transfer.version) });
const loadedCount = (data: Count, etag: string | null): LoadedCount => ({ count: data, etag: etag ?? etagOf(data.count.version) });

const transferPath = (id: string, suffix = "") => `/api/v1/stock-transfers/${encodeURIComponent(id)}${suffix}`;
const countPath = (id: string, suffix = "") => `/api/v1/stock-counts/${encodeURIComponent(id)}${suffix}`;

/** `status` (comma list) and `warehouseId` (source or destination for transfers) as the list endpoints take them. */
export function documentListQuery(filter: { statuses?: readonly string[]; warehouseId?: string | null }, cursor?: string | null): string {
  const params = new URLSearchParams();
  if (filter.statuses?.length) params.set("status", filter.statuses.join(","));
  if (filter.warehouseId) params.set("warehouseId", filter.warehouseId);
  params.set("limit", String(DOCUMENT_PAGE_SIZE));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

// ---- Transfers ----

export async function listTransfers(api: ApiClient, query: string, signal?: AbortSignal): Promise<TransferPage> {
  return (await api.request<TransferPage>(`/api/v1/stock-transfers?${query}`, { tenant: true, signal })).data;
}

export async function getTransfer(api: ApiClient, id: string, signal?: AbortSignal): Promise<LoadedTransfer> {
  const response = await api.request<Transfer>(transferPath(id), { tenant: true, signal });
  return loadedTransfer(response.data, response.etag);
}

export async function createTransfer(api: ApiClient, body: CreateTransferRequest, idempotencyKey: string): Promise<LoadedTransfer> {
  const response = await api.request<Transfer>("/api/v1/stock-transfers", { method: "POST", tenant: true, body, idempotencyKey });
  return loadedTransfer(response.data, response.etag);
}

/** `etag` is sent as If-Match (412 when the document changed meanwhile); null for scan entries (see enterCounts). */
type CommandOptions = { idempotencyKey: string; etag: string | null };

async function transferCommand(api: ApiClient, id: string, command: string, body: unknown, options: CommandOptions): Promise<LoadedTransfer> {
  const response = await api.request<TransferActionResponse>(transferPath(id, `/${command}`), {
    method: "POST",
    tenant: true,
    body,
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return loadedTransfer(response.data.transfer, response.etag);
}

/** Gönder: the whole quantity leaves the source and is "Yolda". */
export const dispatchTransfer = (api: ApiClient, id: string, options: CommandOptions) => transferCommand(api, id, "dispatch", undefined, options);

/** Teslim al (partial allowed): per entry a line id, a quantity and a destination shelf (id or scanned code). */
export const receiveTransfer = (api: ApiClient, id: string, lines: ReceiveTransferLineRequest[], options: CommandOptions) =>
  transferCommand(api, id, "receive", { lines }, options);

export type TransferResolution = "lost" | "return_to_source";

/** Farkı kapat: what is still in transit is written off (`lost`) or returned to the source. */
export const closeTransfer = (api: ApiClient, id: string, body: { resolution: TransferResolution; reason: string }, options: CommandOptions) =>
  transferCommand(api, id, "close", body, options);

/** Cancel a draft (no stock moves). */
export const cancelTransfer = (api: ApiClient, id: string, reason: string, options: CommandOptions) => transferCommand(api, id, "cancel", { reason }, options);

// ---- Shelf-to-shelf move ----

/**
 * POST /inventory/relocations (inventory.relocate). With `includeReserved` on a line, reserved goods still on the shelf
 * move too; the carried reservations come back in `reservationMoves` (#98).
 */
export async function createRelocation(api: ApiClient, body: CreateRelocationRequest, idempotencyKey: string): Promise<TransferResult> {
  return (await api.request<TransferResult>("/api/v1/inventory/relocations", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

/** Total reserved quantity a relocation carried (`reservationMoves`) and how many orders it belongs to. */
export function summariseReservationMoves(moves: readonly { orderId: string; quantity: number | string }[] | null | undefined): { quantity: number; orders: number } {
  const list = moves ?? [];
  const quantity = Math.round(list.reduce((sum, m) => sum + Number(m.quantity), 0) * 1000) / 1000;
  return { quantity, orders: new Set(list.map((m) => m.orderId)).size };
}

// ---- Stock counts ----

export async function listCounts(api: ApiClient, query: string, signal?: AbortSignal): Promise<CountPage> {
  return (await api.request<CountPage>(`/api/v1/stock-counts?${query}`, { tenant: true, signal })).data;
}

export async function getCount(api: ApiClient, id: string, signal?: AbortSignal): Promise<LoadedCount> {
  const response = await api.request<Count>(countPath(id), { tenant: true, signal });
  return loadedCount(response.data, response.etag);
}

export async function createCount(api: ApiClient, body: CreateCountRequest, idempotencyKey: string): Promise<LoadedCount> {
  const response = await api.request<Count>("/api/v1/stock-counts", { method: "POST", tenant: true, body, idempotencyKey });
  return loadedCount(response.data, response.etag);
}

async function countCommand(api: ApiClient, id: string, command: string, body: unknown, options: CommandOptions): Promise<LoadedCount> {
  const response = await api.request<Count>(countPath(id, `/${command}`), {
    method: "POST",
    tenant: true,
    body,
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return loadedCount(response.data, response.etag);
}

/**
 * Counted quantities. Sent without If-Match on purpose (like pick scans): entries arrive faster than the view refreshes
 * and the API applies each under its lock; the Idempotency-Key makes a retried scan count once.
 */
export async function enterCounts(api: ApiClient, id: string, entries: CountEntryRequest[], idempotencyKey: string): Promise<LoadedCount> {
  const response = await api.request<Count>(countPath(id, "/entries"), { method: "POST", tenant: true, body: { entries }, idempotencyKey });
  return loadedCount(response.data, response.etag);
}

export const submitCount = (api: ApiClient, id: string, options: CommandOptions) => countCommand(api, id, "submit", undefined, options);

export const recountLines = (api: ApiClient, id: string, lineIds: string[], options: CommandOptions) => countCommand(api, id, "recount", { lineIds }, options);

export const cancelCount = (api: ApiClient, id: string, reason: string, options: CommandOptions) => countCommand(api, id, "cancel", { reason }, options);

/** Onayla: count_correction movements. `acceptMovedLines` after a 409 count.lines_moved the user confirmed. */
export async function applyCount(api: ApiClient, id: string, acceptMovedLines: boolean, options: CommandOptions): Promise<LoadedCount> {
  const response = await api.request<ApplyCountResponse>(countPath(id, "/apply"), {
    method: "POST",
    tenant: true,
    body: { acceptMovedLines },
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return loadedCount(response.data.count, response.etag);
}

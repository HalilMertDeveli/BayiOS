import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";
import { toNumber } from "@/lib/inventory/quantity";

type S = components["schemas"];

export type TaskSummary = S["PickingTaskSummaryResponse"];
export type TaskLine = S["PickingTaskLineResponse"];
export type TaskDetail = S["PickingTaskResponse"];
export type TaskPage = S["PickingTaskPageResponse"];
export type MyTasks = S["MyPickingTasksResponse"];
export type PickScanRequest = S["PickScanRequest"];
export type PickScanResponse = S["PickScanResponse"];
export type PickLineProgress = S["PickLineProgress"];
export type ShipmentSummary = S["ShipmentSummaryResponse"];
export type Shipment = S["ShipmentResponse"];
export type ShipmentLine = S["ShipmentLineResponse"];
export type ShipmentPage = S["ShipmentPageResponse"];
export type PackShipmentRequest = S["PackShipmentRequest"];
export type ConfirmShipmentRequest = S["ConfirmShipmentRequest"];
export type ConfirmShipmentResponse = S["ConfirmShipmentResponse"];
export type ReallocateResponse = S["ReallocatePickingTaskResponse"];

export { etagOf };

export const TASKS_HREF = "/depo/gorevler";
export const SHIPMENTS_HREF = "/depo/sevkiyatlar";
export const taskHref = (id: string) => `${TASKS_HREF}/${encodeURIComponent(id)}`;
export const shipmentHref = (id: string) => `${SHIPMENTS_HREF}/${encodeURIComponent(id)}`;

/** Page size for the keyset lists (API max 200). */
export const FULFILMENT_PAGE_SIZE = 50;

/** A loaded task: detail + ETag (`W/"<version>"`) used as If-Match on the next supervisor/complete command. */
export type LoadedTask = { detail: TaskDetail; etag: string };
export type LoadedShipment = { shipment: Shipment; etag: string };

const loadedTask = (data: TaskDetail, etag: string | null): LoadedTask => ({ detail: data, etag: etag ?? etagOf(data.task.version) });
const loadedShipment = (data: Shipment, etag: string | null): LoadedShipment => ({ shipment: data, etag: etag ?? etagOf(data.version) });

const taskPath = (id: string, suffix = "") => `/api/v1/picking-tasks/${encodeURIComponent(id)}${suffix}`;
const shipmentPath = (id: string, suffix = "") => `/api/v1/shipments/${encodeURIComponent(id)}${suffix}`;

/** "My work list": active tasks assigned to me (`assigned`) or still in the pool (`pool`). Needs tasks.read.own. */
export async function listMyTasks(api: ApiClient, scope: "assigned" | "pool" | "all", signal?: AbortSignal): Promise<TaskSummary[]> {
  const query = new URLSearchParams({ scope, limit: String(FULFILMENT_PAGE_SIZE) });
  return (await api.request<MyTasks>(`/api/v1/picking-tasks/mine?${query}`, { tenant: true, signal })).data.items;
}

export type TaskListFilter = {
  statuses?: readonly string[];
  assignedTo?: string | null;
  /** low | normal | high | urgent (#98); empty = every priority. */
  priority?: string | null;
  orderId?: string | null;
  type?: string | null;
};

/** Every task (tasks.read.all), oldest first, keyset paged. `statuses` is a comma list on the wire. */
export function taskListQuery(filter: TaskListFilter, cursor?: string | null): string {
  const params = new URLSearchParams();
  if (filter.statuses?.length) params.set("status", filter.statuses.join(","));
  if (filter.type) params.set("type", filter.type);
  if (filter.priority) params.set("priority", filter.priority);
  if (filter.orderId) params.set("orderId", filter.orderId);
  if (filter.assignedTo) params.set("assignedTo", filter.assignedTo);
  params.set("limit", String(FULFILMENT_PAGE_SIZE));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

export async function listTasks(api: ApiClient, query: string, signal?: AbortSignal): Promise<TaskPage> {
  return (await api.request<TaskPage>(`/api/v1/picking-tasks?${query}`, { tenant: true, signal })).data;
}

export async function getTask(api: ApiClient, id: string, signal?: AbortSignal): Promise<LoadedTask> {
  const response = await api.request<TaskDetail>(taskPath(id), { tenant: true, signal });
  return loadedTask(response.data, response.etag);
}

export type TaskCommand = "claim" | "unassign" | "start" | "complete";

/**
 * POST /picking-tasks/{id}/{claim|unassign|start|complete}. `etag` is optional: claim is decided by the API's row lock
 * (two claims → one 409 picking.already_assigned), so it is sent without If-Match; supervisor and complete commands
 * send it and get 412 when the task changed meanwhile.
 */
export async function taskCommand(
  api: ApiClient,
  id: string,
  command: TaskCommand,
  options: { idempotencyKey: string; etag?: string | null },
): Promise<LoadedTask> {
  const response = await api.request<TaskDetail>(taskPath(id, `/${command}`), {
    method: "POST",
    tenant: true,
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return loadedTask(response.data, response.etag);
}

/** tasks.assign: the user must be an active member whose role carries tasks.pick. */
export async function assignTask(api: ApiClient, id: string, userId: string, options: { idempotencyKey: string; etag?: string | null }): Promise<LoadedTask> {
  const response = await api.request<TaskDetail>(taskPath(id, "/assign"), {
    method: "POST",
    tenant: true,
    body: { userId },
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return loadedTask(response.data, response.etag);
}

/**
 * POST /picking-tasks (tasks.assign): the pick task for what is still open and reserved on an approved order — used when
 * the tenant turned off `autoCreatePickingTask` (#98). 409 picking.fulfillment_in_progress when one already exists.
 */
export async function createPickingTask(api: ApiClient, body: { orderId: string; priority: string }, idempotencyKey: string): Promise<LoadedTask> {
  const response = await api.request<TaskDetail>("/api/v1/picking-tasks", { method: "POST", tenant: true, body, idempotencyKey });
  return loadedTask(response.data, response.etag);
}

/** POST /picking-tasks/{id}/priority (tasks.assign) with If-Match: changes an active task's priority. */
export async function setTaskPriority(
  api: ApiClient,
  id: string,
  priority: string,
  options: { idempotencyKey: string; etag?: string | null },
): Promise<LoadedTask> {
  const response = await api.request<TaskDetail>(taskPath(id, "/priority"), {
    method: "POST",
    tenant: true,
    body: { priority },
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return loadedTask(response.data, response.etag);
}

export type ReallocateResult = { loaded: LoadedTask; moved: number; unmoved: number };

/**
 * POST /picking-tasks/{id}/reallocate (tasks.assign) with If-Match: moves the task's open quantity off inactive shelves
 * (or off `fromLocationId`) onto active shelves with free stock; the reservation moves in the same transaction. What
 * cannot move stays on its line and comes back as `unmoved`.
 */
export async function reallocateTask(
  api: ApiClient,
  id: string,
  fromLocationId: string | null,
  options: { idempotencyKey: string; etag?: string | null },
): Promise<ReallocateResult> {
  const response = await api.request<ReallocateResponse>(taskPath(id, "/reallocate"), {
    method: "POST",
    tenant: true,
    body: { fromLocationId },
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag ?? undefined,
  });
  return {
    loaded: loadedTask(response.data.task, response.etag),
    moved: toNumber(response.data.movedQuantity),
    unmoved: toNumber(response.data.unmovedQuantity),
  };
}

/**
 * One scan. Sent without If-Match on purpose: scans arrive faster than the view refreshes and the API validates every
 * one (wrong item/location, over-pick) under its lock; the Idempotency-Key makes a retried scan count once.
 */
export async function scanPick(api: ApiClient, id: string, body: PickScanRequest, idempotencyKey: string): Promise<PickScanResponse> {
  return (await api.request<PickScanResponse>(taskPath(id, "/scans"), { method: "POST", tenant: true, body, idempotencyKey })).data;
}

export async function reportTaskShortage(
  api: ApiClient,
  id: string,
  lineId: string,
  body: { quantity: number | null; reason: string },
  idempotencyKey: string,
): Promise<LoadedTask> {
  const response = await api.request<TaskDetail>(taskPath(id, `/lines/${encodeURIComponent(lineId)}/shortage`), {
    method: "POST",
    tenant: true,
    body,
    idempotencyKey,
  });
  return loadedTask(response.data, response.etag);
}

export function shipmentListQuery(filter: { statuses?: readonly string[]; orderId?: string | null }, cursor?: string | null): string {
  const params = new URLSearchParams();
  if (filter.statuses?.length) params.set("status", filter.statuses.join(","));
  if (filter.orderId) params.set("orderId", filter.orderId);
  params.set("limit", String(FULFILMENT_PAGE_SIZE));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

/** Shipments, oldest first (staff see only those of their own tasks). */
export async function listShipments(api: ApiClient, query: string, signal?: AbortSignal): Promise<ShipmentPage> {
  return (await api.request<ShipmentPage>(`/api/v1/shipments?${query}`, { tenant: true, signal })).data;
}

export async function getShipment(api: ApiClient, id: string, signal?: AbortSignal): Promise<LoadedShipment> {
  const response = await api.request<Shipment>(shipmentPath(id), { tenant: true, signal });
  return loadedShipment(response.data, response.etag);
}

/** Pack check: every package's items; per line the packed total must equal what ships. */
export async function packShipment(
  api: ApiClient,
  id: string,
  body: PackShipmentRequest,
  options: { idempotencyKey: string; etag: string },
): Promise<LoadedShipment> {
  const response = await api.request<Shipment>(shipmentPath(id, "/pack"), {
    method: "POST",
    tenant: true,
    body,
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag,
  });
  return loadedShipment(response.data, response.etag);
}

export async function reportShipmentShortage(
  api: ApiClient,
  id: string,
  lineId: string,
  body: { quantity: number | null; reason: string },
  options: { idempotencyKey: string; etag: string },
): Promise<LoadedShipment> {
  const response = await api.request<Shipment>(shipmentPath(id, `/lines/${encodeURIComponent(lineId)}/shortage`), {
    method: "POST",
    tenant: true,
    body,
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag,
  });
  return loadedShipment(response.data, response.etag);
}

/** Stock leaves once: the same key replays the stored response; another key gets 409 shipment.already_confirmed. */
export type CancelShipmentResponse = S["CancelShipmentResponse"];

/**
 * POST /shipments/{id}/cancel (tasks.assign): cancels a draft or packed shipment. No stock moves; the order stays as it
 * is and a putaway task is created for what was picked (`putawayTaskId`).
 */
export async function cancelShipment(
  api: ApiClient,
  id: string,
  reason: string,
  options: { idempotencyKey: string; etag: string },
): Promise<{ loaded: LoadedShipment; putawayTaskId: string | null }> {
  const response = await api.request<CancelShipmentResponse>(shipmentPath(id, "/cancel"), {
    method: "POST",
    tenant: true,
    body: { reason },
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag,
  });
  return { loaded: loadedShipment(response.data.shipment, response.etag), putawayTaskId: response.data.putawayTaskId ?? null };
}

export async function confirmShipment(
  api: ApiClient,
  id: string,
  body: ConfirmShipmentRequest,
  options: { idempotencyKey: string; etag: string },
): Promise<{ result: ConfirmShipmentResponse; etag: string }> {
  const response = await api.request<ConfirmShipmentResponse>(shipmentPath(id, "/confirm"), {
    method: "POST",
    tenant: true,
    body,
    idempotencyKey: options.idempotencyKey,
    ifMatch: options.etag,
  });
  return { result: response.data, etag: response.etag ?? etagOf(response.data.shipment.version) };
}

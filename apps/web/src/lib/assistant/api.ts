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

export type OrderParseAccepted = S["OrderParseAcceptedResponse"];
export type OrderParseStatus = S["OrderParseStatusResponse"];
export type AiSettings = S["AiSettingsResponse"];
export type AiInputLimits = S["AiInputLimits"];

/**
 * Fallback input limits of POST /ai/order-parses (services/api AiOptions defaults), used until `GET /ai/settings` has
 * answered with the server's configured `limits` — the server stays authoritative (422 `text`).
 */
export const MAX_MESSAGE_CHARS = 4000;
export const MAX_MESSAGE_LINES = 150;

export type MessageLimits = { maxChars: number; maxLines: number };

export const DEFAULT_MESSAGE_LIMITS: MessageLimits = { maxChars: MAX_MESSAGE_CHARS, maxLines: MAX_MESSAGE_LINES };

export function messageLimits(settings: AiSettings | null): MessageLimits {
  if (!settings) return DEFAULT_MESSAGE_LIMITS;
  const chars = Number(settings.limits.maxInputChars);
  const lines = Number(settings.limits.maxInputLines);
  return {
    maxChars: Number.isFinite(chars) && chars > 0 ? chars : MAX_MESSAGE_CHARS,
    maxLines: Number.isFinite(lines) && lines > 0 ? lines : MAX_MESSAGE_LINES,
  };
}

/** A loaded parse plus the ETag (`W/"<version>"`) used as If-Match on approve / reject. */
export type LoadedParse = { parse: OrderParse; etag: string };

const loaded = (parse: OrderParse, etag: string | null): LoadedParse => ({ parse, etag: etag ?? etagOf(parse.version) });

/**
 * POST /api/v1/ai/order-parses: accepts the message (202); the parse and catalog matching run in the background
 * (ADR 0010 §2.1). Follow it with {@link waitForOrderParse}. 409 `ai.opt_in_required` while the tenant has AI off.
 */
export async function submitOrderParse(api: ApiClient, body: SubmitOrderParseRequest, idempotencyKey: string): Promise<OrderParseAccepted> {
  const response = await api.request<OrderParseAccepted>("/api/v1/ai/order-parses", { method: "POST", tenant: true, body, idempotencyKey });
  return response.data;
}

/** GET …/{id}/status: pending | succeeded | failed (the cheap polling target). */
export async function getOrderParseStatus(api: ApiClient, id: string, signal?: AbortSignal): Promise<OrderParseStatus> {
  const response = await api.request<OrderParseStatus>(`/api/v1/ai/order-parses/${encodeURIComponent(id)}/status`, { tenant: true, signal });
  return response.data;
}

/** The parse is still being processed after the client gave up waiting (it can be opened later from the list). */
export class ParseStillPendingError extends Error {
  constructor(readonly parseId: string) {
    super("The parse is still pending.");
    this.name = "ParseStillPendingError";
  }
}

export type PollOptions = {
  signal?: AbortSignal;
  /** First delay (ms); the 202 / status `pollAfterMs` when known. */
  initialDelayMs?: number;
  /** Backoff factor and cap: 0.75 s → 1.1 s → 1.7 s → … → 5 s. */
  factor?: number;
  maxDelayMs?: number;
  /** Give up (ParseStillPendingError) after this long; the server keeps working. */
  timeoutMs?: number;
  /** Called after each pending answer (e.g. to update a progress text). */
  onPending?: (attempt: number) => void;
  /** Injectable for tests. */
  sleep?: (ms: number, signal?: AbortSignal) => Promise<void>;
  now?: () => number;
};

function abortableSleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason ?? new DOMException("Aborted", "AbortError"));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason ?? new DOMException("Aborted", "AbortError"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/**
 * Polls the status endpoint with exponential backoff until the parse has succeeded or failed (returned), or
 * `timeoutMs` passed (ParseStillPendingError). Errors of a status call (network, 5xx) are thrown to the caller.
 */
export async function waitForOrderParse(api: ApiClient, id: string, options: PollOptions = {}): Promise<OrderParseStatus> {
  const sleep = options.sleep ?? abortableSleep;
  const now = options.now ?? Date.now;
  const factor = options.factor ?? 1.5;
  const maxDelay = options.maxDelayMs ?? 5000;
  const deadline = now() + (options.timeoutMs ?? 90_000);
  let delay = Math.max(100, options.initialDelayMs ?? 750);
  for (let attempt = 1; ; attempt++) {
    await sleep(delay, options.signal);
    const status = await getOrderParseStatus(api, id, options.signal);
    if (status.state !== "pending") return status;
    options.onPending?.(attempt);
    if (now() >= deadline) throw new ParseStillPendingError(id);
    const suggested = Number(status.pollAfterMs);
    delay = Math.min(maxDelay, Math.max(delay * factor, Number.isFinite(suggested) ? suggested : 0));
  }
}

/** GET /api/v1/ai/settings: the tenant's AI opt-in (data-policy §7) and the input limits. */
export async function getAiSettings(api: ApiClient, signal?: AbortSignal): Promise<AiSettings> {
  const response = await api.request<AiSettings>("/api/v1/ai/settings", { tenant: true, signal });
  return response.data;
}

/** PUT /api/v1/ai/settings (owner/admin: tenant.settings.manage). Audited by the API. */
export async function updateAiSettings(api: ApiClient, aiParsingEnabled: boolean): Promise<AiSettings> {
  const response = await api.request<AiSettings>("/api/v1/ai/settings", { method: "PUT", tenant: true, body: { aiParsingEnabled } });
  return response.data;
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

import type { components } from "./schema";
import { NETWORK_ERROR_MESSAGE, problemMessage, readProblem, type ProblemDetails } from "./problems";

export type Meta = components["schemas"]["MetaResponse"];

export class ApiError extends Error {
  /** Stable problem code (api-strategy §6), e.g. `tenant.access_denied`; null for network errors. */
  readonly code: string | null;
  /** Per-field validation messages from a 422 `validation.failed` response. */
  readonly fieldErrors: Record<string, string[]>;
  /** Turkish, user-facing message. */
  readonly userMessage: string;
  /** The full problem body, including extension members (e.g. `shortages`, `confirmationRequired`). */
  readonly problem: ProblemDetails | null;
  /**
   * Seconds from a `Retry-After` header, or null when absent/unreadable. The API exposes the header to the browser
   * (Access-Control-Expose-Headers, #86); callers keep a safe default for a missing value.
   */
  readonly retryAfterSeconds: number | null;

  constructor(
    message: string,
    readonly status: number,
    problem?: ProblemDetails | null,
    userMessage?: string,
    retryAfterSeconds: number | null = null,
  ) {
    super(message);
    this.name = "ApiError";
    this.code = problem?.code ?? null;
    this.fieldErrors = problem?.errors ?? {};
    this.userMessage = userMessage ?? problemMessage(status, problem);
    this.problem = problem ?? null;
    this.retryAfterSeconds = retryAfterSeconds;
  }

  /** True when no HTTP response was received (offline, DNS, CORS, aborted connection). */
  get isNetworkError(): boolean {
    return this.status === 0;
  }
}

/** `Retry-After` as delay-seconds (the API sends integers); an HTTP-date is converted relative to `now`. */
export function parseRetryAfter(value: string | null, now: number = Date.now()): number | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (/^\d+$/.test(trimmed)) return Number(trimmed);
  const date = Date.parse(trimmed);
  return Number.isNaN(date) ? null : Math.max(0, Math.ceil((date - now) / 1000));
}

export function apiBaseUrl(): string {
  const url = process.env.NEXT_PUBLIC_API_BASE_URL;
  if (!url) {
    throw new Error("NEXT_PUBLIC_API_BASE_URL is not configured");
  }
  return url.replace(/\/+$/, "");
}

export async function getMeta(signal?: AbortSignal): Promise<Meta> {
  const response = await fetch(`${apiBaseUrl()}/api/v1/meta`, {
    headers: { Accept: "application/json" },
    signal,
  });
  if (!response.ok) {
    throw new ApiError(`GET /api/v1/meta failed with ${response.status}`, response.status);
  }
  return (await response.json()) as Meta;
}

export const TENANT_HEADER = "X-Tenant-Id";
export const IDEMPOTENCY_HEADER = "Idempotency-Key";

export type ApiClientOptions = {
  /** Supabase access token, or null when signed out. */
  getAccessToken: () => Promise<string | null>;
  /** Active tenant id for tenant-scoped endpoints. */
  getTenantId?: () => string | null;
  /** Called after any 401 (token missing, expired or rejected) — the session layer signs out. */
  onUnauthorized?: () => void;
  /** Called after 403 `tenant.access_denied` — the session layer clears the selection and shows the picker. */
  onTenantAccessDenied?: () => void;
  /** Called after 403 `tenant.inactive` (the business is suspended or closed) — the session layer shows a notice. */
  onTenantInactive?: () => void;
};

export type RequestOptions = {
  method?: "GET" | "POST" | "PATCH" | "PUT" | "DELETE";
  /** JSON-serialised — except FormData, which is sent as multipart/form-data as is. */
  body?: unknown;
  /** Sends X-Tenant-Id. Required by every tenant-scoped endpoint. */
  tenant?: boolean;
  /**
   * Sends this X-Tenant-Id without requiring an active tenant (GET /me: the header is optional and selects whose
   * effective permissions come back). Ignored when null/undefined or when `tenant` is set.
   */
  tenantId?: string | null;
  idempotencyKey?: string;
  ifMatch?: string;
  contentType?: string;
  /** Accept header; defaults to application/json. File downloads ask for the file types plus problem+json. */
  accept?: string;
  /** "blob": the success body is returned as a Blob (file downloads, e.g. report exports); errors stay Problem Details. */
  responseType?: "json" | "blob";
  signal?: AbortSignal;
};

export type ApiResponse<T> = {
  data: T;
  status: number;
  etag: string | null;
  /**
   * Response headers of a `responseType: "blob"` download (e.g. Content-Disposition — readable only when the API exposes
   * it to the browser via Access-Control-Expose-Headers). Absent on JSON responses.
   */
  headers?: Headers;
};

export type ApiClient = {
  request<T>(path: string, options?: RequestOptions): Promise<ApiResponse<T>>;
};

/** Thin fetch wrapper: Bearer token, X-Tenant-Id, Idempotency-Key / If-Match and Problem Details mapping. */
export function createApiClient(options: ApiClientOptions): ApiClient {
  return {
    async request<T>(path: string, request: RequestOptions = {}): Promise<ApiResponse<T>> {
      const method = request.method ?? "GET";
      const headers = new Headers({ Accept: request.accept ?? "application/json" });

      const token = await options.getAccessToken();
      if (token) headers.set("Authorization", `Bearer ${token}`);

      if (request.tenant) {
        const tenantId = options.getTenantId?.() ?? null;
        if (!tenantId) {
          options.onTenantAccessDenied?.();
          throw new ApiError(`${method} ${path}: no active tenant`, 403, { code: "tenant.access_denied" });
        }
        headers.set(TENANT_HEADER, tenantId);
      } else if (request.tenantId) {
        headers.set(TENANT_HEADER, request.tenantId);
      }
      if (request.idempotencyKey) headers.set(IDEMPOTENCY_HEADER, request.idempotencyKey);
      if (request.ifMatch) headers.set("If-Match", request.ifMatch);
      // FormData (multipart uploads): the browser sets Content-Type with the boundary itself.
      const form = typeof FormData !== "undefined" && request.body instanceof FormData ? request.body : null;
      if (request.body !== undefined && !form) headers.set("Content-Type", request.contentType ?? "application/json");

      let response: Response;
      try {
        response = await fetch(`${apiBaseUrl()}${path}`, {
          method,
          headers,
          body: request.body === undefined ? undefined : (form ?? JSON.stringify(request.body)),
          signal: request.signal,
        });
      } catch (error) {
        if (request.signal?.aborted) throw error;
        throw new ApiError(`${method} ${path}: network error`, 0, null, NETWORK_ERROR_MESSAGE);
      }

      if (!response.ok) {
        const problem = await readProblem(response);
        if (response.status === 401) options.onUnauthorized?.();
        if (response.status === 403 && problem?.code === "tenant.access_denied") options.onTenantAccessDenied?.();
        if (response.status === 403 && problem?.code === "tenant.inactive") options.onTenantInactive?.();
        throw new ApiError(
          `${method} ${path} failed with ${response.status}`,
          response.status,
          problem,
          undefined,
          parseRetryAfter(response.headers.get("Retry-After")),
        );
      }

      const etag = response.headers.get("ETag");
      if (request.responseType === "blob") {
        return { data: (await response.blob()) as T, status: response.status, etag, headers: response.headers };
      }
      const data = (response.status === 204 ? undefined : await response.json()) as T;
      return { data, status: response.status, etag };
    },
  };
}

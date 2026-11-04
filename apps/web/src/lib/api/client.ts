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

  constructor(
    message: string,
    readonly status: number,
    problem?: ProblemDetails | null,
    userMessage?: string,
  ) {
    super(message);
    this.name = "ApiError";
    this.code = problem?.code ?? null;
    this.fieldErrors = problem?.errors ?? {};
    this.userMessage = userMessage ?? problemMessage(status, problem);
  }

  /** True when no HTTP response was received (offline, DNS, CORS, aborted connection). */
  get isNetworkError(): boolean {
    return this.status === 0;
  }
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
};

export type RequestOptions = {
  method?: "GET" | "POST" | "PATCH" | "PUT" | "DELETE";
  body?: unknown;
  /** Sends X-Tenant-Id. Required by every tenant-scoped endpoint. */
  tenant?: boolean;
  idempotencyKey?: string;
  ifMatch?: string;
  contentType?: string;
  signal?: AbortSignal;
};

export type ApiResponse<T> = { data: T; status: number; etag: string | null };

export type ApiClient = {
  request<T>(path: string, options?: RequestOptions): Promise<ApiResponse<T>>;
};

/** Thin fetch wrapper: Bearer token, X-Tenant-Id, Idempotency-Key / If-Match and Problem Details mapping. */
export function createApiClient(options: ApiClientOptions): ApiClient {
  return {
    async request<T>(path: string, request: RequestOptions = {}): Promise<ApiResponse<T>> {
      const method = request.method ?? "GET";
      const headers = new Headers({ Accept: "application/json" });

      const token = await options.getAccessToken();
      if (token) headers.set("Authorization", `Bearer ${token}`);

      if (request.tenant) {
        const tenantId = options.getTenantId?.() ?? null;
        if (!tenantId) {
          options.onTenantAccessDenied?.();
          throw new ApiError(`${method} ${path}: no active tenant`, 403, { code: "tenant.access_denied" });
        }
        headers.set(TENANT_HEADER, tenantId);
      }
      if (request.idempotencyKey) headers.set(IDEMPOTENCY_HEADER, request.idempotencyKey);
      if (request.ifMatch) headers.set("If-Match", request.ifMatch);
      if (request.body !== undefined) headers.set("Content-Type", request.contentType ?? "application/json");

      let response: Response;
      try {
        response = await fetch(`${apiBaseUrl()}${path}`, {
          method,
          headers,
          body: request.body === undefined ? undefined : JSON.stringify(request.body),
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
        throw new ApiError(`${method} ${path} failed with ${response.status}`, response.status, problem);
      }

      const data = (response.status === 204 ? undefined : await response.json()) as T;
      return { data, status: response.status, etag: response.headers.get("ETag") };
    },
  };
}

import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, createApiClient, getMeta } from "./client";
import { problemMessage } from "./problems";

afterEach(() => vi.unstubAllGlobals());

const problemResponse = (status: number, body: object) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/problem+json" } });

describe("getMeta", () => {
  it("calls the versioned meta endpoint without a double slash", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      Response.json({ name: "BayiOS API", version: "0.1.0", environment: "Development" }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const meta = await getMeta();

    expect(fetchMock).toHaveBeenCalledWith("http://api.test/api/v1/meta", expect.any(Object));
    expect(meta.name).toBe("BayiOS API");
  });

  it("throws ApiError with the status code on failure", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 503 })));

    await expect(getMeta()).rejects.toMatchObject({ name: "ApiError", status: 503 } satisfies Partial<ApiError>);
  });
});

describe("createApiClient", () => {
  const base = { getAccessToken: async () => "token-123", getTenantId: () => "tenant-1" };

  it("sends the bearer token, tenant header, idempotency key and JSON body", async () => {
    const fetchMock = vi.fn().mockResolvedValue(Response.json({ id: "x" }, { status: 201, headers: { ETag: 'W/"3"' } }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createApiClient(base);

    const result = await api.request<{ id: string }>("/api/v1/units", {
      method: "POST",
      tenant: true,
      idempotencyKey: "key-1",
      body: { code: "AD" },
    });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = init.headers as Headers;
    expect(url).toBe("http://api.test/api/v1/units");
    expect(headers.get("Authorization")).toBe("Bearer token-123");
    expect(headers.get("X-Tenant-Id")).toBe("tenant-1");
    expect(headers.get("Idempotency-Key")).toBe("key-1");
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(init.body).toBe(JSON.stringify({ code: "AD" }));
    expect(result).toEqual({ data: { id: "x" }, status: 201, etag: 'W/"3"' });
  });

  it("omits X-Tenant-Id on user-level endpoints and Authorization when signed out", async () => {
    const fetchMock = vi.fn().mockResolvedValue(Response.json({}));
    vi.stubGlobal("fetch", fetchMock);
    await createApiClient({ getAccessToken: async () => null, getTenantId: () => "tenant-1" }).request("/api/v1/me");
    const headers = (fetchMock.mock.calls[0][1] as RequestInit).headers as Headers;
    expect(headers.has("Authorization")).toBe(false);
    expect(headers.has("X-Tenant-Id")).toBe(false);
  });

  it("refuses a tenant-scoped call without an active tenant and asks for the picker", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const onTenantAccessDenied = vi.fn();
    const api = createApiClient({ getAccessToken: async () => "t", getTenantId: () => null, onTenantAccessDenied });

    await expect(api.request("/api/v1/tenant", { tenant: true })).rejects.toMatchObject({ code: "tenant.access_denied" });
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onTenantAccessDenied).toHaveBeenCalledOnce();
  });

  it("maps 401 to onUnauthorized and a Turkish message", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problemResponse(401, { status: 401, code: "auth.invalid_token" })));
    const onUnauthorized = vi.fn();
    const error = await createApiClient({ ...base, onUnauthorized }).request("/api/v1/me").catch((e: unknown) => e);

    expect(onUnauthorized).toHaveBeenCalledOnce();
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).userMessage).toBe("Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.");
  });

  it("maps 403 tenant.access_denied to onTenantAccessDenied, but not other 403s", async () => {
    const onTenantAccessDenied = vi.fn();
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problemResponse(403, { status: 403, code: "tenant.access_denied" })));
    await createApiClient({ ...base, onTenantAccessDenied }).request("/api/v1/tenant", { tenant: true }).catch(() => {});
    expect(onTenantAccessDenied).toHaveBeenCalledOnce();

    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(problemResponse(403, { status: 403, code: "auth.forbidden" })));
    const error = await createApiClient({ ...base, onTenantAccessDenied })
      .request("/api/v1/tenant/settings", { tenant: true })
      .catch((e: unknown) => e);
    expect(onTenantAccessDenied).toHaveBeenCalledOnce();
    expect((error as ApiError).code).toBe("auth.forbidden");
  });

  it("exposes validation field errors", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(problemResponse(422, { status: 422, code: "validation.failed", errors: { slug: ["bad"] } })),
    );
    const error = (await createApiClient(base).request("/api/v1/tenants", { method: "POST", body: {} }).catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(422);
    expect(error.fieldErrors).toEqual({ slug: ["bad"] });
    expect(error.userMessage).toContain("işaretli alanları");
  });

  it("turns a network failure into status 0 with a Turkish message", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));
    const error = (await createApiClient(base).request("/api/v1/me").catch((e: unknown) => e)) as ApiError;
    expect(error.isNetworkError).toBe(true);
    expect(error.userMessage).toContain("Sunucuya ulaşılamadı");
  });
});

describe("problemMessage", () => {
  it("prefers the stable code, falls back to the status, never leaks the English title", () => {
    expect(problemMessage(409, { code: "tenant.slug_taken", title: "Slug is already in use" })).toContain("işletme adresi");
    expect(problemMessage(412, null)).toContain("değişti");
    expect(problemMessage(500, { title: "NullReferenceException" })).toBe("Beklenmeyen bir hata oluştu. Lütfen biraz sonra tekrar deneyin.");
    expect(problemMessage(418, { code: "unknown.code" })).toBe("Beklenmeyen bir hata oluştu. Lütfen biraz sonra tekrar deneyin.");
  });
});

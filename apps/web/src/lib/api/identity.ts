import type { ApiClient } from "./client";
import type { components } from "./schema";

export type Me = components["schemas"]["MeResponse"];
export type TenantResponse = components["schemas"]["TenantResponse"];
export type CreateTenantRequest = components["schemas"]["CreateTenantRequest"];
export type TenantSettings = components["schemas"]["TenantSettingsResponse"];

/** Sector codes accepted by POST /api/v1/tenants (Identity module `Sectors`). */
export const SECTORS = ["motorcycle_parts", "led_electronics", "general"] as const;
export type Sector = (typeof SECTORS)[number];

export const NEGATIVE_STOCK_POLICIES = ["block", "warn", "allow"] as const;
export type NegativeStockPolicy = (typeof NEGATIVE_STOCK_POLICIES)[number];

export async function getMe(api: ApiClient, signal?: AbortSignal): Promise<Me> {
  return (await api.request<Me>("/api/v1/me", { signal })).data;
}

/** Creates a tenant; the caller becomes its owner. `idempotencyKey` must be reused for retries of the same attempt. */
export async function createTenant(
  api: ApiClient,
  body: { name: string; slug: string; sector: Sector },
  idempotencyKey: string,
): Promise<TenantResponse> {
  return (await api.request<TenantResponse>("/api/v1/tenants", { method: "POST", body, idempotencyKey })).data;
}

/** Settings of the active tenant (X-Tenant-Id) with the ETag needed for PATCH. */
export async function getTenantSettings(api: ApiClient): Promise<{ settings: TenantSettings; etag: string | null }> {
  const response = await api.request<TenantSettings>("/api/v1/tenant/settings", { tenant: true });
  return { settings: response.data, etag: response.etag };
}

export async function updateTenantSettings(
  api: ApiClient,
  patch: Partial<TenantSettings>,
  etag: string,
): Promise<TenantSettings> {
  const response = await api.request<TenantSettings>("/api/v1/tenant/settings", {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: "application/merge-patch+json",
  });
  return response.data;
}

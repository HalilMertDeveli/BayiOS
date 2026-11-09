import type { ApiClient } from "./client";
import type { Permission } from "@/lib/session/access";
import type { components } from "./schema";

export type Me = components["schemas"]["MeResponse"];
export type TenantResponse = components["schemas"]["TenantResponse"];
export type CreateTenantRequest = components["schemas"]["CreateTenantRequest"];
export type TenantSettings = components["schemas"]["TenantSettingsResponse"];
export type UpdateTenantSettingsRequest = components["schemas"]["UpdateTenantSettingsRequest"];
export type UpdateTenantRequest = components["schemas"]["UpdateTenantRequest"];
export type ApplySectorTemplateResponse = components["schemas"]["ApplySectorTemplateResponse"];

/** Sector codes accepted by POST /api/v1/tenants (Identity module `Sectors`). */
export const SECTORS = ["motorcycle_parts", "led_electronics", "general"] as const;
export type Sector = (typeof SECTORS)[number];

/**
 * GET /api/v1/me. With `tenantId` the optional X-Tenant-Id header is sent and `activeTenant` carries that membership's
 * role and effective permissions; it is null when the header is absent, or the tenant is foreign or not active.
 */
export async function getMe(api: ApiClient, options: { tenantId?: string | null; signal?: AbortSignal } = {}): Promise<Me> {
  return (await api.request<Me>("/api/v1/me", { tenantId: options.tenantId, signal: options.signal })).data;
}

export type Member = components["schemas"]["MemberResponse"];
type MemberPage = components["schemas"]["MemberPageResponse"];

/** Display label of a member: the API's display name, else full name, else e-mail. */
export function memberLabel(m: Pick<Member, "displayName" | "fullName" | "email" | "userId">): string {
  return m.displayName || m.fullName || m.email || m.userId;
}

/**
 * Active members of the active tenant (GET /members, members.read), every page (keyset, capped), sorted by name on the
 * client (Turkish collation) for the staff picker. `permissions` (#111, `?permission=a,b`) keeps only members whose role
 * holds every one of them under the tenant's rules — e.g. tasks.pick for the people a picking task can be assigned to.
 */
export async function listActiveMembers(
  api: ApiClient,
  signal?: AbortSignal,
  options: { permissions?: readonly Permission[]; maxPages?: number } = {},
): Promise<Member[]> {
  const { permissions = [], maxPages = 10 } = options;
  const members: Member[] = [];
  let cursor: string | null = null;
  for (let page = 0; page < maxPages; page++) {
    const params = new URLSearchParams({ status: "active", limit: "200" });
    if (permissions.length > 0) params.set("permission", permissions.join(","));
    if (cursor) params.set("cursor", cursor);
    const data: MemberPage = (await api.request<MemberPage>(`/api/v1/members?${params}`, { tenant: true, signal })).data;
    members.push(...data.items);
    if (!data.page.hasMore || !data.page.nextCursor) break;
    cursor = data.page.nextCursor;
  }
  return members.sort((a, b) => memberLabel(a).localeCompare(memberLabel(b), "tr"));
}

/** Creates a tenant; the caller becomes its owner. `idempotencyKey` must be reused for retries of the same attempt. */
export async function createTenant(
  api: ApiClient,
  body: { name: string; slug: string; sector: Sector },
  idempotencyKey: string,
): Promise<TenantResponse> {
  return (await api.request<TenantResponse>("/api/v1/tenants", { method: "POST", body, idempotencyKey })).data;
}

/** Business profile of the active tenant (X-Tenant-Id) with the ETag needed for PATCH. */
export async function getCurrentTenant(api: ApiClient): Promise<{ tenant: TenantResponse; etag: string | null }> {
  const response = await api.request<TenantResponse>("/api/v1/tenant", { tenant: true });
  return { tenant: response.data, etag: response.etag };
}

/** PATCH /api/v1/tenant (JSON merge patch, If-Match). */
export async function updateTenant(api: ApiClient, patch: Partial<UpdateTenantRequest>, etag: string): Promise<TenantResponse> {
  const response = await api.request<TenantResponse>("/api/v1/tenant", {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: "application/merge-patch+json",
  });
  return response.data;
}

/**
 * Applies the sector template (units, categories, custom fields) to the active tenant.
 * Safe to re-apply: existing items are skipped. Requires catalog.fields.manage (owner has it).
 */
export async function applySectorTemplate(api: ApiClient, sector: Sector, idempotencyKey: string): Promise<ApplySectorTemplateResponse> {
  const response = await api.request<ApplySectorTemplateResponse>(
    `/api/v1/sector-templates/${encodeURIComponent(sector)}/apply`,
    { method: "POST", tenant: true, idempotencyKey },
  );
  return response.data;
}

/** Settings of the active tenant (X-Tenant-Id) with the ETag needed for PATCH. */
export async function getTenantSettings(api: ApiClient): Promise<{ settings: TenantSettings; etag: string | null }> {
  const response = await api.request<TenantSettings>("/api/v1/tenant/settings", { tenant: true });
  return { settings: response.data, etag: response.etag };
}

/** PATCH /api/v1/tenant/settings (JSON merge patch, If-Match; 412 when someone else saved first). Returns the new ETag. */
export async function updateTenantSettings(
  api: ApiClient,
  patch: Partial<UpdateTenantSettingsRequest>,
  etag: string,
): Promise<{ settings: TenantSettings; etag: string | null }> {
  const response = await api.request<TenantSettings>("/api/v1/tenant/settings", {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: "application/merge-patch+json",
  });
  return { settings: response.data, etag: response.etag };
}

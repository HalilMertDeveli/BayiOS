import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";

type S = components["schemas"];
export type BillingCatalog = S["BillingCatalogResponse"];
export type Plan = S["PlanResponse"];
export type CatalogModule = S["CatalogModuleResponse"];
export type Subscription = S["SubscriptionResponse"];
export type TenantModule = S["TenantModuleResponse"];
export type Usage = S["UsageResponse"];
export type UsageItem = S["UsageItemResponse"];

const BASE = "/api/v1/billing";

/** GET /billing/plans — the versioned catalog (identity only, no prices). */
export async function getPlans(api: ApiClient, signal?: AbortSignal): Promise<BillingCatalog> {
  return (await api.request<BillingCatalog>(`${BASE}/plans`, { signal })).data;
}

/** GET /billing/subscription (billing.read). */
export async function getSubscription(api: ApiClient, signal?: AbortSignal): Promise<Subscription> {
  return (await api.request<Subscription>(`${BASE}/subscription`, { tenant: true, signal })).data;
}

/** GET /billing/modules (billing.read): the module store with what the caller may do. */
export async function getModules(api: ApiClient, signal?: AbortSignal): Promise<TenantModule[]> {
  return (await api.request<S["TenantModuleListResponse"]>(`${BASE}/modules`, { tenant: true, signal })).data.items;
}

/** GET /billing/usage (billing.read): limit / current / remaining per limit code. */
export async function getUsage(api: ApiClient, signal?: AbortSignal): Promise<Usage> {
  return (await api.request<Usage>(`${BASE}/usage`, { tenant: true, signal })).data;
}

/** POST /billing/modules/{code}/activate (billing.manage, Idempotency-Key): free, time-limited activation — no payment. */
export async function activateModule(api: ApiClient, code: string, idempotencyKey: string): Promise<TenantModule> {
  return (await api.request<TenantModule>(`${BASE}/modules/${encodeURIComponent(code)}/activate`, { method: "POST", tenant: true, idempotencyKey }))
    .data;
}

/** POST /billing/modules/{code}/deactivate (billing.manage, Idempotency-Key): no data is deleted. */
export async function deactivateModule(api: ApiClient, code: string, idempotencyKey: string): Promise<TenantModule> {
  return (await api.request<TenantModule>(`${BASE}/modules/${encodeURIComponent(code)}/deactivate`, { method: "POST", tenant: true, idempotencyKey }))
    .data;
}

import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";

type S = components["schemas"];

export type Customer = S["CustomerResponse"];
export type CustomerDetail = S["CustomerDetailResponse"];
export type CustomerPage = S["CustomerPageResponse"];
export type Address = S["AddressResponse"];
export type DealerGroup = S["DealerGroupResponse"];
export type CreateCustomerRequest = S["CreateCustomerRequest"];
export type UpdateCustomerRequest = S["UpdateCustomerRequest"];
export type CreateAddressRequest = S["CreateAddressRequest"];
export type UpdateAddressRequest = S["UpdateAddressRequest"];
export type CreateDealerGroupRequest = S["CreateDealerGroupRequest"];
export type UpdateDealerGroupRequest = S["UpdateDealerGroupRequest"];

export { etagOf };

const MERGE_PATCH = "application/merge-patch+json";

export const CUSTOMERS_HREF = "/musteriler";
export const NEW_CUSTOMER_HREF = "/musteriler/yeni";
export const DEALER_GROUPS_HREF = "/musteriler/gruplar";

export function customerHref(id: string): string {
  return `${CUSTOMERS_HREF}/${encodeURIComponent(id)}`;
}

// ---- Customers ----

/** GET /api/v1/customers with an already-built query string (see list-query.ts). Tax numbers come masked. */
export async function listCustomers(api: ApiClient, query: string, signal?: AbortSignal): Promise<CustomerPage> {
  const suffix = query ? `?${query}` : "";
  return (await api.request<CustomerPage>(`/api/v1/customers${suffix}`, { tenant: true, signal })).data;
}

export async function getCustomer(api: ApiClient, id: string, signal?: AbortSignal): Promise<{ detail: CustomerDetail; etag: string }> {
  const response = await api.request<CustomerDetail>(`/api/v1/customers/${encodeURIComponent(id)}`, { tenant: true, signal });
  return { detail: response.data, etag: response.etag ?? etagOf(response.data.customer.version) };
}

export async function createCustomer(api: ApiClient, body: CreateCustomerRequest, idempotencyKey: string): Promise<CustomerDetail> {
  return (await api.request<CustomerDetail>("/api/v1/customers", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

/** JSON merge patch (changed members only) with If-Match. Archive = `{ status: "archived" }`. */
export async function updateCustomer(
  api: ApiClient,
  id: string,
  patch: Partial<UpdateCustomerRequest>,
  etag: string,
): Promise<{ detail: CustomerDetail; etag: string }> {
  const response = await api.request<CustomerDetail>(`/api/v1/customers/${encodeURIComponent(id)}`, {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: MERGE_PATCH,
  });
  return { detail: response.data, etag: response.etag ?? etagOf(response.data.customer.version) };
}

/** KVKK data-subject erasure: personal fields cleared, addresses removed, customer archived. Irreversible. */
export async function anonymizeCustomer(api: ApiClient, id: string, idempotencyKey: string): Promise<CustomerDetail> {
  return (
    await api.request<CustomerDetail>(`/api/v1/customers/${encodeURIComponent(id)}/anonymize`, { method: "POST", tenant: true, idempotencyKey })
  ).data;
}

// ---- Addresses ----

export async function createAddress(api: ApiClient, customerId: string, body: CreateAddressRequest, idempotencyKey: string): Promise<Address> {
  return (
    await api.request<Address>(`/api/v1/customers/${encodeURIComponent(customerId)}/addresses`, {
      method: "POST",
      tenant: true,
      body,
      idempotencyKey,
    })
  ).data;
}

/** Merge patch with If-Match `W/"<version>"`. Making an address the default clears the old default of that type. */
export async function updateAddress(api: ApiClient, address: Address, patch: Partial<UpdateAddressRequest>): Promise<Address> {
  return (
    await api.request<Address>(`/api/v1/customers/${encodeURIComponent(address.customerId)}/addresses/${encodeURIComponent(address.id)}`, {
      method: "PATCH",
      tenant: true,
      body: patch,
      ifMatch: etagOf(address.version),
      contentType: MERGE_PATCH,
    })
  ).data;
}

export async function deleteAddress(api: ApiClient, address: Address): Promise<void> {
  await api.request<void>(`/api/v1/customers/${encodeURIComponent(address.customerId)}/addresses/${encodeURIComponent(address.id)}`, {
    method: "DELETE",
    tenant: true,
  });
}

export async function listAddresses(api: ApiClient, customerId: string, signal?: AbortSignal): Promise<Address[]> {
  return (await api.request<{ items: Address[] }>(`/api/v1/customers/${encodeURIComponent(customerId)}/addresses`, { tenant: true, signal })).data
    .items;
}

// ---- Dealer groups ----

/** All dealer groups (≤ 1000, unpaged). */
export async function listDealerGroups(api: ApiClient, options: { includeArchived?: boolean; signal?: AbortSignal } = {}): Promise<DealerGroup[]> {
  const query = options.includeArchived ? "?includeArchived=true" : "";
  return (await api.request<{ items: DealerGroup[] }>(`/api/v1/dealer-groups${query}`, { tenant: true, signal: options.signal })).data.items;
}

export async function createDealerGroup(api: ApiClient, body: CreateDealerGroupRequest, idempotencyKey: string): Promise<DealerGroup> {
  return (await api.request<DealerGroup>("/api/v1/dealer-groups", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

export async function updateDealerGroup(api: ApiClient, group: DealerGroup, patch: Partial<UpdateDealerGroupRequest>): Promise<DealerGroup> {
  return (
    await api.request<DealerGroup>(`/api/v1/dealer-groups/${encodeURIComponent(group.id)}`, {
      method: "PATCH",
      tenant: true,
      body: patch,
      ifMatch: etagOf(group.version),
      contentType: MERGE_PATCH,
    })
  ).data;
}

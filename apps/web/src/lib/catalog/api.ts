import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";

type S = components["schemas"];

/**
 * `attributes` is an open JSON object (custom field key → value); the generated schema types it as an empty
 * object, so it is widened here.
 */
export type Attributes = Record<string, unknown>;

export type Variant = Omit<S["VariantResponse"], "attributes"> & { attributes: Attributes };
export type Product = Omit<S["ProductResponse"], "attributes" | "variants"> & { attributes: Attributes; variants: Variant[] };
export type Brand = S["BrandResponse"];
export type Unit = S["UnitResponse"];
export type Category = S["CategoryResponse"];
export type CustomField = S["CustomFieldResponse"];
export type PageInfo = S["PageInfo"];
export type ProductPage = { items: Product[]; page: PageInfo };
export type CreateVariantRequest = Omit<S["CreateVariantRequest"], "attributes"> & { attributes?: Attributes | null };
export type CreateProductRequest = Omit<S["CreateProductRequest"], "attributes" | "variantAttributes" | "variants"> & {
  attributes?: Attributes | null;
  variantAttributes?: Attributes | null;
  variants: CreateVariantRequest[] | null;
};
export type UpdateProductRequest = Omit<S["UpdateProductRequest"], "attributes"> & { attributes?: Attributes | null };
export type UpdateVariantRequest = Omit<S["UpdateVariantRequest"], "attributes"> & { attributes?: Attributes | null };
export type CreateCustomFieldRequest = components["schemas"]["CreateCustomFieldRequest"];
export type UpdateCustomFieldRequest = components["schemas"]["UpdateCustomFieldRequest"];

export type EntityType = "product" | "variant";

const MERGE_PATCH = "application/merge-patch+json";

/**
 * Product detail/edit page. The id travels as a query parameter (not a dynamic segment): with Cache Components
 * the (app) shell reads the pathname, which a request-time [id] segment would turn into a prerender blocker.
 */
export function productHref(id: string): string {
  return `/urunler/duzenle?id=${encodeURIComponent(id)}`;
}

/** Catalog resources carry `version`; the API's ETag is `W/"<version>"` and If-Match accepts the same form. */
export function etagOf(version: number | string): string {
  return `W/"${version}"`;
}

type ListResponse<T> = { items: T[] };

/** GET /api/v1/products with an already-built query string (see list-query.ts). */
export async function listProducts(api: ApiClient, query: string, signal?: AbortSignal): Promise<ProductPage> {
  const suffix = query ? `?${query}` : "";
  return (await api.request<ProductPage>(`/api/v1/products${suffix}`, { tenant: true, signal })).data;
}

export async function getProduct(api: ApiClient, id: string, signal?: AbortSignal): Promise<{ product: Product; etag: string }> {
  const response = await api.request<Product>(`/api/v1/products/${encodeURIComponent(id)}`, { tenant: true, signal });
  return { product: response.data, etag: response.etag ?? etagOf(response.data.version) };
}

export async function createProduct(api: ApiClient, body: CreateProductRequest, idempotencyKey: string): Promise<Product> {
  return (await api.request<Product>("/api/v1/products", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

/** JSON merge patch (only changed members). */
export async function updateProduct(
  api: ApiClient,
  id: string,
  patch: Partial<UpdateProductRequest>,
  etag: string,
): Promise<{ product: Product; etag: string }> {
  const response = await api.request<Product>(`/api/v1/products/${encodeURIComponent(id)}`, {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: MERGE_PATCH,
  });
  return { product: response.data, etag: response.etag ?? etagOf(response.data.version) };
}

export async function archiveProduct(api: ApiClient, id: string, etag: string): Promise<void> {
  await api.request<void>(`/api/v1/products/${encodeURIComponent(id)}`, { method: "DELETE", tenant: true, ifMatch: etag });
}

export async function addVariant(
  api: ApiClient,
  productId: string,
  body: CreateVariantRequest,
  idempotencyKey: string,
): Promise<Variant> {
  return (
    await api.request<Variant>(`/api/v1/products/${encodeURIComponent(productId)}/variants`, {
      method: "POST",
      tenant: true,
      body,
      idempotencyKey,
    })
  ).data;
}

export async function updateVariant(
  api: ApiClient,
  id: string,
  patch: Partial<UpdateVariantRequest>,
  etag: string,
): Promise<Variant> {
  const response = await api.request<Variant>(`/api/v1/variants/${encodeURIComponent(id)}`, {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: MERGE_PATCH,
  });
  return response.data;
}

export async function archiveVariant(api: ApiClient, id: string, etag: string): Promise<void> {
  await api.request<void>(`/api/v1/variants/${encodeURIComponent(id)}`, { method: "DELETE", tenant: true, ifMatch: etag });
}

export async function listBrands(api: ApiClient, signal?: AbortSignal): Promise<Brand[]> {
  return (await api.request<ListResponse<Brand>>("/api/v1/brands", { tenant: true, signal })).data.items;
}

export async function listUnits(api: ApiClient, signal?: AbortSignal): Promise<Unit[]> {
  return (await api.request<ListResponse<Unit>>("/api/v1/units", { tenant: true, signal })).data.items;
}

export async function listCategories(api: ApiClient, signal?: AbortSignal): Promise<Category[]> {
  return (await api.request<ListResponse<Category>>("/api/v1/categories", { tenant: true, signal })).data.items;
}

/** All custom field definitions (product and variant); archived ones only when asked. */
export async function listCustomFields(
  api: ApiClient,
  options: { includeArchived?: boolean; signal?: AbortSignal } = {},
): Promise<CustomField[]> {
  const query = options.includeArchived ? "?includeArchived=true" : "";
  return (await api.request<ListResponse<CustomField>>(`/api/v1/custom-fields${query}`, { tenant: true, signal: options.signal }))
    .data.items;
}

export async function createCustomField(api: ApiClient, body: CreateCustomFieldRequest, idempotencyKey: string): Promise<CustomField> {
  return (await api.request<CustomField>("/api/v1/custom-fields", { method: "POST", tenant: true, body, idempotencyKey })).data;
}

export async function updateCustomField(
  api: ApiClient,
  id: string,
  patch: Partial<UpdateCustomFieldRequest>,
  etag: string,
): Promise<CustomField> {
  const response = await api.request<CustomField>(`/api/v1/custom-fields/${encodeURIComponent(id)}`, {
    method: "PATCH",
    tenant: true,
    body: patch,
    ifMatch: etag,
    contentType: MERGE_PATCH,
  });
  return response.data;
}

export async function archiveCustomField(api: ApiClient, id: string, etag: string): Promise<void> {
  await api.request<void>(`/api/v1/custom-fields/${encodeURIComponent(id)}`, { method: "DELETE", tenant: true, ifMatch: etag });
}

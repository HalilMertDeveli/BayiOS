export const CUSTOMER_PAGE_SIZE = 50;

/** "" = the API default (active + inactive). */
export const CUSTOMER_STATUS_FILTERS = ["", "active", "inactive", "archived", "all"] as const;
export type CustomerStatusFilter = (typeof CUSTOMER_STATUS_FILTERS)[number];

export const customerStatusFilterLabels: Record<CustomerStatusFilter, string> = {
  "": "Aktif ve pasif",
  active: "Yalnızca aktif",
  inactive: "Yalnızca pasif",
  archived: "Arşivlenenler",
  all: "Tümü (arşiv dahil)",
};

export type CustomerListQuery = { q: string; status: CustomerStatusFilter; group: string };

export const emptyCustomerQuery: CustomerListQuery = { q: "", status: "", group: "" };

type ParamsLike = { get(name: string): string | null };

/** URL → query. Unknown status values fall back to the default. */
export function parseCustomerQuery(params: ParamsLike): CustomerListQuery {
  const status = params.get("durum") ?? "";
  return {
    q: (params.get("q") ?? "").trim(),
    status: (CUSTOMER_STATUS_FILTERS as readonly string[]).includes(status) ? (status as CustomerStatusFilter) : "",
    group: (params.get("grup") ?? "").trim(),
  };
}

/** Query → URL search string (Turkish parameter names, empty values dropped). */
export function toCustomerSearchString(query: CustomerListQuery): string {
  const params = new URLSearchParams();
  if (query.q) params.set("q", query.q);
  if (query.status) params.set("durum", query.status);
  if (query.group) params.set("grup", query.group);
  return params.toString();
}

/** Query → GET /api/v1/customers parameters. */
export function toCustomerApiQuery(query: CustomerListQuery, cursor?: string | null, limit: number = CUSTOMER_PAGE_SIZE): string {
  const params = new URLSearchParams();
  if (query.q) params.set("q", query.q);
  if (query.group) params.set("dealerGroupId", query.group);
  if (query.status === "all") params.set("status", "active,inactive,archived");
  else if (query.status) params.set("status", query.status);
  params.set("limit", String(limit));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

export function hasCustomerCriteria(query: CustomerListQuery): boolean {
  return query.q !== "" || query.status !== "" || query.group !== "";
}

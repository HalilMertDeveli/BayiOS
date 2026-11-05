import { ORDER_SOURCES, ORDER_STATUSES, orderStatusLabels } from "./labels";

export const ORDER_PAGE_SIZE = 50;

/** Status filter: one status, or a group sent as the API's comma-separated `status`. "" = every status. */
export const ORDER_STATUS_FILTERS: readonly { value: string; label: string; statuses: readonly string[] }[] = [
  { value: "", label: "Tüm durumlar", statuses: [] },
  { value: "acik", label: "Açık (taslak → paketlendi)", statuses: ["draft", "pending_approval", "approved", "picking", "packed"] },
  { value: "hazirlaniyor", label: "Depoda (onaylandı → paketlendi)", statuses: ["approved", "picking", "packed"] },
  ...ORDER_STATUSES.map((s) => ({ value: s, label: orderStatusLabels[s], statuses: [s] })),
];

export type OrderListQuery = {
  /** Order number contains (q). */
  q: string;
  status: string;
  customerId: string;
  source: string;
  /** Created on or after (yyyy-MM-dd, UTC — as the API filters). */
  from: string;
  /** Created on or before (yyyy-MM-dd, UTC). */
  to: string;
};

export const emptyOrderQuery: OrderListQuery = { q: "", status: "", customerId: "", source: "", from: "", to: "" };

type ParamsLike = { get(name: string): string | null };

const DATE = /^\d{4}-\d{2}-\d{2}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** URL → query. Unknown or malformed values are dropped (the API would answer 400 for them). */
export function parseOrderQuery(params: ParamsLike): OrderListQuery {
  const status = params.get("durum") ?? "";
  const source = params.get("kaynak") ?? "";
  const customerId = (params.get("musteri") ?? "").trim();
  const from = params.get("baslangic") ?? "";
  const to = params.get("bitis") ?? "";
  return {
    q: (params.get("q") ?? "").trim(),
    status: ORDER_STATUS_FILTERS.some((f) => f.value === status) ? status : "",
    customerId: UUID.test(customerId) ? customerId : "",
    source: (ORDER_SOURCES as readonly string[]).includes(source) ? source : "",
    from: DATE.test(from) ? from : "",
    to: DATE.test(to) ? to : "",
  };
}

/** Query → URL search string (Turkish parameter names, empty values dropped). */
export function toOrderSearchString(query: OrderListQuery): string {
  const params = new URLSearchParams();
  if (query.q) params.set("q", query.q);
  if (query.status) params.set("durum", query.status);
  if (query.customerId) params.set("musteri", query.customerId);
  if (query.source) params.set("kaynak", query.source);
  if (query.from) params.set("baslangic", query.from);
  if (query.to) params.set("bitis", query.to);
  return params.toString();
}

/** Query → GET /api/v1/orders parameters. */
export function toOrderApiQuery(query: OrderListQuery, cursor?: string | null, limit: number = ORDER_PAGE_SIZE): string {
  const params = new URLSearchParams();
  const statuses = ORDER_STATUS_FILTERS.find((f) => f.value === query.status)?.statuses ?? [];
  if (statuses.length > 0) params.set("status", statuses.join(","));
  if (query.customerId) params.set("customerId", query.customerId);
  if (query.source) params.set("source", query.source);
  if (query.from) params.set("from", query.from);
  if (query.to) params.set("to", query.to);
  if (query.q) params.set("q", query.q);
  params.set("limit", String(limit));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

export function hasOrderCriteria(query: OrderListQuery): boolean {
  return Object.values(query).some((v) => v !== "");
}

/** "from after to" is a user mistake worth flagging before asking the API (it would just return nothing). */
export function dateRangeError(query: Pick<OrderListQuery, "from" | "to">): string | null {
  return query.from && query.to && query.from > query.to ? "Başlangıç tarihi bitiş tarihinden sonra olamaz." : null;
}

import type { Category, CustomField } from "./api";

export const PAGE_SIZE = 50;
/** The API accepts at most 10 attr.* filters per request. */
export const MAX_ATTRIBUTE_FILTERS = 10;

export const STATUS_FILTERS = ["", "active", "inactive", "archived", "all"] as const;
export type StatusFilter = (typeof STATUS_FILTERS)[number];

export const statusFilterLabels: Record<StatusFilter, string> = {
  "": "Aktif ve pasif",
  active: "Yalnızca aktif",
  inactive: "Yalnızca pasif",
  archived: "Arşivlenmiş",
  all: "Tümü (arşiv dahil)",
};

export type SortOption = "" | "name";
export const sortLabels: Record<SortOption, string> = { "": "En yeni", name: "Ada göre (A–Z)" };

/** List state as it lives in the page URL (/urunler?q=…&categoryId=…&attr.guc.min=…). */
export type ProductListQuery = {
  q: string;
  categoryId: string;
  brandId: string;
  status: StatusFilter;
  sort: SortOption;
  /** `attr.<key>` / `attr.<key>.min` / `attr.<key>.max` → value, exactly as sent to the API. */
  attributes: Record<string, string>;
};

export const emptyQuery: ProductListQuery = { q: "", categoryId: "", brandId: "", status: "", sort: "", attributes: {} };

type ParamsLike = { get(name: string): string | null; forEach(cb: (value: string, key: string) => void): void };

export function parseListQuery(params: ParamsLike): ProductListQuery {
  const status = params.get("status") ?? "";
  const sort = params.get("sort") ?? "";
  const attributes: Record<string, string> = {};
  params.forEach((value, key) => {
    if (key.startsWith("attr.") && key.length > 5 && value !== "" && Object.keys(attributes).length < MAX_ATTRIBUTE_FILTERS) {
      attributes[key] = value;
    }
  });
  return {
    q: params.get("q") ?? "",
    categoryId: params.get("categoryId") ?? "",
    brandId: params.get("brandId") ?? "",
    status: (STATUS_FILTERS as readonly string[]).includes(status) ? (status as StatusFilter) : "",
    sort: sort === "name" ? "name" : "",
    attributes,
  };
}

/** Page URL search string (no leading "?"); empty values are dropped so URLs stay short and shareable. */
export function toSearchString(query: ProductListQuery): string {
  const params = new URLSearchParams();
  if (query.q.trim()) params.set("q", query.q.trim());
  if (query.categoryId) params.set("categoryId", query.categoryId);
  if (query.brandId) params.set("brandId", query.brandId);
  if (query.status) params.set("status", query.status);
  if (query.sort) params.set("sort", query.sort);
  for (const key of Object.keys(query.attributes).sort()) {
    const value = query.attributes[key].trim();
    if (value) params.set(key, value);
  }
  return params.toString();
}

/** GET /api/v1/products query string for one page. */
export function toApiQuery(query: ProductListQuery, cursor?: string | null): string {
  const params = new URLSearchParams(toSearchString({ ...query, status: "" }));
  if (query.status === "all") params.set("status", "active,inactive,archived");
  else if (query.status) params.set("status", query.status);
  params.set("limit", String(PAGE_SIZE));
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

/** Numeric filter values typed the Turkish way ("12,5") are sent as the API expects them ("12.5"). */
export function normalizeNumericFilter(value: string): string {
  const v = value.trim();
  return /^-?\d+,\d+$/.test(v) ? v.replace(",", ".") : v;
}

export function activeFilterCount(query: ProductListQuery): number {
  return [query.categoryId, query.brandId, query.status].filter(Boolean).length + Object.keys(query.attributes).length;
}

/** Filterable, non-archived definitions: these get filter inputs on the list. */
export function filterableFields(fields: readonly CustomField[]): CustomField[] {
  return fields
    .filter((f) => f.filterable && !f.isArchived)
    .sort((a, b) => a.entityType.localeCompare(b.entityType) || Number(a.sortOrder) - Number(b.sortOrder));
}

/** Categories in tree order with an indented label ("Motor › Piston" is shown as "— Piston" under "Motor"). */
export function categoryOptions(categories: readonly Category[]): { value: string; label: string }[] {
  const byParent = new Map<string | null, Category[]>();
  for (const c of categories) {
    const list = byParent.get(c.parentId ?? null) ?? [];
    list.push(c);
    byParent.set(c.parentId ?? null, list);
  }
  const ids = new Set(categories.map((c) => c.id));
  const out: { value: string; label: string }[] = [];
  const visit = (parent: string | null, depth: number) => {
    const children = (byParent.get(parent) ?? []).sort(
      (a, b) => Number(a.sortOrder) - Number(b.sortOrder) || a.name.localeCompare(b.name, "tr"),
    );
    for (const c of children) {
      out.push({ value: c.id, label: `${"  ".repeat(depth)}${depth ? "— " : ""}${c.name}` });
      visit(c.id, depth + 1);
    }
  };
  visit(null, 0);
  // Orphans (parent archived/filtered out) still need to be selectable.
  for (const c of categories) {
    if (c.parentId && !ids.has(c.parentId)) {
      out.push({ value: c.id, label: c.name });
      visit(c.id, 1);
    }
  }
  return out;
}

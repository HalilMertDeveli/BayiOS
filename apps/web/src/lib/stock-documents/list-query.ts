import { COUNT_FILTERS, TRANSFER_FILTERS, type CountFilter, type TransferFilter } from "./labels";

type ParamsLike = { get(name: string): string | null };

/** /depo/transferler and /depo/sayimlar list state in the URL (?durum=…&depo=…). */
export type TransferListQuery = { filter: TransferFilter; warehouseId: string };
export type CountListQuery = { filter: CountFilter; warehouseId: string };

const isTransferFilter = (v: string | null): v is TransferFilter => v !== null && v in TRANSFER_FILTERS;
const isCountFilter = (v: string | null): v is CountFilter => v !== null && v in COUNT_FILTERS;

export function parseTransferListQuery(params: ParamsLike): TransferListQuery {
  const durum = params.get("durum");
  return { filter: isTransferFilter(durum) ? durum : "active", warehouseId: params.get("depo") ?? "" };
}

export function toTransferListSearch(query: TransferListQuery): string {
  const params = new URLSearchParams();
  if (query.filter !== "active") params.set("durum", query.filter);
  if (query.warehouseId) params.set("depo", query.warehouseId);
  return params.toString();
}

export function parseCountListQuery(params: ParamsLike): CountListQuery {
  const durum = params.get("durum");
  return { filter: isCountFilter(durum) ? durum : "active", warehouseId: params.get("depo") ?? "" };
}

export function toCountListSearch(query: CountListQuery): string {
  const params = new URLSearchParams();
  if (query.filter !== "active") params.set("durum", query.filter);
  if (query.warehouseId) params.set("depo", query.warehouseId);
  return params.toString();
}

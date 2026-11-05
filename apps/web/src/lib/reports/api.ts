import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import type { DateRange } from "./date-range";
import type { ReportId } from "./definitions";
import { exportFilename, filenameFromContentDisposition, type ExportFormat } from "./download";

type S = components["schemas"];
export type CriticalStockReport = S["CriticalStockReportResponse"];
export type CriticalStockItem = S["CriticalStockItem"];
export type OrdersReport = S["OrdersReportResponse"];
export type TopProductsReport = S["TopProductsReportResponse"];
export type TopProductItem = S["TopProductItem"];
export type StockMovementsReport = S["StockMovementsReportResponse"];
export type PickingTasksReport = S["PickingTasksReportResponse"];
export type AiUsageReport = S["AiUsageReportResponse"];
export type ReportExportRequest = S["ReportExportRequest"];

const BASE = "/api/v1/reports";

/** Critical stock page size (the API default is 100, max 500). */
export const CRITICAL_PAGE_SIZE = 100;
export const TOP_PRODUCT_LIMITS = [10, 20, 50, 100] as const;
export const DEFAULT_TOP_PRODUCTS = 20;

function query(params: Record<string, string | number | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== null && value !== undefined && value !== "") search.set(key, String(value));
  const text = search.toString();
  return text ? `?${text}` : "";
}

async function get<T>(api: ApiClient, path: string, signal?: AbortSignal): Promise<T> {
  return (await api.request<T>(`${BASE}/${path}`, { tenant: true, signal })).data;
}

export type CriticalStockFilter = { threshold: string; warehouseId: string };

export function getCriticalStock(api: ApiClient, filter: CriticalStockFilter, cursor: string | null, signal?: AbortSignal) {
  return get<CriticalStockReport>(
    api,
    `critical-stock${query({ threshold: filter.threshold, warehouseId: filter.warehouseId, limit: CRITICAL_PAGE_SIZE, cursor })}`,
    signal,
  );
}

export function getOrdersReport(api: ApiClient, range: DateRange, statuses: readonly string[], signal?: AbortSignal) {
  return get<OrdersReport>(api, `orders${query({ from: range.from, to: range.to, status: statuses.join(",") })}`, signal);
}

export function getTopProducts(api: ApiClient, range: DateRange, limit: number, signal?: AbortSignal) {
  return get<TopProductsReport>(api, `top-products${query({ from: range.from, to: range.to, limit })}`, signal);
}

export function getStockMovements(api: ApiClient, range: DateRange, warehouseId: string, signal?: AbortSignal) {
  return get<StockMovementsReport>(api, `stock-movements${query({ from: range.from, to: range.to, warehouseId })}`, signal);
}

export function getPickingTasks(api: ApiClient, range: DateRange, warehouseId: string, signal?: AbortSignal) {
  return get<PickingTasksReport>(api, `picking-tasks${query({ from: range.from, to: range.to, warehouseId })}`, signal);
}

export function getAiUsage(api: ApiClient, range: DateRange, signal?: AbortSignal) {
  return get<AiUsageReport>(api, `ai-usage${query({ from: range.from, to: range.to })}`, signal);
}

/** Filters of an export body without the format; only the filters the report takes (others → 422). */
export type ExportFilters = Omit<ReportExportRequest, "format">;

export const EXPORT_ACCEPT =
  "text/csv, application/vnd.openxmlformats-officedocument.spreadsheetml.sheet, application/problem+json";

export type ExportedFile = { blob: Blob; filename: string };

/**
 * POST /reports/{id}/export (reports.export, or billing.read for AI usage; Idempotency-Key). The file name comes from
 * Content-Disposition when the browser may read it, else the same pattern the API uses.
 */
export async function exportReport(
  api: ApiClient,
  id: ReportId,
  format: ExportFormat,
  filters: ExportFilters,
  idempotencyKey: string,
  today: string,
): Promise<ExportedFile> {
  const response = await api.request<Blob>(`${BASE}/${id}/export`, {
    method: "POST",
    tenant: true,
    idempotencyKey,
    body: { format, ...filters },
    accept: EXPORT_ACCEPT,
    responseType: "blob",
  });
  const filename =
    filenameFromContentDisposition(response.headers?.get("Content-Disposition") ?? null) ??
    exportFilename(id, format, { from: filters.from ?? null, to: filters.to ?? null }, today);
  return { blob: response.data, filename };
}

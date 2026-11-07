import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { etagOf } from "@/lib/catalog/api";

type S = components["schemas"];

export type ImportJob = S["ImportJobResponse"];
export type ImportJobPage = S["ImportJobPageResponse"];
export type ImportRow = S["ImportRowResponse"];
export type ImportRowPage = S["ImportRowPageResponse"];
export type ImportField = S["ImportFieldResponse"];
export type ColumnSuggestion = S["ColumnSuggestion"];
export type MappingSuggestions = S["MappingSuggestionsResponse"];
export type MappingTemplate = S["MappingTemplateResponse"];
export type SaveImportMappingRequest = S["SaveImportMappingRequest"];

/** Import types the API supports today (ImportTypes): products and price list items. Stock/customer imports are not built yet. */
export const IMPORT_TYPES = ["products", "prices"] as const;
export type ImportType = (typeof IMPORT_TYPES)[number];

export const IMPORTS_HREF = "/ice-aktarma";
export const NEW_IMPORT_HREF = "/ice-aktarma/yeni";

export function importHref(id: string): string {
  return `${IMPORTS_HREF}/${encodeURIComponent(id)}`;
}

export const HISTORY_PAGE_SIZE = 20;
export const ROW_PAGE_SIZE = 50;

const jobPath = (id: string) => `/api/v1/import-jobs/${encodeURIComponent(id)}`;

/** The API sends int32/int64 as JSON numbers (the schema also allows numeric strings). */
export const num = (value: number | string | null | undefined): number => {
  const n = typeof value === "number" ? value : typeof value === "string" ? Number(value) : NaN;
  return Number.isFinite(n) ? n : 0;
};

/** History, newest first (keyset). */
export async function listImportJobs(api: ApiClient, options: { cursor?: string | null; limit?: number; signal?: AbortSignal } = {}): Promise<ImportJobPage> {
  const params = new URLSearchParams({ limit: String(options.limit ?? HISTORY_PAGE_SIZE) });
  if (options.cursor) params.set("cursor", options.cursor);
  return (await api.request<ImportJobPage>(`/api/v1/import-jobs?${params}`, { tenant: true, signal: options.signal })).data;
}

export async function getImportJob(api: ApiClient, id: string, signal?: AbortSignal): Promise<ImportJob> {
  return (await api.request<ImportJob>(jobPath(id), { tenant: true, signal })).data;
}

/** Multipart upload (`file` + `type`). The server checks size, type, content, macros, archive and row limits. */
export async function uploadImportFile(api: ApiClient, type: ImportType, file: File, idempotencyKey: string): Promise<ImportJob> {
  const form = new FormData();
  form.append("type", type);
  form.append("file", file, file.name);
  return (await api.request<ImportJob>("/api/v1/import-jobs", { method: "POST", tenant: true, body: form, idempotencyKey })).data;
}

/** Suggestions only — nothing is applied until a mapping is saved (PRD IMP-04). */
export async function getMappingSuggestions(api: ApiClient, id: string, signal?: AbortSignal): Promise<MappingSuggestions> {
  return (await api.request<MappingSuggestions>(`${jobPath(id)}/mapping-suggestions`, { tenant: true, signal })).data;
}

/** Saves the mapping (field → header) and optionally a template; discards an earlier preview. */
export async function saveImportMapping(api: ApiClient, id: string, body: SaveImportMappingRequest): Promise<ImportJob> {
  return (await api.request<ImportJob>(`${jobPath(id)}/mapping`, { method: "PUT", tenant: true, body })).data;
}

export async function validateImport(api: ApiClient, id: string, idempotencyKey: string): Promise<ImportJob> {
  return (await api.request<ImportJob>(`${jobPath(id)}/validate`, { method: "POST", tenant: true, idempotencyKey })).data;
}

export async function startImport(api: ApiClient, id: string, idempotencyKey: string): Promise<ImportJob> {
  return (await api.request<ImportJob>(`${jobPath(id)}/start`, { method: "POST", tenant: true, idempotencyKey })).data;
}

export async function cancelImport(api: ApiClient, id: string, idempotencyKey: string): Promise<ImportJob> {
  return (await api.request<ImportJob>(`${jobPath(id)}/cancel`, { method: "POST", tenant: true, idempotencyKey })).data;
}

/** Rollback (PRD IMP-12) — If-Match with the job version the user confirmed. */
export async function rollbackImport(api: ApiClient, job: Pick<ImportJob, "id" | "version">, idempotencyKey: string): Promise<ImportJob> {
  return (
    await api.request<ImportJob>(`${jobPath(job.id)}/rollback`, { method: "POST", tenant: true, idempotencyKey, ifMatch: etagOf(job.version) })
  ).data;
}

export type RowFilter = {
  /** "error" = invalid + failed rows. */
  status?: "error" | null;
  action?: "create" | "update" | "skip" | "error" | null;
  rollbackStatus?: "rolled_back" | "conflict" | "failed" | "skipped" | null;
};

export function rowQuery(filter: RowFilter, cursor: string | null, limit = ROW_PAGE_SIZE): string {
  const params = new URLSearchParams({ limit: String(limit) });
  if (filter.status) params.set("status", filter.status);
  if (filter.action) params.set("action", filter.action);
  if (filter.rollbackStatus) params.set("rollbackStatus", filter.rollbackStatus);
  if (cursor) params.set("cursor", cursor);
  return params.toString();
}

export async function listImportRows(
  api: ApiClient,
  id: string,
  filter: RowFilter,
  options: { cursor?: string | null; limit?: number; signal?: AbortSignal } = {},
): Promise<ImportRowPage> {
  return (await api.request<ImportRowPage>(`${jobPath(id)}/rows?${rowQuery(filter, options.cursor ?? null, options.limit)}`, { tenant: true, signal: options.signal }))
    .data;
}

/** Invalid and failed rows as CSV (UTF-8 BOM, `;`, formula-neutralised by the API). */
export async function downloadErrorReport(api: ApiClient, id: string): Promise<{ blob: Blob; contentDisposition: string | null }> {
  const response = await api.request<Blob>(`${jobPath(id)}/error-report`, {
    tenant: true,
    accept: "text/csv, application/problem+json",
    responseType: "blob",
  });
  return { blob: response.data, contentDisposition: response.headers?.get("Content-Disposition") ?? null };
}

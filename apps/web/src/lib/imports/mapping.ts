import { num, type ColumnSuggestion, type ImportField, type MappingTemplate, type SaveImportMappingRequest } from "./api";

/** Custom field columns target `attr.<key>` (ImportCatalogFields.AttributePrefix). */
export const ATTRIBUTE_PREFIX = "attr.";
export const PRICE_FIELD = "price";

export const isAttributeField = (key: string) => key.startsWith(ATTRIBUTE_PREFIX);

/** File header → target field key ("" = not imported). The API stores the inverse (field → header). */
export type HeaderMapping = Record<string, string>;

export type MappingSource = "saved" | "suggested" | "template" | "empty";

export type MappingDraft = {
  byHeader: HeaderMapping;
  matchKey: string;
  mode: string;
  /** Where the prefilled values came from; suggestions are shown as suggestions until the user saves. */
  source: MappingSource;
};

/** field → header (request body `columns`), unmapped headers dropped. */
export function toColumns(byHeader: HeaderMapping): Record<string, string> {
  const columns: Record<string, string> = {};
  for (const [header, field] of Object.entries(byHeader)) if (field) columns[field] = header;
  return columns;
}

/** header → field from a saved/template `columns` map; fields pointing at headers this file does not have are dropped. */
export function fromColumns(columns: Record<string, string> | null | undefined, headers: readonly string[]): HeaderMapping {
  const byHeader: HeaderMapping = Object.fromEntries(headers.map((h) => [h, ""]));
  for (const [field, header] of Object.entries(columns ?? {})) if (header in byHeader) byHeader[header] = field;
  return byHeader;
}

/** Highest-confidence suggestion per header (the API already keeps one per field). */
export function suggestionsByHeader(suggestions: readonly ColumnSuggestion[]): Record<string, ColumnSuggestion> {
  const result: Record<string, ColumnSuggestion> = {};
  for (const s of suggestions) {
    const current = result[s.header];
    if (!current || Number(s.confidence) > Number(current.confidence)) result[s.header] = s;
  }
  return result;
}

/** Prefills from suggestions; a field suggested for two headers goes to the more confident one only. */
export function fromSuggestions(suggestions: readonly ColumnSuggestion[], headers: readonly string[]): HeaderMapping {
  const byHeader: HeaderMapping = Object.fromEntries(headers.map((h) => [h, ""]));
  const ordered = [...suggestions].sort((a, b) => Number(b.confidence) - Number(a.confidence));
  const used = new Set<string>();
  for (const s of ordered) {
    if (!(s.header in byHeader) || byHeader[s.header] || used.has(s.field)) continue;
    byHeader[s.header] = s.field;
    used.add(s.field);
  }
  return byHeader;
}

/** Initial draft: a saved mapping wins (the user already confirmed it), else the API's suggestions, prefilled but editable. */
export function initialDraft(
  job: { mapping: Record<string, string> | null; matchKey: string; mode: string; headers: readonly string[] },
  suggestions: readonly ColumnSuggestion[],
): MappingDraft {
  if (job.mapping && Object.keys(job.mapping).length > 0) {
    return { byHeader: fromColumns(job.mapping, job.headers), matchKey: job.matchKey || "sku", mode: job.mode || "upsert", source: "saved" };
  }
  const byHeader = fromSuggestions(suggestions, job.headers);
  const any = Object.values(byHeader).some(Boolean);
  return { byHeader, matchKey: job.matchKey || "sku", mode: job.mode || "upsert", source: any ? "suggested" : "empty" };
}

/** Maps `header` to `field`; a field belongs to one header only, so it is taken away from any other header. */
export function assignField(byHeader: HeaderMapping, header: string, field: string): HeaderMapping {
  const next: HeaderMapping = {};
  for (const [h, f] of Object.entries(byHeader)) next[h] = field && f === field && h !== header ? "" : f;
  next[header] = field;
  return next;
}

export function applyTemplate(template: Pick<MappingTemplate, "columns" | "matchKey" | "mode">, headers: readonly string[]): MappingDraft {
  return { byHeader: fromColumns(template.columns, headers), matchKey: template.matchKey, mode: template.mode, source: "template" };
}

export function clearMapping(headers: readonly string[], draft: MappingDraft): MappingDraft {
  return { ...draft, byHeader: Object.fromEntries(headers.map((h) => [h, ""])), source: "empty" };
}

/**
 * Fields that must be mapped (ImportService.ValidateColumns): `required` always; `requiredForCreate` unless the mode is
 * update_only; plus the match key's column. Custom fields never block the mapping (their row values are checked in
 * the preview).
 */
export function requiredFieldKeys(fields: readonly ImportField[], mode: string, matchKey: string): string[] {
  const keys = fields
    .filter((f) => !isAttributeField(f.key) && (f.required || (f.requiredForCreate && mode !== "update_only")))
    .map((f) => f.key);
  const key = matchKey === "barcode" ? "barcode" : "sku";
  if (!keys.includes(key)) keys.push(key);
  return keys;
}

export function missingRequired(fields: readonly ImportField[], draft: MappingDraft): string[] {
  const mapped = new Set(Object.values(draft.byHeader).filter(Boolean));
  return requiredFieldKeys(fields, draft.mode, draft.matchKey).filter((k) => !mapped.has(k));
}

/** Price list rule (ImportService.SaveMappingAsync): prices → always; products → exactly when the price column is mapped. */
export function priceListNeed(type: string, byHeader: HeaderMapping): "required" | "forbidden" {
  if (type === "prices") return "required";
  return Object.values(byHeader).includes(PRICE_FIELD) ? "required" : "forbidden";
}

export type MappingErrors = {
  missing: string[];
  priceList?: string;
  template?: string;
  permission?: string;
};

export function validateDraft(input: {
  type: string;
  fields: readonly ImportField[];
  draft: MappingDraft;
  priceListId: string;
  saveTemplate: boolean;
  templateName: string;
  canWritePrices: boolean;
}): MappingErrors {
  const errors: MappingErrors = { missing: missingRequired(input.fields, input.draft) };
  const need = priceListNeed(input.type, input.draft.byHeader);
  if (need === "required" && !input.priceListId) errors.priceList = "Fiyatların yazılacağı fiyat listesini seçin.";
  if (need === "required" && !input.canWritePrices) {
    errors.permission =
      input.type === "prices"
        ? "Fiyat listesi aktarmak için fiyat yazma yetkiniz yok."
        : "Fiyat kolonunu aktarmak için fiyat yazma yetkiniz yok. Fiyat kolonunu “Aktarılmayacak” olarak bırakın.";
  }
  const name = input.templateName.trim();
  if (input.saveTemplate && (name.length < 1 || name.length > 100)) errors.template = "Şablon adı 1–100 karakter olmalı.";
  return errors;
}

export const hasErrors = (errors: MappingErrors) => errors.missing.length > 0 || Boolean(errors.priceList || errors.template || errors.permission);

export function buildMappingRequest(input: {
  type: string;
  draft: MappingDraft;
  priceListId: string;
  saveTemplate: boolean;
  templateName: string;
}): SaveImportMappingRequest {
  const need = priceListNeed(input.type, input.draft.byHeader);
  return {
    columns: toColumns(input.draft.byHeader),
    matchKey: input.draft.matchKey,
    mode: input.draft.mode,
    priceListId: need === "required" && input.priceListId ? input.priceListId : null,
    saveAsTemplate: input.saveTemplate ? input.templateName.trim() : null,
  };
}

/** "%92" for a 0–1 confidence. */
export const confidencePercent = (confidence: number | string) => `%${Math.round(Math.min(1, Math.max(0, num(confidence))) * 100)}`;

/** Sample values (first rows) of one column, empty cells dropped. */
export function sampleValues(headers: readonly string[], sampleRows: readonly (readonly string[])[], header: string, max = 3): string[] {
  const index = headers.indexOf(header);
  if (index < 0) return [];
  return sampleRows
    .map((r) => r[index] ?? "")
    .filter((v) => v.trim() !== "")
    .slice(0, max);
}

import type { CustomField, EntityType } from "./api";

/** Data types of custom fields (Catalog `CustomFieldDataTypes`). */
export const DATA_TYPES = ["text", "number", "integer", "boolean", "enum", "multi_enum", "range", "date"] as const;
export type DataType = (typeof DATA_TYPES)[number];

export const dataTypeLabels: Record<DataType, string> = {
  text: "Metin",
  number: "Sayı (ondalıklı)",
  integer: "Tam sayı",
  boolean: "Evet / Hayır",
  enum: "Tek seçim",
  multi_enum: "Çoklu seçim",
  range: "Aralık (en az – en çok)",
  date: "Tarih",
};

export const TEXT_MAX_LENGTH = 500;

/**
 * Form state of one attribute input. Scalars are strings exactly as typed ("" = empty; boolean "true"/"false";
 * date "yyyy-MM-dd"); multi_enum is the chosen options; range is the two typed bounds.
 */
export type AttributeFormValue = string | string[] | { min: string; max: string };
export type AttributeFormValues = Record<string, AttributeFormValue>;

export type AttributeResult = { kind: "empty" } | { kind: "value"; value: unknown } | { kind: "error"; message: string };

export function isDataType(value: string): value is DataType {
  return (DATA_TYPES as readonly string[]).includes(value);
}

/** Active (non-archived) definitions of one record type, in display order. */
export function fieldsFor(fields: readonly CustomField[], entityType: EntityType): CustomField[] {
  return fields
    .filter((f) => f.entityType === entityType && !f.isArchived)
    .sort((a, b) => Number(a.sortOrder) - Number(b.sortOrder) || a.label.localeCompare(b.label, "tr"));
}

export function emptyFormValue(field: CustomField): AttributeFormValue {
  if (field.dataType === "multi_enum") return [];
  if (field.dataType === "range") return { min: "", max: "" };
  return "";
}

/** Formats a number for a Turkish input ("12,5"). */
function numberToInput(value: unknown): string {
  return typeof value === "number" && Number.isFinite(value) ? String(value).replace(".", ",") : "";
}

/** API value (as stored) → form value. Unknown shapes become empty rather than throwing. */
export function toFormValue(field: CustomField, value: unknown): AttributeFormValue {
  if (value === undefined || value === null) return emptyFormValue(field);
  switch (field.dataType) {
    case "multi_enum":
      return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
    case "range": {
      const range = value as { min?: unknown; max?: unknown };
      return { min: numberToInput(range?.min), max: numberToInput(range?.max) };
    }
    case "boolean":
      return value === true ? "true" : value === false ? "false" : "";
    case "number":
    case "integer":
      return numberToInput(value);
    default:
      return typeof value === "string" ? value : "";
  }
}

export function toFormValues(fields: readonly CustomField[], attributes: Record<string, unknown> | null | undefined): AttributeFormValues {
  const values: AttributeFormValues = {};
  for (const field of fields) values[field.key] = toFormValue(field, attributes?.[field.key]);
  return values;
}

/** Parses "12,5", "12.5", "-3" (no thousands separators). Returns null when it is not a number. */
export function parseDecimal(input: string): number | null {
  const text = input.trim().replace(",", ".");
  if (!/^-?\d+(\.\d+)?$/.test(text)) return null;
  const n = Number(text);
  return Number.isFinite(n) ? n : null;
}

const REQUIRED = "Bu alan zorunlu.";

/** Form value → API value, with the same rules the API's CustomFieldValidator applies (in Turkish). */
export function toApiValue(field: CustomField, input: AttributeFormValue | undefined): AttributeResult {
  const value = input ?? emptyFormValue(field);
  const empty = (): AttributeResult => (field.required ? { kind: "error", message: REQUIRED } : { kind: "empty" });

  switch (field.dataType) {
    case "text": {
      const text = typeof value === "string" ? value.trim() : "";
      if (!text) return empty();
      if (text.length > TEXT_MAX_LENGTH) return { kind: "error", message: `En fazla ${TEXT_MAX_LENGTH} karakter girin.` };
      return { kind: "value", value: text };
    }
    case "number":
    case "integer": {
      const text = typeof value === "string" ? value.trim() : "";
      if (!text) return empty();
      const n = parseDecimal(text);
      if (n === null) return { kind: "error", message: "Geçerli bir sayı girin (ör. 12,5)." };
      if (field.dataType === "integer" && !Number.isInteger(n)) return { kind: "error", message: "Tam sayı girin (ör. 4)." };
      return { kind: "value", value: n };
    }
    case "boolean":
      if (value === "true") return { kind: "value", value: true };
      if (value === "false") return { kind: "value", value: false };
      return empty();
    case "enum": {
      if (typeof value !== "string" || !value) return empty();
      if (!field.options.includes(value)) return { kind: "error", message: "Listeden bir seçenek seçin." };
      return { kind: "value", value };
    }
    case "multi_enum": {
      const chosen = Array.isArray(value) ? [...new Set(value)] : [];
      if (chosen.length === 0) return empty();
      if (chosen.some((c) => !field.options.includes(c))) return { kind: "error", message: "Listeden seçenekler seçin." };
      // Keep the definition's option order so the stored value is stable.
      return { kind: "value", value: field.options.filter((o) => chosen.includes(o)) };
    }
    case "range": {
      const range = typeof value === "object" && !Array.isArray(value) ? value : { min: "", max: "" };
      const minText = range.min.trim();
      const maxText = range.max.trim();
      if (!minText && !maxText) return empty();
      const min = parseDecimal(minText);
      const max = parseDecimal(maxText);
      if (min === null || max === null) return { kind: "error", message: "En az ve en çok değerlerini sayı olarak girin." };
      if (min > max) return { kind: "error", message: "En az değer, en çok değerden büyük olamaz." };
      return { kind: "value", value: { min, max } };
    }
    case "date": {
      const text = typeof value === "string" ? value.trim() : "";
      if (!text) return empty();
      if (!/^\d{4}-\d{2}-\d{2}$/.test(text) || Number.isNaN(Date.parse(`${text}T00:00:00Z`))) {
        return { kind: "error", message: "Geçerli bir tarih girin." };
      }
      return { kind: "value", value: text };
    }
    default:
      return { kind: "error", message: "Bu alan türü desteklenmiyor." };
  }
}

export type AttributeBuild = { attributes: Record<string, unknown>; errors: Record<string, string> };

/**
 * Complete attribute object for a create request. Errors are keyed like the API's (`{prefix}.{key}`) so client
 * and server messages land on the same input.
 */
export function buildAttributes(fields: readonly CustomField[], values: AttributeFormValues, prefix: string): AttributeBuild {
  const attributes: Record<string, unknown> = {};
  const errors: Record<string, string> = {};
  for (const field of fields) {
    const result = toApiValue(field, values[field.key]);
    if (result.kind === "value") attributes[field.key] = result.value;
    if (result.kind === "error") errors[`${prefix}.${field.key}`] = result.message;
  }
  return { attributes, errors };
}

/**
 * RFC 7396 merge patch for `attributes`: changed values are set, cleared values become null, unchanged keys and
 * values of archived fields (not in `fields`) are left out.
 */
export function buildAttributePatch(
  fields: readonly CustomField[],
  values: AttributeFormValues,
  original: Record<string, unknown> | null | undefined,
  prefix: string,
): { patch: Record<string, unknown>; errors: Record<string, string> } {
  const { attributes, errors } = buildAttributes(fields, values, prefix);
  const patch: Record<string, unknown> = {};
  for (const field of fields) {
    const before = original?.[field.key];
    const after = attributes[field.key];
    if (after === undefined) {
      if (before !== undefined && before !== null) patch[field.key] = null;
    } else if (JSON.stringify(after) !== JSON.stringify(before)) {
      patch[field.key] = after;
    }
  }
  return { patch, errors };
}

/** Short, human-readable rendering of a stored value (list cells, summaries). */
export function formatAttribute(field: CustomField, value: unknown): string {
  if (value === undefined || value === null) return "";
  const withUnit = (text: string) => (field.unit ? `${text} ${field.unit}` : text);
  switch (field.dataType) {
    case "boolean":
      return value === true ? "Evet" : value === false ? "Hayır" : "";
    case "multi_enum":
      return Array.isArray(value) ? value.join(", ") : "";
    case "range": {
      const r = value as { min?: unknown; max?: unknown };
      return withUnit(`${numberToInput(r.min)}–${numberToInput(r.max)}`);
    }
    case "number":
    case "integer":
      return withUnit(numberToInput(value));
    default:
      return String(value);
  }
}

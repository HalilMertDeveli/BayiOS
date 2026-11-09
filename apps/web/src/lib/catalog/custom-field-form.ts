import type { CreateCustomFieldRequest, CustomField, EntityType, UpdateCustomFieldRequest } from "./api";
import type { DataType } from "./attributes";

export const KEY_PATTERN = /^[a-z][a-z0-9_]{0,49}$/;
export const LABEL_MAX = 100;
export const UNIT_MAX = 20;
export const OPTION_MAX = 100;
export const MAX_OPTIONS = 100;

export const hasUnit = (type: string) => type === "number" || type === "integer" || type === "range";
export const hasOptions = (type: string) => type === "enum" || type === "multi_enum";

export type CustomFieldFormValues = {
  entityType: EntityType;
  key: string;
  label: string;
  dataType: DataType;
  unit: string;
  /** One option per line. */
  options: string;
  required: boolean;
  filterable: boolean;
  sortOrder: string;
};

export const newCustomFieldValues = (entityType: EntityType = "product"): CustomFieldFormValues => ({
  entityType,
  key: "",
  label: "",
  dataType: "text",
  unit: "",
  options: "",
  required: false,
  filterable: false,
  sortOrder: "0",
});

export function customFieldToValues(field: CustomField): CustomFieldFormValues {
  return {
    entityType: field.entityType === "variant" ? "variant" : "product",
    key: field.key,
    label: field.label,
    dataType: field.dataType as DataType,
    unit: field.unit ?? "",
    options: field.options.join("\n"),
    required: field.required,
    filterable: field.filterable,
    sortOrder: String(field.sortOrder),
  };
}

const TR_ASCII: Record<string, string> = { ç: "c", ğ: "g", ı: "i", İ: "i", ö: "o", ş: "s", ü: "u", â: "a", î: "i", û: "u" };

/** Suggests a key from the label: "Güç (W)" → "guc_w". */
export function keyFromLabel(label: string): string {
  const ascii = label
    .split("")
    .map((ch) => TR_ASCII[ch] ?? TR_ASCII[ch.toLowerCase()] ?? ch)
    .join("")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^[^a-z]+/, "")
    .replace(/_+$/, "");
  return ascii.slice(0, 50);
}

function parseOptions(text: string): string[] {
  return text
    .split("\n")
    .map((o) => o.trim())
    .filter(Boolean);
}

function validateShared(values: CustomFieldFormValues, errors: Record<string, string>) {
  const label = values.label.trim();
  if (!label) errors.label = "Alan adı zorunlu.";
  else if (label.length > LABEL_MAX) errors.label = `Alan adı en fazla ${LABEL_MAX} karakter olabilir.`;

  // Unit and options only exist for some data types; values left over from another type are dropped, not errors.
  const unit = values.unit.trim();
  if (hasUnit(values.dataType) && unit.length > UNIT_MAX) errors.unit = `Birim en fazla ${UNIT_MAX} karakter olabilir.`;

  const options = parseOptions(values.options);
  if (hasOptions(values.dataType)) {
    if (options.length === 0) errors.options = "En az bir seçenek girin (her satıra bir seçenek).";
    else if (options.length > MAX_OPTIONS) errors.options = `En fazla ${MAX_OPTIONS} seçenek girilebilir.`;
    else if (options.some((o) => o.length > OPTION_MAX)) errors.options = `Her seçenek en fazla ${OPTION_MAX} karakter olabilir.`;
    else if (new Set(options).size !== options.length) errors.options = "Seçenekler birbirinden farklı olmalı.";
  }

  const sort = values.sortOrder.trim() === "" ? 0 : Number(values.sortOrder);
  if (!Number.isInteger(sort) || sort < 0 || sort > 10000) errors.sortOrder = "0 ile 10000 arasında bir tam sayı girin.";
  return { label, unit: hasUnit(values.dataType) && unit ? unit : null, options: hasOptions(values.dataType) ? options : [], sort };
}

export function buildCustomFieldCreate(values: CustomFieldFormValues): { body: CreateCustomFieldRequest; errors: Record<string, string> } {
  const errors: Record<string, string> = {};
  const key = values.key.trim();
  if (!KEY_PATTERN.test(key)) {
    errors.key = "Küçük harf ile başlayın; yalnızca küçük harf (a–z), rakam ve alt çizgi kullanın (en fazla 50).";
  }
  const shared = validateShared(values, errors);
  return {
    body: {
      entityType: values.entityType,
      key,
      label: shared.label,
      dataType: values.dataType,
      unit: shared.unit,
      options: shared.options,
      required: values.required,
      filterable: values.filterable,
      sortOrder: shared.sort,
    },
    errors,
  };
}

/** Merge patch of the editable members (key, record type and data type are fixed after creation). */
export function buildCustomFieldPatch(
  values: CustomFieldFormValues,
  original: CustomField,
): { patch: Partial<UpdateCustomFieldRequest>; errors: Record<string, string> } {
  const errors: Record<string, string> = {};
  const shared = validateShared({ ...values, dataType: original.dataType as DataType }, errors);
  const patch: Partial<UpdateCustomFieldRequest> = {};
  if (shared.label !== original.label) patch.label = shared.label;
  if (shared.unit !== (original.unit ?? null)) patch.unit = shared.unit;
  if (hasOptions(original.dataType) && JSON.stringify(shared.options) !== JSON.stringify(original.options)) patch.options = shared.options;
  if (values.required !== original.required) patch.required = values.required;
  if (values.filterable !== original.filterable) patch.filterable = values.filterable;
  if (shared.sort !== Number(original.sortOrder)) patch.sortOrder = shared.sort;
  return { patch, errors };
}

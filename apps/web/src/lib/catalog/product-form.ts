import type { CreateProductRequest, CreateVariantRequest, CustomField, Product, UpdateProductRequest, UpdateVariantRequest, Variant } from "./api";
import { buildAttributePatch, buildAttributes, emptyFormValue, parseDecimal, toFormValues, type AttributeFormValues } from "./attributes";

export const NAME_MAX = 200;
export const DESCRIPTION_MAX = 4000;
export const SKU_MAX = 64;
export const BARCODE_MAX = 64;
export const MAX_VARIANTS = 100;

export type EditableStatus = "active" | "inactive";
export const statusLabels: Record<string, string> = { active: "Aktif", inactive: "Pasif", archived: "Arşivlenmiş" };

export type VariantFormValues = { sku: string; barcode: string; name: string; status: EditableStatus; attributes: AttributeFormValues };

export type ProductFormValues = {
  name: string;
  description: string;
  categoryId: string;
  brandId: string;
  defaultUnitId: string;
  taxRate: string;
  status: EditableStatus;
  attributes: AttributeFormValues;
  /** Create only: the product's variants (a simple product has one). Existing variants are edited one by one. */
  variants: VariantFormValues[];
};

export type FormErrors = Record<string, string>;

export function emptyVariant(variantFields: readonly CustomField[]): VariantFormValues {
  return { sku: "", barcode: "", name: "", status: "active", attributes: toFormValues(variantFields, {}) };
}

export function newProductValues(
  productFields: readonly CustomField[],
  variantFields: readonly CustomField[],
  defaults: { defaultUnitId?: string; taxRate?: string } = {},
): ProductFormValues {
  const attributes: AttributeFormValues = {};
  for (const f of productFields) attributes[f.key] = emptyFormValue(f);
  return {
    name: "",
    description: "",
    categoryId: "",
    brandId: "",
    defaultUnitId: defaults.defaultUnitId ?? "",
    taxRate: defaults.taxRate ?? "20",
    status: "active",
    attributes,
    variants: [emptyVariant(variantFields)],
  };
}

const formatRate = (rate: number | string) => String(rate).replace(".", ",");

export function productToValues(product: Product, productFields: readonly CustomField[]): ProductFormValues {
  return {
    name: product.name,
    description: product.description ?? "",
    categoryId: product.categoryId ?? "",
    brandId: product.brandId ?? "",
    defaultUnitId: product.defaultUnitId,
    taxRate: formatRate(product.taxRate),
    status: product.status === "inactive" ? "inactive" : "active",
    attributes: toFormValues(productFields, product.attributes),
    variants: [],
  };
}

export function variantToValues(variant: Variant, variantFields: readonly CustomField[]): VariantFormValues {
  return {
    sku: variant.sku,
    barcode: variant.barcode ?? "",
    name: variant.name ?? "",
    status: variant.status === "inactive" ? "inactive" : "active",
    attributes: toFormValues(variantFields, variant.attributes),
  };
}

const orNull = (s: string) => (s.trim() ? s.trim() : null);

/** Same rules as the API's ValidateProductFields, in Turkish. */
function validateCore(values: ProductFormValues, errors: FormErrors): number | null {
  const name = values.name.trim();
  if (!name) errors.name = "Ürün adı zorunlu.";
  else if (name.length > NAME_MAX) errors.name = `Ürün adı en fazla ${NAME_MAX} karakter olabilir.`;
  if (values.description.trim().length > DESCRIPTION_MAX) errors.description = `Açıklama en fazla ${DESCRIPTION_MAX} karakter olabilir.`;
  if (!values.defaultUnitId) errors.defaultUnitId = "Birim seçin.";

  const rate = parseDecimal(values.taxRate);
  if (values.taxRate.trim() === "") errors.taxRate = "KDV oranı zorunlu.";
  else if (rate === null || rate < 0 || rate > 100 || Math.round(rate * 100) !== rate * 100) {
    errors.taxRate = "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin.";
  }
  return rate;
}

/** Same rules as the API's ValidateVariant; keys are `{prefix}sku` etc. */
function validateVariant(v: VariantFormValues, prefix: string, errors: FormErrors) {
  const sku = v.sku.trim();
  if (!sku) errors[`${prefix}sku`] = "SKU zorunlu.";
  else if (sku.length > SKU_MAX) errors[`${prefix}sku`] = `SKU en fazla ${SKU_MAX} karakter olabilir.`;
  const barcode = v.barcode.trim();
  if (barcode && (barcode.length > BARCODE_MAX || /\s/.test(barcode))) {
    errors[`${prefix}barcode`] = `Barkod boşluk içermemeli ve en fazla ${BARCODE_MAX} karakter olmalı.`;
  }
  if (v.name.trim().length > NAME_MAX) errors[`${prefix}name`] = `Varyant adı en fazla ${NAME_MAX} karakter olabilir.`;
}

function variantBody(v: VariantFormValues, variantFields: readonly CustomField[], attributePrefix: string, errors: FormErrors): CreateVariantRequest {
  const built = buildAttributes(variantFields, v.attributes, attributePrefix);
  Object.assign(errors, built.errors);
  return {
    sku: v.sku.trim(),
    barcode: orNull(v.barcode),
    name: orNull(v.name),
    optionValues: null,
    unitId: null,
    status: v.status,
    attributes: built.attributes,
  };
}

/** POST /api/v1/products body (always the `variants` form) plus client-side errors keyed like the API's. */
export function buildCreateRequest(
  values: ProductFormValues,
  productFields: readonly CustomField[],
  variantFields: readonly CustomField[],
): { body: CreateProductRequest; errors: FormErrors } {
  const errors: FormErrors = {};
  const rate = validateCore(values, errors);
  const product = buildAttributes(productFields, values.attributes, "attributes");
  Object.assign(errors, product.errors);

  if (values.variants.length === 0 || values.variants.length > MAX_VARIANTS) errors.variants = `1–${MAX_VARIANTS} varyant ekleyin.`;
  const variants = values.variants.map((v, i) => {
    validateVariant(v, `variants[${i}].`, errors);
    return variantBody(v, variantFields, `variants[${i}].attributes`, errors);
  });

  const skus = variants.map((v) => v.sku ?? "").filter(Boolean);
  if (new Set(skus).size !== skus.length) errors.variants ??= "Varyantların SKU'ları birbirinden farklı olmalı.";
  const barcodes = variants.map((v) => v.barcode).filter((b): b is string => !!b);
  if (new Set(barcodes).size !== barcodes.length) errors.variants ??= "Varyantların barkodları birbirinden farklı olmalı.";

  const body: CreateProductRequest = {
    name: values.name.trim(),
    description: orNull(values.description),
    categoryId: values.categoryId || null,
    brandId: values.brandId || null,
    defaultUnitId: values.defaultUnitId || null,
    taxRate: rate ?? null,
    status: values.status,
    sku: null,
    barcode: null,
    variants,
    attributes: product.attributes,
  };
  return { body, errors };
}

/** PATCH /api/v1/products/{id} merge patch: only changed members. */
export function buildProductPatch(
  values: ProductFormValues,
  original: Product,
  productFields: readonly CustomField[],
): { patch: Partial<UpdateProductRequest>; errors: FormErrors } {
  const errors: FormErrors = {};
  const rate = validateCore(values, errors);
  const patch: Partial<UpdateProductRequest> = {};

  const name = values.name.trim();
  if (name !== original.name) patch.name = name;
  const description = orNull(values.description);
  if (description !== (original.description ?? null)) patch.description = description;
  const categoryId = values.categoryId || null;
  if (categoryId !== (original.categoryId ?? null)) patch.categoryId = categoryId;
  const brandId = values.brandId || null;
  if (brandId !== (original.brandId ?? null)) patch.brandId = brandId;
  if (values.defaultUnitId && values.defaultUnitId !== original.defaultUnitId) patch.defaultUnitId = values.defaultUnitId;
  if (rate !== null && rate !== Number(original.taxRate)) patch.taxRate = rate;
  // Archived products are restored with a separate action; the status select only covers active/inactive.
  if (original.status !== "archived" && values.status !== original.status) patch.status = values.status;

  const attributes = buildAttributePatch(productFields, values.attributes, original.attributes, "attributes");
  Object.assign(errors, attributes.errors);
  if (Object.keys(attributes.patch).length > 0) patch.attributes = attributes.patch;
  return { patch, errors };
}

/** POST /api/v1/products/{id}/variants body; errors are keyed without a prefix (sku, attributes.x) like the API's. */
export function buildVariantCreate(v: VariantFormValues, variantFields: readonly CustomField[]): { body: CreateVariantRequest; errors: FormErrors } {
  const errors: FormErrors = {};
  validateVariant(v, "", errors);
  const body = variantBody(v, variantFields, "attributes", errors);
  return { body, errors };
}

export function buildVariantPatch(
  v: VariantFormValues,
  original: Variant,
  variantFields: readonly CustomField[],
): { patch: Partial<UpdateVariantRequest>; errors: FormErrors } {
  const errors: FormErrors = {};
  validateVariant(v, "", errors);
  const patch: Partial<UpdateVariantRequest> = {};
  if (v.sku.trim() !== original.sku) patch.sku = v.sku.trim();
  const barcode = orNull(v.barcode);
  if (barcode !== (original.barcode ?? null)) patch.barcode = barcode;
  const name = orNull(v.name);
  if (name !== (original.name ?? null)) patch.name = name;
  if (original.status !== "archived" && v.status !== original.status) patch.status = v.status;
  const attributes = buildAttributePatch(variantFields, v.attributes, original.attributes, "attributes");
  Object.assign(errors, attributes.errors);
  if (Object.keys(attributes.patch).length > 0) patch.attributes = attributes.patch;
  return { patch, errors };
}

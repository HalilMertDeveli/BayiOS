import type { CustomField, Product, Variant } from "./api";

/** Synthetic custom field definition for unit tests. */
export function field(overrides: Partial<CustomField> & Pick<CustomField, "key" | "dataType">): CustomField {
  return {
    id: `id-${overrides.key}`,
    entityType: "product",
    label: overrides.key,
    unit: null,
    options: [],
    required: false,
    filterable: false,
    sortOrder: 0,
    isArchived: false,
    version: 1,
    createdAt: "2026-11-01T00:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

export function variant(overrides: Partial<Variant> = {}): Variant {
  return {
    id: "v1",
    productId: "p1",
    sku: "SKU-1",
    barcode: null,
    name: null,
    optionValues: {},
    unitId: "u1",
    status: "active",
    attributes: {},
    version: 3,
    createdAt: "2026-11-01T00:00:00Z",
    createdBy: null,
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  };
}

export function product(overrides: Partial<Product> = {}): Product {
  return {
    id: "p1",
    name: "Ürün",
    description: null,
    categoryId: null,
    brandId: null,
    defaultUnitId: "u1",
    taxRate: 20,
    status: "active",
    attributes: {},
    variants: [variant()],
    version: 7,
    createdAt: "2026-11-01T00:00:00Z",
    createdBy: null,
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  };
}

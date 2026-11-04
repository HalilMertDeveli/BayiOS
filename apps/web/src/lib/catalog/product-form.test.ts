import { describe, expect, it } from "vitest";
import {
  buildCreateRequest,
  buildProductPatch,
  buildVariantCreate,
  buildVariantPatch,
  newProductValues,
  productToValues,
  variantToValues,
} from "./product-form";
import { field, product, variant } from "./test-fixtures";

const productFields = [field({ key: "guc", dataType: "number", unit: "W", required: true })];
const variantFields = [field({ key: "renk", dataType: "enum", entityType: "variant", options: ["Sarı", "Beyaz"] })];

function filled() {
  const values = newProductValues(productFields, variantFields, { defaultUnitId: "u1" });
  values.name = "  LED Panel  ";
  values.attributes.guc = "18";
  values.variants[0] = { ...values.variants[0], sku: "LED-18", barcode: "8690000000001", attributes: { renk: "Sarı" } };
  return values;
}

describe("buildCreateRequest", () => {
  it("builds the variants form with trimmed values and typed attributes", () => {
    const { body, errors } = buildCreateRequest(filled(), productFields, variantFields);
    expect(errors).toEqual({});
    expect(body).toMatchObject({
      name: "LED Panel",
      defaultUnitId: "u1",
      taxRate: 20,
      status: "active",
      sku: null,
      attributes: { guc: 18 },
      variants: [{ sku: "LED-18", barcode: "8690000000001", attributes: { renk: "Sarı" }, status: "active" }],
    });
  });

  it("reports client errors with the API's keys", () => {
    const values = filled();
    values.name = "";
    values.taxRate = "120";
    values.defaultUnitId = "";
    values.attributes.guc = "";
    values.variants[0].sku = "";
    values.variants[0].barcode = "86 90";
    const { errors } = buildCreateRequest(values, productFields, variantFields);
    expect(Object.keys(errors).sort()).toEqual(
      ["attributes.guc", "defaultUnitId", "name", "taxRate", "variants[0].barcode", "variants[0].sku"].sort(),
    );
  });

  it("rejects duplicate SKUs within the request", () => {
    const values = filled();
    values.variants.push({ ...values.variants[0], barcode: "" });
    expect(buildCreateRequest(values, productFields, variantFields).errors.variants).toMatch(/SKU/);
  });

  it("accepts a Turkish decimal tax rate and rejects 3 decimals", () => {
    const values = filled();
    values.taxRate = "8,5";
    expect(buildCreateRequest(values, productFields, variantFields).body.taxRate).toBe(8.5);
    values.taxRate = "8,555";
    expect(buildCreateRequest(values, productFields, variantFields).errors.taxRate).toBeDefined();
  });
});

describe("buildProductPatch", () => {
  const original = product({ name: "Eski", taxRate: 20, attributes: { guc: 10 } });

  it("is empty when nothing changed", () => {
    const values = productToValues(original, productFields);
    expect(buildProductPatch(values, original, productFields)).toEqual({ patch: {}, errors: {} });
  });

  it("contains only changed members (merge patch)", () => {
    const values = productToValues(original, productFields);
    values.name = "Yeni";
    values.brandId = "b1";
    values.attributes.guc = "12,5";
    values.status = "inactive";
    expect(buildProductPatch(values, original, productFields).patch).toEqual({
      name: "Yeni",
      brandId: "b1",
      status: "inactive",
      attributes: { guc: 12.5 },
    });
  });

  it("clears an optional reference with null", () => {
    const withCategory = product({ categoryId: "c1", attributes: { guc: 10 } });
    const values = productToValues(withCategory, productFields);
    values.categoryId = "";
    expect(buildProductPatch(values, withCategory, productFields).patch).toEqual({ categoryId: null });
  });

  it("never sends status for an archived product", () => {
    const archived = product({ status: "archived", attributes: { guc: 10 } });
    const values = productToValues(archived, productFields);
    expect(buildProductPatch(values, archived, productFields).patch).toEqual({});
  });
});

describe("variant builders", () => {
  it("create: errors without prefix, attributes typed", () => {
    const { body, errors } = buildVariantCreate({ sku: "", barcode: "", name: "", status: "active", attributes: { renk: "Mavi" } }, variantFields);
    expect(errors).toEqual({ sku: "SKU zorunlu.", "attributes.renk": "Listeden bir seçenek seçin." });
    expect(body.barcode).toBeNull();
  });

  it("patch: only changes", () => {
    const original = variant({ sku: "A", barcode: "1", attributes: { renk: "Sarı" } });
    const values = variantToValues(original, variantFields);
    values.barcode = "";
    values.attributes.renk = "Beyaz";
    expect(buildVariantPatch(values, original, variantFields).patch).toEqual({ barcode: null, attributes: { renk: "Beyaz" } });
  });
});

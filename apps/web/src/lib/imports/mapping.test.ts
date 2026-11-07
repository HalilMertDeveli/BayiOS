import { describe, expect, it } from "vitest";
import type { ColumnSuggestion, ImportField } from "./api";
import {
  applyTemplate,
  assignField,
  buildMappingRequest,
  confidencePercent,
  fromColumns,
  fromSuggestions,
  hasErrors,
  initialDraft,
  missingRequired,
  priceListNeed,
  requiredFieldKeys,
  sampleValues,
  toColumns,
  validateDraft,
  type MappingDraft,
} from "./mapping";

// Same flags as the API's ImportFields.Products / Prices (synthetic test data).
const f = (key: string, label: string, requiredForCreate = false, required = false): ImportField => ({ key, label, requiredForCreate, required });
const PRODUCT_FIELDS = [
  f("sku", "Stok kodu (SKU)", true),
  f("barcode", "Barkod"),
  f("name", "Ürün adı", true),
  f("unit", "Birim", true),
  f("taxRate", "KDV oranı (%)", true),
  f("price", "Fiyat"),
  f("attr.renk", "Renk", true),
];
const PRICE_FIELDS = [f("sku", "Stok kodu (SKU)"), f("barcode", "Barkod"), f("price", "Fiyat", false, true), f("minQuantity", "Minimum miktar")];
const HEADERS = ["Stok Kodu", "Ürün Adı", "Birim", "KDV", "Fiyat", "Renk", "Not"];
const s = (field: string, header: string, confidence: number): ColumnSuggestion => ({ field, header, confidence, source: "synonym" });

const draft = (byHeader: Record<string, string>, extra: Partial<MappingDraft> = {}): MappingDraft => ({
  byHeader: { ...Object.fromEntries(HEADERS.map((h) => [h, ""])), ...byHeader },
  matchKey: "sku",
  mode: "upsert",
  source: "empty",
  ...extra,
});

describe("columns conversion", () => {
  it("inverts header → field into field → header and drops unmapped headers", () => {
    expect(toColumns({ "Stok Kodu": "sku", "Ürün Adı": "name", Not: "" })).toEqual({ sku: "Stok Kodu", name: "Ürün Adı" });
  });

  it("reads a saved mapping back, ignoring headers this file does not have", () => {
    expect(fromColumns({ sku: "Stok Kodu", name: "Eski Başlık" }, ["Stok Kodu", "Ürün Adı"])).toEqual({ "Stok Kodu": "sku", "Ürün Adı": "" });
  });
});

describe("suggestions", () => {
  it("prefills each field once, at the most confident header", () => {
    const byHeader = fromSuggestions([s("name", "Not", 0.4), s("name", "Ürün Adı", 0.95), s("sku", "Stok Kodu", 0.9)], HEADERS);
    expect(byHeader["Ürün Adı"]).toBe("name");
    expect(byHeader.Not).toBe("");
    expect(byHeader["Stok Kodu"]).toBe("sku");
  });

  it("ignores suggestions for headers that are not in the file", () => {
    expect(Object.values(fromSuggestions([s("sku", "Yok", 1)], HEADERS)).every((v) => v === "")).toBe(true);
  });

  it("a saved mapping wins over suggestions; otherwise suggestions are marked as such", () => {
    const job = { mapping: { sku: "Stok Kodu" }, matchKey: "barcode", mode: "update_only", headers: HEADERS };
    expect(initialDraft(job, [s("name", "Ürün Adı", 1)])).toMatchObject({ source: "saved", matchKey: "barcode", mode: "update_only" });
    expect(initialDraft(job, [s("name", "Ürün Adı", 1)]).byHeader["Ürün Adı"]).toBe("");

    const fresh = initialDraft({ ...job, mapping: null, matchKey: "sku", mode: "upsert" }, [s("name", "Ürün Adı", 1)]);
    expect(fresh.source).toBe("suggested");
    expect(fresh.byHeader["Ürün Adı"]).toBe("name");
    expect(initialDraft({ ...job, mapping: null }, []).source).toBe("empty");
  });

  it("formats confidence as a percentage", () => {
    expect(confidencePercent(0.925)).toBe("%93");
    expect(confidencePercent("1")).toBe("%100");
  });
});

describe("editing", () => {
  it("moves a field to the newly chosen header (a field maps one header only)", () => {
    const next = assignField({ A: "sku", B: "", C: "name" }, "B", "sku");
    expect(next).toEqual({ A: "", B: "sku", C: "name" });
  });

  it("clearing a header keeps the others", () => {
    expect(assignField({ A: "sku", B: "name" }, "A", "")).toEqual({ A: "", B: "name" });
  });

  it("a template applies its columns, match key and mode", () => {
    const applied = applyTemplate({ columns: { sku: "Stok Kodu", price: "Fiyat", name: "Missing" }, matchKey: "barcode", mode: "create_only" }, HEADERS);
    expect(applied).toMatchObject({ matchKey: "barcode", mode: "create_only", source: "template" });
    expect(toColumns(applied.byHeader)).toEqual({ sku: "Stok Kodu", price: "Fiyat" });
  });
});

describe("required fields (ImportService.ValidateColumns)", () => {
  it("products upsert: SKU, name, unit, VAT; custom fields never block", () => {
    expect(requiredFieldKeys(PRODUCT_FIELDS, "upsert", "sku")).toEqual(["sku", "name", "unit", "taxRate"]);
  });

  it("update_only drops the create-only requirements but keeps the match key", () => {
    expect(requiredFieldKeys(PRODUCT_FIELDS, "update_only", "sku")).toEqual(["sku"]);
    expect(requiredFieldKeys(PRODUCT_FIELDS, "update_only", "barcode")).toEqual(["barcode"]);
  });

  it("prices: price always, plus the match key column", () => {
    expect(requiredFieldKeys(PRICE_FIELDS, "upsert", "sku")).toEqual(["price", "sku"]);
    expect(requiredFieldKeys(PRICE_FIELDS, "create_only", "barcode")).toEqual(["price", "barcode"]);
  });

  it("reports what is still missing", () => {
    expect(missingRequired(PRODUCT_FIELDS, draft({ "Stok Kodu": "sku", "Ürün Adı": "name" }))).toEqual(["unit", "taxRate"]);
  });
});

describe("price list rule and validation", () => {
  const complete = { "Stok Kodu": "sku", "Ürün Adı": "name", Birim: "unit", KDV: "taxRate" };

  it("prices always need a list; products only when the price column is mapped", () => {
    expect(priceListNeed("prices", {})).toBe("required");
    expect(priceListNeed("products", complete)).toBe("forbidden");
    expect(priceListNeed("products", { ...complete, Fiyat: "price" })).toBe("required");
  });

  it("validates the draft before any request", () => {
    const base = { type: "products", fields: PRODUCT_FIELDS, priceListId: "", saveTemplate: false, templateName: "", canWritePrices: true };
    expect(hasErrors(validateDraft({ ...base, draft: draft(complete) }))).toBe(false);
    expect(validateDraft({ ...base, draft: draft({ ...complete, Fiyat: "price" }) }).priceList).toBeTruthy();
    expect(validateDraft({ ...base, draft: draft({ ...complete, Fiyat: "price" }), priceListId: "pl-1", canWritePrices: false }).permission).toMatch(/yetkiniz yok/);
    expect(validateDraft({ ...base, draft: draft(complete), saveTemplate: true, templateName: "  " }).template).toBeTruthy();
  });

  it("builds the request: field → header, list only when needed, trimmed template name", () => {
    const body = buildMappingRequest({ type: "products", draft: draft(complete), priceListId: "pl-1", saveTemplate: true, templateName: " Tedarikçi A " });
    expect(body).toEqual({
      columns: { sku: "Stok Kodu", name: "Ürün Adı", unit: "Birim", taxRate: "KDV" },
      matchKey: "sku",
      mode: "upsert",
      priceListId: null,
      saveAsTemplate: "Tedarikçi A",
    });
    expect(buildMappingRequest({ type: "prices", draft: draft({ "Stok Kodu": "sku", Fiyat: "price" }), priceListId: "pl-1", saveTemplate: false, templateName: "x" })).toMatchObject({
      priceListId: "pl-1",
      saveAsTemplate: null,
    });
  });
});

describe("samples", () => {
  it("returns the first non-empty values of a column", () => {
    expect(sampleValues(["A", "B"], [["1", ""], ["2", "x"], ["", "y"], ["4", "z"]], "A")).toEqual(["1", "2", "4"]);
    expect(sampleValues(["A"], [["1"]], "Z")).toEqual([]);
  });
});

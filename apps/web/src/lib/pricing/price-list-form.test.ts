import { describe, expect, it } from "vitest";
import type { PriceList } from "./api";
import {
  baseListCandidates,
  buildCreatePriceList,
  buildPriceListPatch,
  newPriceListValues,
  parsePercent,
  priceListToValues,
  validatePriceList,
} from "./price-list-form";

function list(overrides: Partial<PriceList> = {}): PriceList {
  return {
    id: "l1",
    name: "Perakende",
    kind: "sales",
    currency: "TRY",
    isDefault: false,
    pricesIncludeTax: false,
    validFrom: null,
    validTo: null,
    basePriceListId: null,
    discountPercent: null,
    status: "active",
    version: 3,
    createdAt: "2026-11-01T00:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

describe("price list form", () => {
  it("validates name, date order and base + discount together", () => {
    const v = { ...newPriceListValues(), name: " ", validFrom: "2026-12-01", validTo: "2026-11-01", basePriceListId: "b1" };
    expect(validatePriceList(v)).toEqual({
      name: "Liste adı girin.",
      validTo: "Bitiş tarihi başlangıç tarihinden önce olamaz.",
      discountPercent: "Taban listeden türetmek için iskonto oranını girin (0 olabilir).",
    });
    expect(validatePriceList({ ...newPriceListValues(), name: "A", discountPercent: "5" })).toHaveProperty("basePriceListId");
    expect(validatePriceList({ ...newPriceListValues(), name: "A", basePriceListId: "b1", discountPercent: "100,01" })).toHaveProperty("discountPercent");
    expect(validatePriceList({ ...newPriceListValues(), name: "A", basePriceListId: "b1", discountPercent: "12,5" })).toEqual({});
  });

  it("parses percents as text (0–100, ≤ 2 decimals)", () => {
    expect(parsePercent("12,5")).toBe("12.5");
    expect(parsePercent("100")).toBe("100");
    expect(parsePercent("100,5")).toBeNull();
    expect(parsePercent("1,234")).toBeNull();
  });

  it("builds a create body with decimals as strings; purchase lists never default or derive", () => {
    expect(buildCreatePriceList({ ...newPriceListValues("EUR"), name: " Bayi A ", isDefault: true, basePriceListId: "b1", discountPercent: "7,5" })).toEqual({
      name: "Bayi A",
      kind: "sales",
      currency: "EUR",
      isDefault: true,
      pricesIncludeTax: false,
      validFrom: null,
      validTo: null,
      basePriceListId: "b1",
      discountPercent: "7.5",
    });
    const purchase = buildCreatePriceList({ ...newPriceListValues(), name: "Alış", kind: "purchase", isDefault: true, basePriceListId: "b1", discountPercent: "5" });
    expect(purchase.isDefault).toBe(false);
    expect(purchase.basePriceListId).toBeNull();
    expect(purchase.discountPercent).toBeNull();
  });

  it("patches only changed members; base and discount travel together; isDefault only as true", () => {
    const original = list({ basePriceListId: "b1", discountPercent: 10 });
    const values = priceListToValues(original);
    expect(buildPriceListPatch(original, values)).toEqual({});
    expect(buildPriceListPatch(original, { ...values, discountPercent: "10,00" })).toEqual({});
    expect(buildPriceListPatch(original, { ...values, discountPercent: "12" })).toEqual({ basePriceListId: "b1", discountPercent: "12" });
    expect(buildPriceListPatch(original, { ...values, basePriceListId: "", discountPercent: "" })).toEqual({ basePriceListId: null, discountPercent: null });
    expect(buildPriceListPatch(original, { ...values, isDefault: true, validTo: "2026-12-31", status: "inactive" })).toEqual({
      isDefault: true,
      validTo: "2026-12-31",
      status: "inactive",
    });
    const defaultList = list({ isDefault: true });
    expect(buildPriceListPatch(defaultList, { ...priceListToValues(defaultList), isDefault: false })).toEqual({});
  });

  it("offers only same-currency, same-VAT, non-derived, non-archived sales lists as base", () => {
    const lists = [
      list({ id: "self" }),
      list({ id: "ok", name: "Taban" }),
      list({ id: "eur", currency: "EUR" }),
      list({ id: "vat", pricesIncludeTax: true }),
      list({ id: "derived", basePriceListId: "ok", discountPercent: 5 }),
      list({ id: "arch", status: "archived" }),
      list({ id: "buy", kind: "purchase" }),
    ];
    const values = priceListToValues(list({ id: "self" }));
    expect(baseListCandidates(lists, values, "self").map((l) => l.id)).toEqual(["ok"]);
  });
});

import { describe, expect, it } from "vitest";
import { criticalStockQuery } from "@/lib/reports/api";
import {
  bulkErrorsByKey,
  defaultThresholds,
  effectiveMinimum,
  parseThreshold,
  planThresholds,
  thresholdInput,
  thresholdKey,
  type StockThreshold,
} from "./thresholds";

const t = (id: string, variantId: string, warehouseId: string | null, minQuantity: number | string, version = 1): StockThreshold => ({
  id,
  variantId,
  warehouseId,
  minQuantity,
  version,
  createdAt: "2026-11-07T10:00:00Z",
  updatedAt: null,
});

describe("minimum stock thresholds (#137)", () => {
  it("parses typed minimums: empty = none, Turkish comma, whole numbers for units without fractions", () => {
    expect(parseThreshold("", false)).toEqual({ ok: true, value: null });
    expect(parseThreshold("  ", true)).toEqual({ ok: true, value: null });
    expect(parseThreshold("12", false)).toEqual({ ok: true, value: "12" });
    expect(parseThreshold("2,5", true)).toEqual({ ok: true, value: "2.5" });
    expect(parseThreshold("0", false)).toEqual({ ok: true, value: "0" });
    expect(parseThreshold("2,5", false).ok).toBe(false);
    expect(parseThreshold("1,2345", true).ok).toBe(false);
    expect(parseThreshold("-1", true).ok).toBe(false);
    expect(parseThreshold("abc", true).ok).toBe(false);
    expect(parseThreshold("1000000001", false)).toEqual({ ok: false, message: "En fazla 1.000.000.000 girilebilir." });
    expect(thresholdInput("2.5")).toBe("2,5");
    expect(thresholdInput(null)).toBe("");
  });

  it("override wins in its warehouse, else the general minimum", () => {
    const all = [t("a", "v1", null, 5), t("b", "v1", "w2", 1), t("c", "v2", "w1", 3)];
    expect(effectiveMinimum(all, "v1", "w1")).toBe(5);
    expect(effectiveMinimum(all, "v1", "w2")).toBe(1);
    expect(effectiveMinimum(all, "v2", "w1")).toBe(3);
    expect(effectiveMinimum(all, "v2", "w2")).toBeNull();
    expect([...defaultThresholds(all).keys()]).toEqual(["v1"]);
  });

  it("plans only changed rows: set, update, remove; unchanged and empty-without-row send nothing", () => {
    const current = [t("a", "v1", null, 5), t("b", "v2", null, "3.000"), t("c", "v3", "w1", 2)];
    const { items, errors } = planThresholds(
      [
        { variantId: "v1", text: "5", allowsFraction: false }, // unchanged
        { variantId: "v2", text: "4", allowsFraction: false }, // update
        { variantId: "v3", warehouseId: "w1", text: "", allowsFraction: false }, // remove the override
        { variantId: "v4", text: "", allowsFraction: false }, // nothing to remove
        { variantId: "v5", text: "1,5", allowsFraction: true }, // new
      ],
      current,
    );
    expect(errors).toEqual({});
    expect(items).toEqual([
      { variantId: "v2", warehouseId: null, minQuantity: "4", remove: null },
      { variantId: "v3", warehouseId: "w1", minQuantity: null, remove: true },
      { variantId: "v5", warehouseId: null, minQuantity: "1.5", remove: null },
    ]);
  });

  it("collects input errors by (variant, warehouse) and maps bulk 422 rows back to their input", () => {
    const { items, errors } = planThresholds(
      [
        { variantId: "v1", text: "1,5", allowsFraction: false },
        { variantId: "v2", warehouseId: "w9", text: "x", allowsFraction: true },
      ],
      [],
    );
    expect(items).toEqual([]);
    expect(Object.keys(errors)).toEqual(["v1", thresholdKey("v2", "w9")]);
    expect(errors.v1).toMatch(/kesirli olamaz/);

    const sent = [
      { variantId: "v1", warehouseId: null, minQuantity: "2", remove: null },
      { variantId: "v2", warehouseId: "w9", minQuantity: "3", remove: null },
    ];
    expect(bulkErrorsByKey(sent, { "items[1].warehouseId": "Depo bulunamadı.", items: "x" })).toEqual({ "v2@w9": "Depo bulunamadı." });
  });

  it("critical-stock query: no threshold by default, includeOutOfStock only without a fixed threshold", () => {
    expect(criticalStockQuery({ threshold: null, warehouseId: "" }, null)).toBe("?limit=100");
    expect(criticalStockQuery({ threshold: null, warehouseId: "w1", includeOutOfStock: true }, "c1")).toBe(
      "?warehouseId=w1&includeOutOfStock=true&limit=100&cursor=c1",
    );
    expect(criticalStockQuery({ threshold: "2.5", warehouseId: "", includeOutOfStock: true }, null)).toBe("?threshold=2.5&limit=100");
  });
});

import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { itemRowErrors, pricingErrorMessage, pricingFieldErrors } from "./errors";
import { buildUpsertItems, draftFromItem, newDraftRow, validateDraftRows } from "./items-form";

const v1 = { id: "v1", sku: "LED-1", name: null, barcode: null };
const v2 = { id: "v2", sku: "LED-2", name: null, barcode: null };

describe("price list items editor", () => {
  it("builds the bulk upsert in row order with decimals as strings", () => {
    const rows = [
      { ...newDraftRow(v1), unitPrice: "12,50" },
      { ...newDraftRow(v1, "10"), unitPrice: "11" },
    ];
    expect(buildUpsertItems(rows)).toEqual([
      { variantId: "v1", minQuantity: "1", unitPrice: "12.50" },
      { variantId: "v1", minQuantity: "10", unitPrice: "11" },
    ]);
  });

  it("flags missing/invalid prices, zero quantities and duplicate tiers per row", () => {
    const rows = [
      { ...newDraftRow(v1), unitPrice: "" },
      { ...newDraftRow(v1, "0"), unitPrice: "5" },
      { ...newDraftRow(v2, "1,000"), unitPrice: "1,23456" },
      { ...newDraftRow(v2, "1"), unitPrice: "2" },
    ];
    const result = validateDraftRows(rows);
    expect(result.general).toEqual([]);
    expect(result.rows[0]).toEqual({ unitPrice: "Birim fiyat girin." });
    expect(result.rows[1]).toEqual({ minQuantity: "0'dan büyük, en fazla 3 ondalıklı bir miktar girin." });
    expect(result.rows[2]).toEqual({ unitPrice: "0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin." });
    // "1,000" and "1" are the same tier.
    expect(result.rows[3]).toEqual({ row: "Bu ürün için bu en az miktar zaten 3. satırda var." });
  });

  it("accepts a zero price (a free item is a price, not 'no price')", () => {
    expect(validateDraftRows([{ ...newDraftRow(v1), unitPrice: "0" }])).toEqual({ rows: {}, general: [] });
  });

  it("requires at least one row", () => {
    expect(validateDraftRows([]).general).toHaveLength(1);
  });

  it("prefills an existing item for repricing", () => {
    const row = draftFromItem(
      { id: "i1", priceListId: "l1", variantId: "v1", minQuantity: 10, unitPrice: "9.5", version: 1, createdAt: "", updatedAt: null },
      v1,
    );
    expect(row).toMatchObject({ itemId: "i1", minQuantity: "10", unitPrice: "9,5" });
  });

  it("maps a 422 per row (items[i].field) and keeps other keys general", () => {
    const error = new ApiError("x", 422, {
      code: "validation.failed",
      errors: {
        "items[1].unitPrice": ["Required: 0 or more with at most 4 decimals."],
        "items[2].variantId": ["Variant not found."],
        "items[3]": ["Duplicate variantId + minQuantity in this request."],
        items: ["Must contain 1–500 items."],
      },
    });
    const mapped = itemRowErrors(pricingFieldErrors(error), (i) => i);
    expect(mapped.rows).toEqual({
      1: { unitPrice: "0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin." },
      2: { variantId: "Ürün bulunamadı. Satırı silip ürünü yeniden seçin." },
      3: { row: "Aynı ürün ve en az miktar bu listede iki kez var." },
    });
    expect(mapped.general).toEqual(["En az bir satır girin (tek seferde en fazla 500)."]);
  });

  it("translates pricing conflicts", () => {
    expect(pricingErrorMessage(new ApiError("x", 409, { code: "pricing.list_archived" }))).toBe("Arşivlenmiş fiyat listesi değiştirilemez.");
    expect(pricingErrorMessage(new ApiError("x", 409, { code: "pricing.base_chain" }))).toMatch(/tek seviyelidir/);
    expect(pricingErrorMessage(new ApiError("x", 412, { code: "concurrency.version_mismatch" }))).toMatch(/değişti/);
    expect(pricingErrorMessage(new Error("boom"))).toMatch(/Beklenmeyen/);
  });
});

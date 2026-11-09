import { describe, expect, it } from "vitest";
import { addVariantLine, buildReceiptRequest, receiptTotal, validateReceipt, type ReceiptFormValues, type VariantRef } from "./receipt-form";

const A: VariantRef = { id: "v-a", sku: "MOCK-A", name: null, barcode: "2000000000011" };
const B: VariantRef = { id: "v-b", sku: "MOCK-B", name: "Mavi", barcode: null };

const keys = () => {
  let n = 0;
  return () => `k${++n}`;
};

describe("addVariantLine (scan → line)", () => {
  it("adds a new variant with quantity 1 and increments a repeated scan on the same location", () => {
    const makeKey = keys();
    let { lines } = addVariantLine([], A, "", makeKey);
    ({ lines } = addVariantLine(lines, B, "", makeKey));
    const repeat = addVariantLine(lines, A, "", makeKey);
    expect(repeat.index).toBe(0);
    expect(repeat.lines.map((l) => [l.variant.sku, l.quantity])).toEqual([
      ["MOCK-A", "2"],
      ["MOCK-B", "1"],
    ]);
  });

  it("keeps a Turkish decimal when incrementing and treats another location as a new line", () => {
    const makeKey = keys();
    const lines = [{ key: "x", variant: A, quantity: "1,5", locationId: "loc-1" }];
    expect(addVariantLine(lines, A, "loc-1", makeKey).lines[0].quantity).toBe("2,5");
    const other = addVariantLine(lines, A, "loc-2", makeKey);
    expect(other.lines).toHaveLength(2);
    expect(other.index).toBe(1);
  });
});

describe("validateReceipt / buildReceiptRequest", () => {
  const values: ReceiptFormValues = {
    warehouseId: "wh-1",
    reference: "  IRS-2026-001 ",
    lines: [
      { key: "1", variant: A, quantity: "12,5", locationId: "loc-1" },
      { key: "2", variant: B, quantity: "0", locationId: "" },
    ],
  };

  it("uses the API's error keys per line", () => {
    expect(validateReceipt(values, { usesLocations: true })).toEqual({
      "lines[1].quantity": "Miktar 0'dan büyük olmalı.",
      "lines[1].locationId": "Raf/göz seçin.",
    });
    expect(validateReceipt({ ...values, lines: [] }, { usesLocations: false })).toEqual({ lines: "En az bir ürün okutun veya arayıp ekleyin." });
    expect(validateReceipt({ ...values, reference: "x".repeat(201), lines: [values.lines[0]] }, { usesLocations: false })).toHaveProperty("reference");
  });

  it("builds the contract body: numbers, trimmed reference, locations only when the warehouse uses them", () => {
    const ok = { ...values, lines: [values.lines[0], { ...values.lines[1], quantity: "3" }] };
    expect(buildReceiptRequest(ok, { usesLocations: false })).toEqual({
      warehouseId: "wh-1",
      reference: "IRS-2026-001",
      lines: [
        { variantId: "v-a", locationId: null, quantity: 12.5, unitCost: null },
        { variantId: "v-b", locationId: null, quantity: 3, unitCost: null },
      ],
    });
    expect(buildReceiptRequest(ok, { usesLocations: true }).lines?.[0].locationId).toBe("loc-1");
    expect(buildReceiptRequest({ ...ok, warehouseId: "", reference: " " }, { usesLocations: false })).toMatchObject({ warehouseId: null, reference: null });
    expect(receiptTotal(ok.lines)).toBe(15.5);
  });
});

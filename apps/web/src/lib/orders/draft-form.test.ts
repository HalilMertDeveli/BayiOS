import { describe, expect, it } from "vitest";
import {
  buildCreateRequest,
  buildLineInputs,
  buildUpdatePatch,
  draftValuesFromDetail,
  isDirty,
  newDraftLine,
  newDraftValues,
  validateDraft,
  type DraftValues,
} from "./draft-form";
import { orderDetail, orderLine } from "./test-fixtures";

const variant = { id: "var-9", sku: "LED-9", name: null, barcode: null };
const manager = { manualPrice: true, discount: true };
const sales = { manualPrice: false, discount: false };
/** Sales under a tenant discount limit (orders.price.override conditional): discounts yes, manual price no. */
const limitedSales = { manualPrice: false, discount: true };

function withLine(patch: Partial<ReturnType<typeof newDraftLine>> = {}): DraftValues {
  return { ...newDraftValues("wh-1"), customer: { id: "cus-1", name: "Müşteri", code: "C1" }, lines: [{ ...newDraftLine(variant), ...patch }] };
}

describe("validateDraft", () => {
  it("needs a customer and positive quantities with ≤ 3 decimals", () => {
    const values = { ...withLine({ quantity: "0" }), customer: null };
    expect(validateDraft(values, sales)).toEqual({ customerId: "Müşteri seçin.", "lines[0].quantity": "0'dan büyük, en fazla 3 ondalıklı bir miktar girin." });
    expect(validateDraft(withLine({ quantity: "1,234" }), sales)).toEqual({});
    expect(validateDraft(withLine({ quantity: "1,2345" }), sales)).toHaveProperty(["lines[0].quantity"]);
  });

  it("checks manual price and discount only when the role may override", () => {
    const bad = withLine({ unitPrice: "abc", discountMode: "percent", discount: "100,01" });
    expect(validateDraft(bad, sales)).toEqual({});
    expect(Object.keys(validateDraft(bad, manager))).toEqual(["lines[0].unitPrice", "lines[0].discountPercent"]);
    expect(validateDraft(withLine({ discountMode: "percent", discount: "100" }), manager)).toEqual({});
    expect(validateDraft(withLine({ discountMode: "amount", discount: "5,555" }), manager)).toHaveProperty(["lines[0].discountAmount"]);
  });
});

describe("buildLineInputs / buildCreateRequest", () => {
  it("sends decimals as normalised strings and never a manual price or discount without override", () => {
    const values = withLine({ quantity: "2,5", unitPrice: "99,90", discountMode: "percent", discount: "5" });
    expect(buildLineInputs(values, sales)).toEqual([{ id: null, variantId: "var-9", quantity: "2.5", unitPrice: null, discountPercent: null, discountAmount: null }]);
    expect(buildLineInputs(values, manager)).toEqual([{ id: null, variantId: "var-9", quantity: "2.5", unitPrice: "99.90", discountPercent: "5", discountAmount: null }]);
  });

  it("a limited discount grant (sales, K¹⁰) sends discounts but never a manual price", () => {
    const values = withLine({ quantity: "2", unitPrice: "99,90", discountMode: "percent", discount: "5" });
    expect(buildLineInputs(values, limitedSales)).toEqual([{ id: null, variantId: "var-9", quantity: "2", unitPrice: null, discountPercent: "5", discountAmount: null }]);
    expect(validateDraft(withLine({ unitPrice: "abc", discountMode: "percent", discount: "101" }), limitedSales)).toEqual({
      "lines[0].discountPercent": "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin.",
    });
  });

  it("the /me discount cap (maxDiscountPercent) is checked before the API is asked", () => {
    const capped = { ...limitedSales, maxDiscountPercent: 10 };
    expect(validateDraft(withLine({ discountMode: "percent", discount: "10" }), capped)).toEqual({});
    expect(validateDraft(withLine({ discountMode: "percent", discount: "10,5" }), capped)).toEqual({ "lines[0].discountPercent": "İskonto en fazla %10 olabilir." });
    // Amounts depend on the line total (API-side check only).
    expect(validateDraft(withLine({ discountMode: "amount", discount: "500" }), capped)).toEqual({});
  });

  it("sends the chosen addresses; '' (the customer's default) is null", () => {
    expect(buildCreateRequest({ ...withLine(), shippingAddressId: "adr-2" }, sales)).toMatchObject({ shippingAddressId: "adr-2", billingAddressId: null });
  });

  it("creates a manual draft; empty optional fields are null", () => {
    expect(buildCreateRequest(withLine(), sales)).toEqual({
      customerId: "cus-1",
      warehouseId: "wh-1",
      source: "manual",
      sourceReferenceId: null,
      currency: null,
      pricesIncludeTax: null,
      requestedDeliveryDate: null,
      notes: null,
      lines: [{ id: null, variantId: "var-9", quantity: "1", unitPrice: null, discountPercent: null, discountAmount: null }],
      shippingAddressId: null,
      billingAddressId: null,
    });
  });
});

describe("draft edit (merge patch)", () => {
  const detail = orderDetail([
    orderLine(),
    orderLine({ id: "line-2", lineNo: 2, variantId: "var-2", sku: "LED-2", priceOverridden: true, unitPrice: "75.5", discountPercent: "5" }),
  ]);

  it("loads a manual price only for overridden lines, and the line's discount", () => {
    const values = draftValuesFromDetail(detail);
    expect(values.lines.map((l) => [l.id, l.quantity, l.unitPrice, l.discountMode, l.discount])).toEqual([
      ["line-1", "2", "", "none", ""],
      ["line-2", "2", "75,5", "percent", "5"],
    ]);
    expect(values.taxMode).toBe("excluded");
  });

  it("an unchanged form is not dirty and produces an empty patch", () => {
    const original = draftValuesFromDetail(detail);
    expect(buildUpdatePatch(original, original, manager)).toEqual({});
    expect(isDirty(original, original, manager)).toBe(false);
  });

  it("header-only changes do not resend (and re-price) the lines", () => {
    const original = draftValuesFromDetail(detail);
    expect(buildUpdatePatch(original, { ...original, notes: " Kapıda teslim ", requestedDeliveryDate: "2026-11-10" }, manager)).toEqual({
      notes: "Kapıda teslim",
      requestedDeliveryDate: "2026-11-10",
    });
  });

  it("address choices: loaded from the order, patched only when changed, back to the default as null", () => {
    const pinned = orderDetail([orderLine()], { shippingAddressId: "adr-2", billingAddressId: null });
    const original = draftValuesFromDetail(pinned);
    expect([original.shippingAddressId, original.billingAddressId]).toEqual(["adr-2", ""]);
    expect(buildUpdatePatch(original, { ...original, billingAddressId: "adr-9" }, manager)).toEqual({ billingAddressId: "adr-9" });
    // Another customer: the form clears both choices; the API then uses that customer's defaults.
    const otherCustomer = { ...original, customer: { id: "cus-2", name: null, code: null }, shippingAddressId: "", billingAddressId: "" };
    expect(buildUpdatePatch(original, otherCustomer, manager)).toEqual({ customerId: "cus-2", shippingAddressId: null });
  });

  it("a changed line sends every line with its id (lines replace all)", () => {
    const original = draftValuesFromDetail(detail);
    const edited = { ...original, lines: original.lines.map((l, i) => (i === 0 ? { ...l, quantity: "3" } : l)) };
    const patch = buildUpdatePatch(original, edited, manager);
    expect(patch.lines).toEqual([
      { id: "line-1", variantId: "var-1", quantity: "3", unitPrice: null, discountPercent: null, discountAmount: null },
      { id: "line-2", variantId: "var-2", quantity: "2", unitPrice: "75.5", discountPercent: "5", discountAmount: null },
    ]);
  });

  it("removing every line sends an empty array", () => {
    const original = draftValuesFromDetail(detail);
    expect(buildUpdatePatch(original, { ...original, lines: [] }, manager)).toEqual({ lines: [] });
  });
});

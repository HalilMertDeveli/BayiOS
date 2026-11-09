import { describe, expect, it } from "vitest";
import { buildAdjustmentRequest, emptyAdjustment, validateAdjustment, type AdjustmentFormValues } from "./adjustment-form";

const filled: AdjustmentFormValues = {
  variant: { id: "v-1", sku: "MOCK-1", name: null, barcode: null },
  warehouseId: "wh-1",
  locationId: "loc-1",
  type: "damage_out",
  quantity: "2,5",
  reason: "  Kırık geldi ",
};

describe("validateAdjustment", () => {
  it("requires variant, warehouse, type, quantity and a reason", () => {
    expect(Object.keys(validateAdjustment(emptyAdjustment(), { usesLocations: false })).sort()).toEqual([
      "quantity",
      "reason",
      "type",
      "variantId",
      "warehouseId",
    ]);
  });

  it("requires a location only in warehouses that use locations", () => {
    const noLocation = { ...filled, locationId: "" };
    expect(validateAdjustment(noLocation, { usesLocations: true })).toEqual({ locationId: "Raf/göz seçin." });
    expect(validateAdjustment(noLocation, { usesLocations: false })).toEqual({});
  });

  it("limits the reason to 500 characters", () => {
    expect(validateAdjustment({ ...filled, reason: "x".repeat(501) }, { usesLocations: true })).toHaveProperty("reason");
  });
});

describe("buildAdjustmentRequest", () => {
  it("builds the body without any negative-stock confirmation (OD-09: stock never goes below zero)", () => {
    expect(buildAdjustmentRequest(filled, { usesLocations: true })).toEqual({
      variantId: "v-1",
      warehouseId: "wh-1",
      locationId: "loc-1",
      type: "damage_out",
      quantity: 2.5,
      reason: "Kırık geldi",
    });
    const located = buildAdjustmentRequest(filled, { usesLocations: false });
    expect(located.locationId).toBeNull();
    expect(located).not.toHaveProperty("confirmNegativeStock");
  });
});

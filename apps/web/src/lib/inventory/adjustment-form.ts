import type { CreateAdjustmentRequest } from "./api";
import { ADJUSTMENT_TYPES, type AdjustmentType } from "./labels";
import { parseQuantity } from "./quantity";
import type { VariantRef } from "./receipt-form";

export const REASON_MAX = 500;

export type AdjustmentFormValues = {
  variant: VariantRef | null;
  warehouseId: string;
  locationId: string;
  type: AdjustmentType | "";
  quantity: string;
  reason: string;
};

export const emptyAdjustment = (warehouseId = ""): AdjustmentFormValues => ({
  variant: null,
  warehouseId,
  locationId: "",
  type: "",
  quantity: "",
  reason: "",
});

/** Keys match the API's error keys (variantId, warehouseId, locationId, type, quantity, reason). */
export function validateAdjustment(values: AdjustmentFormValues, options: { usesLocations: boolean }): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!values.variant) errors.variantId = "Ürünü okutun veya arayıp seçin.";
  if (!values.warehouseId) errors.warehouseId = "Depo seçin.";
  if (options.usesLocations && !values.locationId) errors.locationId = "Raf/göz seçin.";
  if (!values.type || !(ADJUSTMENT_TYPES as readonly string[]).includes(values.type)) errors.type = "Düzeltme türünü seçin.";
  const q = parseQuantity(values.quantity);
  if (!q.ok) errors.quantity = q.message;
  const reason = values.reason.trim();
  if (!reason) errors.reason = "Düzeltme sebebini yazın.";
  else if (reason.length > REASON_MAX) errors.reason = `En fazla ${REASON_MAX} karakter girin.`;
  return errors;
}

/** Request body. No negative-stock confirmation exists any more: stock never goes below zero (OD-09). */
export function buildAdjustmentRequest(
  values: AdjustmentFormValues,
  options: { usesLocations: boolean },
): CreateAdjustmentRequest {
  const q = parseQuantity(values.quantity);
  return {
    variantId: values.variant?.id ?? null,
    warehouseId: values.warehouseId || null,
    locationId: options.usesLocations && values.locationId ? values.locationId : null,
    type: values.type || null,
    quantity: q.ok ? q.value : null,
    reason: values.reason.trim() || null,
  };
}

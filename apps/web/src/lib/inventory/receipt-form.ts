import type { CreateReceiptRequest } from "./api";
import { addQuantities, parseQuantity, quantityText } from "./quantity";

export const REFERENCE_MAX = 200;
export const MAX_RECEIPT_LINES = 500;

/** What a line needs to know about its variant (display + id). */
export type VariantRef = { id: string; sku: string; name: string | null; barcode: string | null };

export type ReceiptLine = {
  /** Stable React key; not sent. */
  key: string;
  variant: VariantRef;
  /** As typed (Turkish decimal comma allowed). */
  quantity: string;
  /** "" = none (warehouse without locations). */
  locationId: string;
};

export type ReceiptFormValues = {
  /** "" = let the API use the default warehouse (only offered when the tenant has none yet). */
  warehouseId: string;
  reference: string;
  lines: ReceiptLine[];
};

export type ReceiptErrors = Record<string, string>;

export const emptyReceipt = (warehouseId = ""): ReceiptFormValues => ({ warehouseId, reference: "", lines: [] });

/**
 * A scan (or a picked search result) adds the variant: the same variant on the same location gets +1, anything
 * else becomes a new line with quantity 1. Returns the index of the line that changed, for focus/highlight.
 */
export function addVariantLine(
  lines: readonly ReceiptLine[],
  variant: VariantRef,
  locationId: string,
  makeKey: () => string,
): { lines: ReceiptLine[]; index: number } {
  const index = lines.findIndex((l) => l.variant.id === variant.id && l.locationId === locationId);
  if (index >= 0) {
    const current = parseQuantity(lines[index].quantity);
    const next = current.ok ? quantityText(addQuantities(current.value, 1)) : lines[index].quantity;
    return { lines: lines.map((l, i) => (i === index ? { ...l, quantity: next } : l)), index };
  }
  return { lines: [...lines, { key: makeKey(), variant, quantity: "1", locationId }], index: lines.length };
}

/** Client-side checks mirroring the API rules (the API validates again). Keys match the API's error keys. */
export function validateReceipt(values: ReceiptFormValues, options: { usesLocations: boolean }): ReceiptErrors {
  const errors: ReceiptErrors = {};
  if (values.reference.trim().length > REFERENCE_MAX) errors.reference = `En fazla ${REFERENCE_MAX} karakter girin.`;
  if (values.lines.length === 0) errors.lines = "En az bir ürün okutun veya arayıp ekleyin.";
  if (values.lines.length > MAX_RECEIPT_LINES) errors.lines = `Bir mal kabulde en fazla ${MAX_RECEIPT_LINES} satır olabilir.`;
  values.lines.forEach((line, i) => {
    const q = parseQuantity(line.quantity);
    if (!q.ok) errors[`lines[${i}].quantity`] = q.message;
    if (options.usesLocations && !line.locationId) errors[`lines[${i}].locationId`] = "Raf/göz seçin.";
  });
  return errors;
}

/** Request body; call only after validateReceipt returned no errors. Lines keep their on-screen order (error keys rely on it). */
export function buildReceiptRequest(values: ReceiptFormValues, options: { usesLocations: boolean }): CreateReceiptRequest {
  const reference = values.reference.trim();
  return {
    warehouseId: values.warehouseId || null,
    reference: reference || null,
    lines: values.lines.map((line) => {
      const q = parseQuantity(line.quantity);
      return {
        variantId: line.variant.id,
        locationId: options.usesLocations && line.locationId ? line.locationId : null,
        quantity: q.ok ? q.value : null,
        unitCost: null,
      };
    }),
  };
}

/** Total of the valid quantities (summary line). */
export function receiptTotal(lines: readonly ReceiptLine[]): number {
  return lines.reduce((sum, line) => {
    const q = parseQuantity(line.quantity);
    return q.ok ? addQuantities(sum, q.value) : sum;
  }, 0);
}

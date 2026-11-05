import type { VariantRef } from "@/lib/inventory/receipt-form";
import { isZeroDecimal, parseDecimalInput, toDecimalInput } from "@/lib/pricing/money";
import type { CreateOrderRequest, OrderDetail, OrderLineInput, UpdateOrderRequest } from "./api";

/**
 * Draft order form ⇄ API request. Text only: quantities, prices and discounts are normalised as decimal strings and
 * sent as they are; every amount (line totals, VAT, grand total) comes back from the API. Nothing here computes money.
 */

export type DiscountMode = "none" | "percent" | "amount";
/** "" = let the API decide (request → first priced line's list → excluded). */
export type TaxMode = "" | "excluded" | "included";

export type DraftLine = {
  /** Stable React key; not sent. */
  key: string;
  /** Server line id (kept on edit so the line keeps its history); null for a new line. */
  id: string | null;
  variant: VariantRef;
  /** As typed (Turkish decimal comma allowed). */
  quantity: string;
  /** Manual unit price (orders.price.override). "" = the price list price. */
  unitPrice: string;
  discountMode: DiscountMode;
  discount: string;
};

export type DraftCustomer = { id: string; name: string | null; code: string | null };

export type DraftValues = {
  customer: DraftCustomer | null;
  warehouseId: string;
  taxMode: TaxMode;
  requestedDeliveryDate: string;
  notes: string;
  lines: DraftLine[];
};

export type DraftOptions = {
  /** Unconditional orders.price.override: a manual unit price may be sent. */
  manualPrice: boolean;
  /** orders.price.override in any scope: line discounts may be sent (a limited grant is capped by the API). */
  discount: boolean;
};

let lineCounter = 0;
export const nextLineKey = () => `l-${++lineCounter}`;

export function newDraftValues(warehouseId = ""): DraftValues {
  return { customer: null, warehouseId, taxMode: "", requestedDeliveryDate: "", notes: "", lines: [] };
}

export function newDraftLine(variant: VariantRef): DraftLine {
  return { key: nextLineKey(), id: null, variant, quantity: "1", unitPrice: "", discountMode: "none", discount: "" };
}

/** A saved draft → form values. A manual price is shown only when the line was overridden (list prices are not "typed"). */
export function draftValuesFromDetail(detail: OrderDetail, customer?: { name: string | null; code: string | null }): DraftValues {
  const { order } = detail;
  return {
    customer: { id: order.customerId, name: customer?.name ?? null, code: customer?.code ?? null },
    warehouseId: order.warehouseId,
    taxMode: order.totals?.pricesIncludeTax ? "included" : "excluded",
    requestedDeliveryDate: order.requestedDeliveryDate ?? "",
    notes: order.notes ?? "",
    lines: detail.lines.map((l) => {
      const percent = l.discountPercent ?? null;
      const amount = l.discountAmount ?? null;
      return {
        key: nextLineKey(),
        id: l.id,
        variant: { id: l.variantId, sku: l.sku, name: l.name || null, barcode: null },
        quantity: toDecimalInput(l.quantity),
        unitPrice: l.priceOverridden ? toDecimalInput(l.unitPrice) : "",
        discountMode: amount !== null ? "amount" : percent !== null ? "percent" : "none",
        discount: amount !== null ? toDecimalInput(amount) : percent !== null ? toDecimalInput(percent) : "",
      };
    }),
  };
}

const QUANTITY_MESSAGE = "0'dan büyük, en fazla 3 ondalıklı bir miktar girin.";
const PRICE_MESSAGE = "0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin.";
const PERCENT_MESSAGE = "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin.";
const AMOUNT_MESSAGE = "0 veya daha büyük, en fazla 2 ondalıklı bir tutar girin.";

/** Error key of a line's discount input (matches the API's `lines[i].discountPercent` / `.discountAmount`). */
export function discountKey(index: number, mode: DiscountMode): string {
  return `lines[${index}].${mode === "amount" ? "discountAmount" : "discountPercent"}`;
}

/** ≤ 100 compared as text: "100", "100.0" pass; "100.01", "101" fail. No floating point. */
function percentInRange(value: string): boolean {
  const [integer, fraction = ""] = value.split(".");
  if (integer.length < 3) return true;
  return integer === "100" && /^0*$/.test(fraction);
}

/** Client-side checks with the API's error keys, so server and client errors land on the same inputs. */
export function validateDraft(values: DraftValues, options: DraftOptions): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!values.customer) errors.customerId = "Müşteri seçin.";
  if (values.notes.length > 2000) errors.notes = "Not en fazla 2000 karakter olabilir.";
  values.lines.forEach((line, i) => {
    const q = parseDecimalInput(line.quantity, 3);
    if (!q.ok || isZeroDecimal(q.value)) errors[`lines[${i}].quantity`] = QUANTITY_MESSAGE;
    if (options.manualPrice && line.unitPrice.trim() && !parseDecimalInput(line.unitPrice, 4).ok) errors[`lines[${i}].unitPrice`] = PRICE_MESSAGE;
    if (options.discount && line.discountMode !== "none") {
      const d = parseDecimalInput(line.discount, 2);
      if (!d.ok) errors[discountKey(i, line.discountMode)] = line.discountMode === "percent" ? PERCENT_MESSAGE : AMOUNT_MESSAGE;
      else if (line.discountMode === "percent" && !percentInRange(d.value)) errors[discountKey(i, "percent")] = PERCENT_MESSAGE;
    }
  });
  return errors;
}

const decimalOrNull = (text: string, maxDecimals: number): string | null => {
  if (!text.trim()) return null;
  const parsed = parseDecimalInput(text, maxDecimals);
  return parsed.ok ? parsed.value : text.trim();
};

/**
 * Lines as the API takes them. Without orders.price.override no manual price or discount is sent: the API then
 * applies the list price and the dealer group's default discount (sending one would be a 403). A limited grant (sales
 * discount limit) sends discounts but never a manual price.
 */
export function buildLineInputs(values: DraftValues, options: DraftOptions): OrderLineInput[] {
  return values.lines.map((line) => {
    const discount = options.discount && line.discountMode !== "none" ? decimalOrNull(line.discount, 2) : null;
    return {
      id: line.id,
      variantId: line.variant.id,
      quantity: decimalOrNull(line.quantity, 3),
      unitPrice: options.manualPrice ? decimalOrNull(line.unitPrice, 4) : null,
      discountPercent: line.discountMode === "percent" ? discount : null,
      discountAmount: line.discountMode === "amount" ? discount : null,
    };
  });
}

const taxModeValue = (mode: TaxMode): boolean | null => (mode === "" ? null : mode === "included");

export function buildCreateRequest(values: DraftValues, options: DraftOptions): CreateOrderRequest {
  return {
    customerId: values.customer?.id ?? null,
    warehouseId: values.warehouseId || null,
    source: "manual",
    sourceReferenceId: null,
    currency: null,
    pricesIncludeTax: taxModeValue(values.taxMode),
    requestedDeliveryDate: values.requestedDeliveryDate || null,
    notes: values.notes.trim() || null,
    lines: buildLineInputs(values, options),
  };
}

/**
 * Merge patch of what changed since `original`. `lines` is sent only when a line changed (it replaces every line and
 * re-prices them all); an empty patch means nothing to save.
 */
export function buildUpdatePatch(original: DraftValues, values: DraftValues, options: DraftOptions): Partial<UpdateOrderRequest> {
  const patch: Partial<UpdateOrderRequest> = {};
  if (values.customer && values.customer.id !== original.customer?.id) patch.customerId = values.customer.id;
  if (values.warehouseId && values.warehouseId !== original.warehouseId) patch.warehouseId = values.warehouseId;
  if (values.taxMode !== original.taxMode && values.taxMode !== "") patch.pricesIncludeTax = values.taxMode === "included";
  if (values.requestedDeliveryDate !== original.requestedDeliveryDate) patch.requestedDeliveryDate = values.requestedDeliveryDate || null;
  if (values.notes.trim() !== original.notes.trim()) patch.notes = values.notes.trim() || null;
  const before = JSON.stringify(buildLineInputs(original, options));
  const after = JSON.stringify(buildLineInputs(values, options));
  if (before !== after) patch.lines = buildLineInputs(values, options);
  return patch;
}

export function isDirty(original: DraftValues, values: DraftValues, options: DraftOptions): boolean {
  return Object.keys(buildUpdatePatch(original, values, options)).length > 0;
}

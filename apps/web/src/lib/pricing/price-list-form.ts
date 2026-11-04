import type { CreatePriceListRequest, PriceList, UpdatePriceListRequest } from "./api";
import { parseDecimalInput, sameDecimal, toDecimalInput } from "./money";

export const PRICE_LIST_NAME_MAX = 100;

export type PriceListFormValues = {
  name: string;
  kind: "sales" | "purchase";
  currency: string;
  /** Create: make it the default of its currency. Edit: "make default" (the API only accepts true). */
  isDefault: boolean;
  pricesIncludeTax: boolean;
  /** yyyy-mm-dd or "". */
  validFrom: string;
  validTo: string;
  basePriceListId: string;
  /** Typed text, e.g. "12,5". */
  discountPercent: string;
  status: "active" | "inactive";
};

export const newPriceListValues = (currency = "TRY"): PriceListFormValues => ({
  name: "",
  kind: "sales",
  currency,
  isDefault: false,
  pricesIncludeTax: false,
  validFrom: "",
  validTo: "",
  basePriceListId: "",
  discountPercent: "",
  status: "active",
});

export function priceListToValues(list: PriceList): PriceListFormValues {
  return {
    name: list.name,
    kind: list.kind === "purchase" ? "purchase" : "sales",
    currency: list.currency,
    isDefault: list.isDefault,
    pricesIncludeTax: list.pricesIncludeTax,
    validFrom: list.validFrom ?? "",
    validTo: list.validTo ?? "",
    basePriceListId: list.basePriceListId ?? "",
    discountPercent: toDecimalInput(list.discountPercent),
    status: list.status === "inactive" ? "inactive" : "active",
  };
}

/** Normalised percent text ("12,5" → "12.5") or null when empty/invalid. 0–100, at most 2 decimals. */
export function parsePercent(text: string): string | null {
  const parsed = parseDecimalInput(text, 2);
  if (!parsed.ok) return null;
  // Range check only (not money): a percentage is compared, never used in a calculation here.
  return Number(parsed.value) <= 100 ? parsed.value : null;
}

/**
 * Lists that can be the base of `values`: other active, non-derived sales lists in the same currency and VAT mode
 * (as-built rule, data-model §5.4: one level, no chains).
 */
export function baseListCandidates(lists: readonly PriceList[], values: PriceListFormValues, selfId: string | null): PriceList[] {
  return lists.filter(
    (l) =>
      l.id !== selfId &&
      l.kind === "sales" &&
      l.status !== "archived" &&
      !l.basePriceListId &&
      l.currency === values.currency &&
      l.pricesIncludeTax === values.pricesIncludeTax,
  );
}

/** Mirrors the API rules (name 1–100, ISO currency, date order, base + discount together, 0–100 %). */
export function validatePriceList(values: PriceListFormValues): Record<string, string> {
  const errors: Record<string, string> = {};
  const name = values.name.trim();
  if (!name) errors.name = "Liste adı girin.";
  else if (name.length > PRICE_LIST_NAME_MAX) errors.name = `En fazla ${PRICE_LIST_NAME_MAX} karakter girin.`;
  if (!/^[A-Z]{3}$/.test(values.currency)) errors.currency = "Para birimi seçin.";
  if (values.validFrom && values.validTo && values.validTo < values.validFrom) errors.validTo = "Bitiş tarihi başlangıç tarihinden önce olamaz.";
  if (values.kind === "sales" && values.basePriceListId) {
    if (!values.discountPercent.trim()) errors.discountPercent = "Taban listeden türetmek için iskonto oranını girin (0 olabilir).";
    else if (parsePercent(values.discountPercent) === null) errors.discountPercent = "0 ile 100 arasında, en fazla 2 ondalıklı bir oran girin.";
  } else if (values.discountPercent.trim()) {
    errors.basePriceListId = "İskonto yalnızca bir taban listeyle birlikte kullanılır. Taban liste seçin veya iskontoyu silin.";
  }
  return errors;
}

export function buildCreatePriceList(values: PriceListFormValues): CreatePriceListRequest {
  const sales = values.kind === "sales";
  const derived = sales && values.basePriceListId !== "";
  return {
    name: values.name.trim(),
    kind: values.kind,
    currency: values.currency,
    isDefault: sales && values.isDefault,
    pricesIncludeTax: values.pricesIncludeTax,
    validFrom: values.validFrom || null,
    validTo: values.validTo || null,
    basePriceListId: derived ? values.basePriceListId : null,
    discountPercent: derived ? parsePercent(values.discountPercent) : null,
  };
}

/**
 * Merge patch with the changed members only. `isDefault` is only ever sent as true; the base list and the discount
 * are always sent together (the API requires both or neither).
 */
export function buildPriceListPatch(original: PriceList, values: PriceListFormValues): Partial<UpdatePriceListRequest> {
  const patch: Partial<UpdatePriceListRequest> = {};
  if (values.name.trim() !== original.name) patch.name = values.name.trim();
  if (values.pricesIncludeTax !== original.pricesIncludeTax) patch.pricesIncludeTax = values.pricesIncludeTax;
  if ((values.validFrom || null) !== (original.validFrom ?? null)) patch.validFrom = values.validFrom || null;
  if ((values.validTo || null) !== (original.validTo ?? null)) patch.validTo = values.validTo || null;
  const base = values.kind === "sales" ? values.basePriceListId || null : null;
  const discount = base ? parsePercent(values.discountPercent) : null;
  if (base !== (original.basePriceListId ?? null) || !sameDecimal(discount, original.discountPercent)) {
    patch.basePriceListId = base;
    patch.discountPercent = discount;
  }
  if (values.status !== original.status) patch.status = values.status;
  if (values.isDefault && !original.isDefault) patch.isDefault = true;
  return patch;
}

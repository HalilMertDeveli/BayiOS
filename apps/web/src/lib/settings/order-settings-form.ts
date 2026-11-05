import { NEGATIVE_STOCK_POLICIES, type NegativeStockPolicy, type TenantSettings, type UpdateTenantSettingsRequest } from "@/lib/api/identity";
import { translateFieldMessage } from "@/lib/catalog/errors";
import { parseDecimalInput, toDecimalInput } from "@/lib/pricing/money";

/**
 * Ayarlar → Sipariş ayarları (#92): the tenant rules the order desk follows. Form text ⇄ PATCH /tenant/settings (JSON
 * merge patch: only changed members are sent).
 */
export type OrderSettingsValues = {
  requireOrderApproval: boolean;
  /** As typed; normalised to upper case on save, as the API does. */
  orderNumberPrefix: string;
  negativeStockPolicy: NegativeStockPolicy;
  /** Default VAT rate in percent, as text ("20", "10", "1", "0" or any 0–100 value with ≤ 2 decimals). */
  defaultTaxRate: string;
  pricesIncludeTaxDisplay: boolean;
};

/** Same rule as the API (TenantSettings.IsValidOrderNumberPrefix): 1–10 of A–Z and 0–9, starting with a letter. */
export const ORDER_NUMBER_PREFIX_PATTERN = /^[A-Z][A-Z0-9]{0,9}$/;
export const ORDER_NUMBER_PREFIX_MAX = 10;

export const normalisePrefix = (value: string): string => value.trim().toLocaleUpperCase("en-US");

const toPolicy = (value: string): NegativeStockPolicy =>
  (NEGATIVE_STOCK_POLICIES as readonly string[]).includes(value) ? (value as NegativeStockPolicy) : "block";

export function settingsToValues(s: TenantSettings): OrderSettingsValues {
  return {
    requireOrderApproval: s.requireOrderApproval,
    orderNumberPrefix: s.orderNumberPrefix,
    negativeStockPolicy: toPolicy(s.negativeStockPolicy),
    defaultTaxRate: toDecimalInput(s.defaultTaxRate),
    pricesIncludeTaxDisplay: s.pricesIncludeTaxDisplay,
  };
}

export function validatePrefix(value: string): string | undefined {
  const prefix = normalisePrefix(value);
  if (!prefix) return "Sipariş numarası önekini girin.";
  if (prefix.length > ORDER_NUMBER_PREFIX_MAX) return `Önek en fazla ${ORDER_NUMBER_PREFIX_MAX} karakter olabilir.`;
  if (!/^[A-Z]/.test(prefix)) return "Önek bir harfle (A–Z) başlamalı.";
  if (!ORDER_NUMBER_PREFIX_PATTERN.test(prefix)) return "Yalnızca İngilizce büyük harf (A–Z) ve rakam (0–9) kullanın; boşluk, tire ve Türkçe karakter olmaz.";
  return undefined;
}

/** 0–100 with at most 2 decimals, compared as text (no floating point). */
function taxRateError(value: string): string | undefined {
  const parsed = parseDecimalInput(value, 2);
  const message = "0 ile 100 arasında, en fazla 2 ondalıklı bir KDV oranı girin.";
  if (!parsed.ok) return message;
  const [integer, fraction = ""] = parsed.value.split(".");
  if (integer.length > 3 || (integer.length === 3 && !(integer === "100" && /^0*$/.test(fraction)))) return message;
  return undefined;
}

export function validateOrderSettings(values: OrderSettingsValues): Record<string, string> {
  const errors: Record<string, string> = {};
  const prefix = validatePrefix(values.orderNumberPrefix);
  if (prefix) errors.orderNumberPrefix = prefix;
  const tax = taxRateError(values.defaultTaxRate);
  if (tax) errors.defaultTaxRate = tax;
  return errors;
}

/** Changed members only; an empty patch means nothing to save. Numbers compare numerically ("20.00" = "20"). */
export function buildSettingsPatch(original: TenantSettings, values: OrderSettingsValues): Partial<UpdateTenantSettingsRequest> {
  const patch: Partial<UpdateTenantSettingsRequest> = {};
  if (values.requireOrderApproval !== original.requireOrderApproval) patch.requireOrderApproval = values.requireOrderApproval;
  const prefix = normalisePrefix(values.orderNumberPrefix);
  if (prefix !== original.orderNumberPrefix) patch.orderNumberPrefix = prefix;
  if (values.negativeStockPolicy !== original.negativeStockPolicy) patch.negativeStockPolicy = values.negativeStockPolicy;
  const tax = parseDecimalInput(values.defaultTaxRate, 2);
  if (tax.ok && Number(tax.value) !== Number(original.defaultTaxRate)) patch.defaultTaxRate = Number(tax.value);
  if (values.pricesIncludeTaxDisplay !== original.pricesIncludeTaxDisplay) patch.pricesIncludeTaxDisplay = values.pricesIncludeTaxDisplay;
  return patch;
}

const KNOWN: [RegExp, string][] = [
  [/^Must be 1.10 characters of A.Z and 0.9, starting with a letter\.$/, "Önek 1–10 karakter olmalı; A–Z ve 0–9 kullanın, harfle başlayın."],
  [/^Must be a percentage between 0 and 100 with at most 2 decimals\.$/, "0 ile 100 arasında, en fazla 2 ondalıklı bir KDV oranı girin."],
  [/^Must be one of: /, "Listeden bir seçenek seçin."],
];

/** Settings validation text from the API (English developer text) → Turkish. */
export function translateSettingsFieldMessage(message: string | undefined): string {
  if (message) for (const [pattern, text] of KNOWN) if (pattern.test(message)) return text;
  return translateFieldMessage(message);
}

/** "SIP-2026-000123": what the next order numbers look like with this prefix (numbering per prefix and year). */
export function orderNumberExample(prefix: string, year: number): string {
  return `${normalisePrefix(prefix) || "SIP"}-${year}-000123`;
}

import { NEGATIVE_STOCK_POLICIES, type NegativeStockPolicy, type TenantSettings, type UpdateTenantSettingsRequest } from "@/lib/api/identity";
import { parseDecimalInput, toDecimalInput } from "@/lib/pricing/money";
import { translateSettingsFieldMessage } from "./order-settings-form";

/**
 * Ayarlar → İş kuralları (#129 item 6, roles-permissions §4): the adjustable tenant rules behind the conditional grants
 * of GET /me. Form ⇄ PATCH /tenant/settings (JSON merge patch: only changed members are sent, If-Match).
 */
export type RulesValues = {
  salesCanApproveOrders: boolean;
  /** Percent as typed ("0", "5", "7,5"). 0 = sales may not discount. */
  salesMaxDiscountPercent: string;
  showCostToManager: boolean;
  showCostToViewer: boolean;
  staffCanAssignBarcode: boolean;
  staffCanApproveReceipt: boolean;
  negativeStockPolicy: NegativeStockPolicy;
  /** Stored and audited by the API but NOT enforced yet (roles-permissions §4). */
  require2fa: boolean;
};

export const RULE_FIELDS = [
  "salesCanApproveOrders",
  "salesMaxDiscountPercent",
  "showCostToManager",
  "showCostToViewer",
  "staffCanAssignBarcode",
  "staffCanApproveReceipt",
  "negativeStockPolicy",
  "require2fa",
] as const satisfies readonly (keyof RulesValues)[];

const toPolicy = (value: string): NegativeStockPolicy =>
  (NEGATIVE_STOCK_POLICIES as readonly string[]).includes(value) ? (value as NegativeStockPolicy) : "block";

export function settingsToRules(s: TenantSettings): RulesValues {
  return {
    salesCanApproveOrders: s.salesCanApproveOrders,
    salesMaxDiscountPercent: toDecimalInput(s.salesMaxDiscountPercent),
    showCostToManager: s.showCostToManager,
    showCostToViewer: s.showCostToViewer,
    staffCanAssignBarcode: s.staffCanAssignBarcode,
    staffCanApproveReceipt: s.staffCanApproveReceipt,
    negativeStockPolicy: toPolicy(s.negativeStockPolicy),
    require2fa: s.require2fa,
  };
}

const DISCOUNT_MESSAGE = "0 ile 100 arasında, en fazla 2 ondalıklı bir yüzde girin (ör. 5 veya 7,5).";

/** "7,5" → "7.5"; null when not a 0–100 percentage with ≤ 2 decimals. Text only, no floating point. */
export function parseDiscountPercent(value: string): string | null {
  const parsed = parseDecimalInput(value, 2);
  if (!parsed.ok) return null;
  const [integer, fraction = ""] = parsed.value.split(".");
  if (integer.length > 3) return null;
  if (integer.length === 3 && !(integer === "100" && /^0*$/.test(fraction))) return null;
  return parsed.value;
}

export function validateRules(values: RulesValues): Record<string, string> {
  const errors: Record<string, string> = {};
  if (parseDiscountPercent(values.salesMaxDiscountPercent) === null) errors.salesMaxDiscountPercent = DISCOUNT_MESSAGE;
  return errors;
}

/** Changed members only; empty = nothing to save. The discount compares numerically ("5.00" = "5"). */
export function buildRulesPatch(original: TenantSettings, values: RulesValues): Partial<UpdateTenantSettingsRequest> {
  const patch: Partial<UpdateTenantSettingsRequest> = {};
  const flags = ["salesCanApproveOrders", "showCostToManager", "showCostToViewer", "staffCanAssignBarcode", "staffCanApproveReceipt", "require2fa"] as const;
  for (const key of flags) if (values[key] !== original[key]) patch[key] = values[key];
  const discount = parseDiscountPercent(values.salesMaxDiscountPercent);
  if (discount !== null && Number(discount) !== Number(original.salesMaxDiscountPercent)) patch.salesMaxDiscountPercent = Number(discount);
  if (values.negativeStockPolicy !== original.negativeStockPolicy) patch.negativeStockPolicy = values.negativeStockPolicy;
  return patch;
}

/** API validation text for the rule fields → Turkish. */
export function translateRuleFieldMessage(message: string | undefined): string {
  if (message && /^Must be between 0 and 100\.$/.test(message)) return DISCOUNT_MESSAGE;
  return translateSettingsFieldMessage(message);
}

/** What the discount rule means for sales, in one sentence. */
export function discountRuleSummary(value: string): string {
  const parsed = parseDiscountPercent(value);
  if (parsed === null) return DISCOUNT_MESSAGE;
  if (Number(parsed) === 0) return "Satış personeli iskonto uygulayamaz ve birim fiyatı değiştiremez.";
  return `Satış personeli satır başına en fazla %${parsed.replace(".", ",")} iskonto uygulayabilir; birim fiyatı elle değiştiremez.`;
}

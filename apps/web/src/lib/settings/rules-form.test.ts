import { describe, expect, it } from "vitest";
import type { TenantSettings } from "@/lib/api/identity";
import { buildRulesPatch, discountRuleSummary, parseDiscountPercent, settingsToRules, translateRuleFieldMessage, validateRules } from "./rules-form";

const settings: TenantSettings = {
  negativeStockPolicy: "block",
  salesCanApproveOrders: true,
  salesMaxDiscountPercent: 0,
  showCostToManager: false,
  showCostToViewer: false,
  staffCanAssignBarcode: false,
  staffCanApproveReceipt: false,
  require2fa: false,
  defaultTaxRate: 20,
  pricesIncludeTaxDisplay: false,
  requireOrderApproval: true,
  orderNumberPrefix: "SIP",
};

describe("rules form", () => {
  it("round-trips the settings without changes", () => {
    expect(buildRulesPatch(settings, settingsToRules(settings))).toEqual({});
  });

  it("sends only the changed rules (merge patch), never the order settings", () => {
    const values = { ...settingsToRules(settings), salesCanApproveOrders: false, salesMaxDiscountPercent: "7,5", showCostToManager: true, require2fa: true };
    expect(buildRulesPatch(settings, values)).toEqual({ salesCanApproveOrders: false, salesMaxDiscountPercent: 7.5, showCostToManager: true, require2fa: true });
  });

  it("compares the discount numerically", () => {
    const original = { ...settings, salesMaxDiscountPercent: "5.00" };
    expect(buildRulesPatch(original, { ...settingsToRules(original), salesMaxDiscountPercent: "5" })).toEqual({});
  });

  it.each([
    ["0", "0"],
    ["10", "10"],
    ["7,5", "7.5"],
    ["100", "100"],
    ["100,00", "100.00"],
  ])("accepts %s", (input, parsed) => {
    expect(parseDiscountPercent(input)).toBe(parsed);
  });

  it.each(["", "-1", "100,5", "101", "1,234", "abc", "1000"])("rejects %s", (input) => {
    expect(parseDiscountPercent(input)).toBeNull();
    expect(validateRules({ ...settingsToRules(settings), salesMaxDiscountPercent: input }).salesMaxDiscountPercent).toBeDefined();
  });

  it("explains the discount rule", () => {
    expect(discountRuleSummary("0")).toContain("iskonto uygulayamaz");
    expect(discountRuleSummary("7.5")).toContain("en fazla %7,5");
  });

  it("translates the API's validation text", () => {
    expect(translateRuleFieldMessage("Must be between 0 and 100.")).toContain("0 ile 100 arasında");
    expect(translateRuleFieldMessage("Must be one of: block, warn, allow.")).toBe("Listeden bir seçenek seçin.");
  });
});

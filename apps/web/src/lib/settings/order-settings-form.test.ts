import { describe, expect, it } from "vitest";
import type { TenantSettings } from "@/lib/api/identity";
import {
  buildSettingsPatch,
  orderNumberExample,
  settingsToValues,
  translateSettingsFieldMessage,
  validateOrderSettings,
  validatePrefix,
} from "./order-settings-form";

/** Synthetic tenant settings as GET /tenant/settings returns them (numbers may come as strings). */
const SETTINGS: TenantSettings = {
  negativeStockPolicy: "block",
  salesCanApproveOrders: false,
  salesMaxDiscountPercent: 0,
  showCostToManager: true,
  showCostToViewer: false,
  staffCanAssignBarcode: false,
  staffCanApproveReceipt: false,
  require2fa: false,
  defaultTaxRate: "20.00",
  pricesIncludeTaxDisplay: false,
  requireOrderApproval: true,
  orderNumberPrefix: "SIP",
  autoCreatePickingTask: true,
};

describe("order number prefix (same rule as the API)", () => {
  it.each([
    ["SIP", undefined],
    ["a1", undefined],
    ["ABCDEFGHIJ", undefined],
    ["", "Sipariş numarası önekini girin."],
    ["ABCDEFGHIJK", "Önek en fazla 10 karakter olabilir."],
    ["1SIP", "Önek bir harfle (A–Z) başlamalı."],
    ["SİP", "Yalnızca İngilizce büyük harf (A–Z) ve rakam (0–9) kullanın; boşluk, tire ve Türkçe karakter olmaz."],
    ["SI-P", "Yalnızca İngilizce büyük harf (A–Z) ve rakam (0–9) kullanın; boşluk, tire ve Türkçe karakter olmaz."],
  ])("%s → %s", (value, expected) => {
    expect(validatePrefix(value)).toBe(expected);
  });

  it("shows what new numbers look like", () => {
    expect(orderNumberExample(" web ", 2026)).toBe("WEB-2026-000123");
    expect(orderNumberExample("", 2026)).toBe("SIP-2026-000123");
  });
});

describe("settings form ⇄ merge patch", () => {
  it("an untouched form has nothing to save (numbers compare numerically)", () => {
    expect(buildSettingsPatch(SETTINGS, settingsToValues(SETTINGS))).toEqual({});
  });

  it("sends only what changed; the prefix upper-cased and trimmed", () => {
    const values = { ...settingsToValues(SETTINGS), requireOrderApproval: false, orderNumberPrefix: " web ", defaultTaxRate: "10" };
    expect(buildSettingsPatch(SETTINGS, values)).toEqual({ requireOrderApproval: false, orderNumberPrefix: "WEB", defaultTaxRate: 10 });
  });

  it("sends autoCreatePickingTask only when the toggle changed (#98)", () => {
    const values = { ...settingsToValues(SETTINGS), autoCreatePickingTask: !SETTINGS.autoCreatePickingTask };
    expect(buildSettingsPatch(SETTINGS, values)).toEqual({ autoCreatePickingTask: !SETTINGS.autoCreatePickingTask });
    expect(settingsToValues({ ...SETTINGS, autoCreatePickingTask: false }).autoCreatePickingTask).toBe(false);
  });

  it("never sends negativeStockPolicy: stock never going below zero is a fixed rule (OD-09)", () => {
    expect(settingsToValues(SETTINGS)).not.toHaveProperty("negativeStockPolicy");
    expect(buildSettingsPatch({ ...SETTINGS, negativeStockPolicy: "warn" }, settingsToValues(SETTINGS))).not.toHaveProperty("negativeStockPolicy");
  });

  it("validates the prefix and the VAT rate", () => {
    expect(validateOrderSettings({ ...settingsToValues(SETTINGS), orderNumberPrefix: "", defaultTaxRate: "100,01" })).toEqual({
      orderNumberPrefix: "Sipariş numarası önekini girin.",
      defaultTaxRate: "0 ile 100 arasında, en fazla 2 ondalıklı bir KDV oranı girin.",
    });
    expect(validateOrderSettings({ ...settingsToValues(SETTINGS), defaultTaxRate: "8,5" })).toEqual({});
  });

  it("translates the API's validation text", () => {
    expect(translateSettingsFieldMessage("Must be 1–10 characters of A–Z and 0–9, starting with a letter.")).toMatch(/Önek 1–10 karakter/);
  });
});

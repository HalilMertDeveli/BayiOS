import { describe, expect, it } from "vitest";
import { accessFor } from "@/test/session";
import {
  IMPORT_TEMPLATE_HEADERS,
  canRunSetup,
  checklistDismissKey,
  checklistItems,
  checklistProgress,
  importTemplateCsv,
  missingShelves,
  nextStep,
  parseStep,
  previousStep,
  profilePatch,
  readableFacts,
  setupStepHref,
  shelfCodes,
  suggestSku,
  validateProfile,
  validateQuickProduct,
  validateShelfLayout,
} from "./setup";

describe("setup steps", () => {
  it("parses the step from the URL, falling back to the first step", () => {
    expect(parseStep("depo")).toBe("depo");
    expect(parseStep(null)).toBe("isletme");
    expect(parseStep("bilinmeyen")).toBe("isletme");
  });

  it("walks forward and back without leaving the range", () => {
    expect(nextStep("isletme")).toBe("depo");
    expect(nextStep("bitti")).toBe("bitti");
    expect(previousStep("depo")).toBe("isletme");
    expect(previousStep("isletme")).toBe("isletme");
    expect(setupStepHref("isletme")).toBe("/kurulum");
    expect(setupStepHref("stok")).toBe("/kurulum?adim=stok");
  });
});

describe("business profile", () => {
  it("mirrors the API rules", () => {
    expect(validateProfile({ phone: "", taxOffice: "", taxNumber: "" })).toEqual({});
    expect(validateProfile({ phone: "+90 (212) 555-0000", taxOffice: "Kadıköy", taxNumber: "1234567890" })).toEqual({});
    expect(validateProfile({ phone: "", taxOffice: "", taxNumber: "12345678901" })).toEqual({});
    expect(validateProfile({ phone: "abc", taxOffice: "x".repeat(101), taxNumber: "123" })).toEqual({
      phone: expect.stringContaining("7–20"),
      taxOffice: expect.stringContaining("100"),
      taxNumber: expect.stringContaining("10 haneli"),
    });
  });

  it("patches only changed members and clears with null", () => {
    const current = { phone: "0212 555 00 00", taxOffice: null, taxNumber: "1234567890" };
    expect(profilePatch(current, { phone: "0212 555 00 00", taxOffice: "", taxNumber: "1234567890" })).toEqual({});
    expect(profilePatch(current, { phone: " ", taxOffice: "Merkez", taxNumber: "1234567890" })).toEqual({ phone: null, taxOffice: "Merkez" });
  });
});

describe("shelf layout", () => {
  it("builds A-01 … A-10 with pick order", () => {
    const codes = shelfCodes("a", 10);
    expect(codes).toHaveLength(10);
    expect(codes[0]).toEqual({ code: "A-01", pickSequence: 10 });
    expect(codes[9]).toEqual({ code: "A-10", pickSequence: 100 });
  });

  it("validates prefix and count", () => {
    expect(validateShelfLayout("A", "10")).toEqual({});
    expect(validateShelfLayout("", "10").prefix).toBeDefined();
    expect(validateShelfLayout("A B", "10").prefix).toBeDefined();
    expect(validateShelfLayout("A", "0").count).toBeDefined();
    expect(validateShelfLayout("A", "51").count).toBeDefined();
    expect(validateShelfLayout("A", "2,5").count).toBeDefined();
  });

  it("skips shelves that already exist (resumed or retried setup)", () => {
    const missing = missingShelves(shelfCodes("A", 3), ["a-01", "A-03"]);
    expect(missing.map((s) => s.code)).toEqual(["A-02"]);
  });
});

describe("product import template", () => {
  it("is a ;-separated UTF-8 CSV with BOM whose headers the API recognises", () => {
    const csv = importTemplateCsv();
    expect(csv.startsWith("﻿")).toBe(true);
    const [header, ...rows] = csv.slice(1).trim().split("\r\n");
    expect(header.split(";")).toEqual([...IMPORT_TEMPLATE_HEADERS]);
    expect(rows).toHaveLength(2);
    for (const row of rows) expect(row.split(";")).toHaveLength(IMPORT_TEMPLATE_HEADERS.length);
  });

  it("contains no sector-specific sample data", () => {
    expect(importTemplateCsv()).not.toMatch(/motor|\bLED\b|yedek|parça|watt/i);
  });
});

describe("quick product", () => {
  it("requires name, SKU and unit; barcode optional", () => {
    expect(validateQuickProduct({ name: "Kalem", sku: "KALEM-1", barcode: "", unitId: "u1" })).toEqual({});
    const errors = validateQuickProduct({ name: " ", sku: "A B", barcode: "1 2", unitId: "" });
    expect(Object.keys(errors).sort()).toEqual(["barcode", "name", "sku", "unitId"]);
  });

  it("suggests an ASCII SKU from a Turkish name", () => {
    expect(suggestSku("Çiğ Süt Şişe 1 lt")).toBe("CIG-SUT-SISE-1-LT");
    expect(suggestSku("ıİ öÖ")).toBe("II-OO");
    expect(suggestSku("  ")).toBe("");
    expect(suggestSku("x".repeat(80))).toHaveLength(32);
  });
});

describe("checklist", () => {
  it("leaves out unknown facts and counts the done ones", () => {
    const facts = { warehouse: true, products: false, stock: null, team: true, order: false };
    expect(checklistItems(facts).map((r) => [r.item.key, r.done])).toEqual([
      ["warehouse", true],
      ["products", false],
      ["team", true],
      ["order", false],
    ]);
    expect(checklistProgress(facts)).toEqual({ done: 2, total: 4 });
  });

  it("is for owner/admin; reads follow each role's permissions", () => {
    expect(canRunSetup(accessFor("owner"))).toBe(true);
    expect(canRunSetup(accessFor("admin"))).toBe(true);
    expect(canRunSetup(accessFor("manager"))).toBe(false);
    expect(canRunSetup(accessFor("sales"))).toBe(false);
    expect(readableFacts(accessFor("owner"))).toEqual({ warehouse: true, products: true, stock: true, team: true, order: true });
  });

  it("keeps the dismissal per user and tenant", () => {
    expect(checklistDismissKey("u1", "t1")).not.toBe(checklistDismissKey("u1", "t2"));
  });
});

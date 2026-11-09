import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import {
  catalogErrorMessage,
  codeConflictMessage,
  conflictRowIndex,
  fieldId,
  isVersionConflict,
  partitionErrors,
  readCodeConflict,
  serverFieldErrors,
  translateFieldMessage,
} from "./errors";

describe("409 sku/barcode conflict detail", () => {
  const taken = (code: string, conflict?: object) => new ApiError("x", 409, { code, ...(conflict ? { conflict } : {}) });
  const rows = [
    { sku: "FB-1", barcode: "" },
    { sku: "fb-2", barcode: "8690000000011" },
  ];

  it("reads the field and the holder from the problem", () => {
    const parsed = readCodeConflict(taken("catalog.barcode_taken", { field: "barcode", value: "8690000000011", variantId: "v9", productId: "p9", sku: "LED-9" }));
    expect(parsed).toEqual({ field: "barcode", conflict: { field: "barcode", value: "8690000000011", variantId: "v9", productId: "p9", sku: "LED-9" } });
    expect(readCodeConflict(taken("catalog.sku_taken"))).toEqual({ field: "sku", conflict: null });
    expect(readCodeConflict(new ApiError("x", 409, { code: "catalog.last_variant" }))).toBeNull();
  });

  it("finds the exact variant row by the taken value (SKU case-insensitive)", () => {
    expect(conflictRowIndex(rows, { field: "sku", conflict: { field: "sku", value: "FB-2" } })).toBe(1);
    expect(conflictRowIndex(rows, { field: "barcode", conflict: { field: "barcode", value: "8690000000011" } })).toBe(1);
    // No detail (only the DB index caught it): pinned only on a single-row form.
    expect(conflictRowIndex(rows, { field: "sku", conflict: null })).toBe(-1);
    expect(conflictRowIndex([rows[0]], { field: "sku", conflict: null })).toBe(0);
  });

  it("names the holder: this product vs another product", () => {
    expect(codeConflictMessage({ field: "sku", conflict: { productId: "p1" } }, "p1")).toContain("bu ürünün başka bir varyantında");
    expect(codeConflictMessage({ field: "sku", conflict: { productId: "p2" } }, "p1")).toBe("Bu SKU başka bir üründe kullanılıyor. Farklı bir SKU girin.");
    expect(codeConflictMessage({ field: "barcode", conflict: { productId: "p2", sku: "LED-9" } }, null)).toContain("(SKU LED-9)");
    expect(codeConflictMessage({ field: "barcode", conflict: null })).toBe("Bu barkod başka bir üründe kullanılıyor. Farklı bir barkod girin.");
  });
});

describe("translateFieldMessage", () => {
  it("translates the API's known messages", () => {
    expect(translateFieldMessage("Required.")).toBe("Bu alan zorunlu.");
    expect(translateFieldMessage("Must be 1–200 characters.")).toBe("1–200 karakter girin.");
    expect(translateFieldMessage("Must be one of: Sarı, Beyaz.")).toBe("Şunlardan biri olmalı: Sarı, Beyaz.");
    expect(translateFieldMessage('Must be an object {"min": number, "max": number} with min ≤ max.')).toMatch(/En az/);
  });
  it("never shows unknown English text", () => {
    expect(translateFieldMessage("Something odd happened.")).toBe("Bu alanı kontrol edin.");
    expect(translateFieldMessage(undefined)).toBe("Bu alanı kontrol edin.");
  });
});

describe("422 mapping", () => {
  const error = new ApiError("x", 422, {
    code: "validation.failed",
    errors: {
      "attributes.guc": ["Must be a number."],
      "variants[0].attributes.renk": ["Must be one of: Sarı."],
      "variants[0].unitId": ["Unit not found."],
    },
  });

  it("keeps the API keys and translates messages", () => {
    expect(serverFieldErrors(error)).toEqual({
      "attributes.guc": "Geçerli bir sayı girin.",
      "variants[0].attributes.renk": "Şunlardan biri olmalı: Sarı.",
      "variants[0].unitId": "Birim bulunamadı veya arşivlenmiş.",
    });
  });

  it("puts errors of rendered inputs on the inputs and the rest in the summary", () => {
    const { onFields, unmatched } = partitionErrors(serverFieldErrors(error), new Set(["attributes.guc", "variants[0].attributes.renk"]));
    expect(Object.keys(onFields)).toEqual(["attributes.guc", "variants[0].attributes.renk"]);
    expect(unmatched).toEqual(["Birim bulunamadı veya arşivlenmiş."]);
  });
});

describe("conflicts", () => {
  it("detects 412 version mismatch", () => {
    expect(isVersionConflict(new ApiError("x", 412, { code: "concurrency.version_mismatch" }))).toBe(true);
    expect(isVersionConflict(new ApiError("x", 409, { code: "catalog.sku_taken" }))).toBe(false);
  });
  it("maps catalog conflict codes to Turkish", () => {
    expect(catalogErrorMessage(new ApiError("x", 409, { code: "catalog.sku_taken" }))).toMatch(/SKU/);
    expect(catalogErrorMessage(new ApiError("x", 412, { code: "concurrency.version_mismatch" }))).toMatch(/değişti/);
  });
});

describe("fieldId", () => {
  it("makes DOM-safe, stable ids", () => {
    expect(fieldId("variants[0].attributes.guc")).toBe("f-variants-0-attributes-guc");
    expect(fieldId("name")).toBe("f-name");
  });
});

import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { catalogErrorMessage, fieldId, isVersionConflict, partitionErrors, serverFieldErrors, translateFieldMessage } from "./errors";

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

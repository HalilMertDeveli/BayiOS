import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import {
  inventoryErrorMessage,
  inventoryFieldErrors,
  isBusy,
  needsNegativeStockConfirmation,
  readShortages,
  translateInventoryFieldMessage,
} from "./errors";

const apiError = (status: number, problem: Record<string, unknown>) => new ApiError("x", status, problem);

describe("negative stock (warn policy)", () => {
  const warn = apiError(409, {
    code: "inventory.insufficient_stock",
    confirmationRequired: true,
    shortages: [{ variantId: "v-1", sku: "MOCK-1", warehouseId: "wh-1", locationId: null, requested: 5, available: 2 }, { bad: true }],
  });

  it("asks for confirmation only when the API says so", () => {
    expect(needsNegativeStockConfirmation(warn)).toBe(true);
    expect(needsNegativeStockConfirmation(apiError(409, { code: "inventory.insufficient_stock" }))).toBe(false);
    expect(needsNegativeStockConfirmation(apiError(409, { code: "inventory.insufficient_available", confirmationRequired: true }))).toBe(false);
    expect(needsNegativeStockConfirmation(new Error("x"))).toBe(false);
  });

  it("reads well-formed shortages and skips the rest", () => {
    expect(readShortages(warn)).toEqual([{ variantId: "v-1", sku: "MOCK-1", warehouseId: "wh-1", locationId: null, requested: 5, available: 2 }]);
    expect(readShortages(apiError(409, { shortages: "nope" }))).toEqual([]);
  });
});

describe("messages", () => {
  it("maps inventory and warehouse codes to Turkish", () => {
    expect(inventoryErrorMessage(apiError(409, { code: "warehouse.default_inactive" }))).toContain("Varsayılan depo pasif yapılamaz");
    expect(inventoryErrorMessage(apiError(409, { code: "warehouse.has_stock" }))).toContain("stok varken");
    expect(inventoryErrorMessage(apiError(409, { code: "inventory.busy" }))).toContain("Birkaç saniye sonra");
    expect(inventoryErrorMessage(apiError(404, { code: "resource.not_found" }))).toBe("Aradığınız kayıt bulunamadı.");
    expect(isBusy(apiError(409, { code: "inventory.busy" }))).toBe(true);
  });

  it("translates field messages, falling back to the catalog table", () => {
    expect(translateInventoryFieldMessage("A location is required in this warehouse.")).toBe("Bu depoda raf/göz (lokasyon) seçmek zorunlu.");
    expect(translateInventoryFieldMessage("Must be at most 200 characters.")).toBe("En fazla 200 karakter girin.");
    expect(translateInventoryFieldMessage("Something new.")).toBe("Bu alanı kontrol edin.");
    const e = apiError(422, { code: "validation.failed", errors: { "lines[0].quantity": ["Must be greater than 0 with at most 3 decimals."] } });
    expect(inventoryFieldErrors(e)).toEqual({ "lines[0].quantity": "0'dan büyük, en fazla 3 ondalıklı bir miktar girin." });
  });
});

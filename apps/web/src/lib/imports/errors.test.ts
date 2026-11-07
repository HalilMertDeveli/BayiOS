import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { failureMessage, formatWait, importErrorMessage, mappingFieldErrors, rateLimitMessage } from "./errors";
import { preCheckFile } from "./file-check";
import { canRollbackJob, importPermissions, runnableTypes } from "./permissions";
import { accessFor } from "@/test/session";

const apiError = (status: number, code?: string, extra: { errors?: Record<string, string[]>; retryAfter?: number | null } = {}) =>
  new ApiError(`failed ${status}`, status, code ? { code, errors: extra.errors } : null, undefined, extra.retryAfter ?? null);

describe("importErrorMessage", () => {
  it("429 tells how long to wait (Retry-After)", () => {
    expect(importErrorMessage(apiError(429, "rate_limit.exceeded", { retryAfter: 125 }))).toBe(
      "Bu işletme için saatlik dosya yükleme sınırına ulaşıldı. Yaklaşık 3 dakika sonra tekrar deneyin.",
    );
    expect(rateLimitMessage(null)).toMatch(/Biraz bekleyip/);
  });

  it.each([
    ["imports.file_too_large", 413, /10 MB/],
    ["imports.file_macro_not_allowed", 415, /\.xlsm/],
    ["imports.file_type_not_allowed", 415, /\.xlsx veya \.csv/],
    ["imports.file_too_many_rows", 422, /10\.000/],
    ["imports.file_purged", 409, /30 gün/],
    ["imports.already_rolled_back", 409, /yalnızca bir kez/],
    ["imports.rollback_window_expired", 409, /7 gün/],
    ["imports.invalid_state", 409, /durumu siz bakarken değişti/],
  ])("%s → Turkish", (code, status, pattern) => {
    expect(importErrorMessage(apiError(status, code))).toMatch(pattern);
  });

  it("413/415 without a code still name the file problem; other codes fall back to the shared messages", () => {
    expect(importErrorMessage(apiError(413))).toMatch(/10 MB/);
    expect(importErrorMessage(apiError(415))).toMatch(/dosya türü/);
    expect(importErrorMessage(apiError(403, "auth.forbidden"))).toMatch(/yetkiniz yok/);
    expect(importErrorMessage(new Error("x"))).toMatch(/Beklenmeyen/);
  });

  it("formats waits", () => {
    expect(formatWait(30)).toBe("30 saniye");
    expect(formatWait(3600)).toBe("1 saat");
    expect(formatWait(3900)).toBe("1 saat 5 dakika");
  });

  it("job failure codes", () => {
    expect(failureMessage({ code: "imports.stalled", message: "x" })).toMatch(/devam et/);
    expect(failureMessage({ code: "something.new", message: "x" })).toMatch(/tamamlanamadı/);
    expect(failureMessage(null)).toBeNull();
  });

  it("mapping 422 field messages are translated per key", () => {
    const error = apiError(422, "validation.failed", {
      errors: { "columns.name": ["Required for this import type and mode."], priceListId: ["Required when the price column is mapped."] },
    });
    expect(mappingFieldErrors(error)).toEqual({
      "columns.name": "Bu alan bu tür ve modda zorunlu; bir kolonla eşleyin.",
      priceListId: "Fiyat kolonu eşlendiğinde fiyat listesi seçilmeli.",
    });
  });
});

describe("preCheckFile", () => {
  const file = (name: string, size = 1000) => ({ name, size });
  it("accepts .xlsx and .csv up to 10 MB", () => {
    expect(preCheckFile(file("urunler.xlsx"))).toBeNull();
    expect(preCheckFile(file("URUNLER.CSV"))).toBeNull();
  });
  it("rejects .xls, .xlsm, other types, empty and oversized files with a hint", () => {
    expect(preCheckFile(file("eski.xls"))).toMatch(/\.xlsx/);
    expect(preCheckFile(file("makro.xlsm"))).toMatch(/Makro/);
    expect(preCheckFile(file("liste.pdf"))).toMatch(/Yalnızca/);
    expect(preCheckFile(file("bos.csv", 0))).toMatch(/boş/);
    expect(preCheckFile(file("buyuk.xlsx", 10 * 1024 * 1024 + 1))).toMatch(/10 MB/);
  });
});

describe("importPermissions (GET /me, MOCK fixtures)", () => {
  it("owner/admin may import products and prices and roll back", () => {
    for (const role of ["owner", "admin"] as const) {
      const p = importPermissions(accessFor(role));
      expect(p).toMatchObject({ run: true, products: true, prices: true, rollback: true, stockOnly: false });
      expect(runnableTypes(p)).toEqual(["products", "prices"]);
    }
  });

  it("the operations manager only holds imports.run.stock → locked; others have nothing", () => {
    expect(importPermissions(accessFor("manager"))).toMatchObject({ run: false, stockOnly: true });
    expect(importPermissions(accessFor("sales"))).toMatchObject({ run: false, stockOnly: false });
  });

  it("rollback also needs the type's permissions and prices.write when a price list was written", () => {
    const p = { run: true, stockOnly: false, products: true, prices: false, rollback: true };
    expect(canRollbackJob(p, { type: "products", priceListId: null })).toBe(true);
    expect(canRollbackJob(p, { type: "products", priceListId: "pl-1" })).toBe(false);
    expect(canRollbackJob({ ...p, rollback: false }, { type: "products", priceListId: null })).toBe(false);
  });
});

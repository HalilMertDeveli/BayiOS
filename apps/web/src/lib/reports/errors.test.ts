import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { exportErrorMessage, reportErrorMessage, visibleMoney } from "./errors";

const apiError = (status: number, code: string, extra: Record<string, unknown> = {}, retryAfter: number | null = null) =>
  new ApiError("x", status, { code, ...extra }, undefined, retryAfter);

describe("visibleMoney", () => {
  it("hides amounts entirely when the API says they are not visible", () => {
    expect(visibleMoney(false, 1234.5, "TRY")).toBeNull();
    expect(visibleMoney(false, null, "TRY")).toBeNull();
  });
  it("formats visible amounts and shows a dash for a missing one (never 0)", () => {
    expect(visibleMoney(true, 1234.5, "TRY")).toBe("₺1.234,50");
    expect(visibleMoney(true, "99.9", "EUR")).toBe("€99,90");
    expect(visibleMoney(true, null, "TRY")).toBe("—");
  });
});

describe("exportErrorMessage", () => {
  it("429: the hourly limit with the Retry-After wait", () => {
    expect(exportErrorMessage(apiError(429, "rate_limit.exceeded", {}, 1200))).toBe(
      "Saatlik dışa aktarma sınırına ulaşıldı (işletme başına saatte 10 dosya). 20 dakika sonra tekrar deneyin.",
    );
    expect(exportErrorMessage(apiError(429, "rate_limit.exceeded", {}, 30))).toContain("30 saniye sonra");
    // Without a readable Retry-After a safe default is shown.
    expect(exportErrorMessage(apiError(429, "rate_limit.exceeded"))).toContain("15 dakika sonra");
  });

  it("422 reports.export_too_large: narrow the filters", () => {
    expect(exportErrorMessage(apiError(422, "reports.export_too_large"))).toBe(
      "Dosya 20.000 satır sınırını aşıyor. Tarih aralığını veya filtreleri daraltıp tekrar deneyin.",
    );
  });

  it("403 for sales on a non-order report", () => {
    expect(exportErrorMessage(apiError(403, "reports.orders_and_customers_only"))).toContain("yalnızca sipariş raporlarını");
  });
});

describe("reportErrorMessage", () => {
  it("translates range validation errors", () => {
    expect(reportErrorMessage(apiError(422, "validation.failed", { errors: { to: ["The range may cover at most 366 days"] } }))).toBe(
      "Tarih aralığı en fazla 366 gün olabilir.",
    );
    expect(reportErrorMessage(apiError(422, "validation.failed", { errors: { from: ["from must not be after to"] } }))).toBe(
      "Başlangıç tarihi bitiş tarihinden sonra olamaz.",
    );
  });
  it("network and unknown errors get a generic Turkish message", () => {
    expect(reportErrorMessage(new ApiError("x", 0, null, "Sunucuya ulaşılamadı."))).toBe("Sunucuya ulaşılamadı.");
    expect(reportErrorMessage(new Error("boom"))).toBe("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");
  });
});

import { describe, expect, it } from "vitest";
import { entitlementMessage, problemMessage } from "./problems";

describe("entitlement problems (#51) → Turkish", () => {
  it("entitlement.required names the feature and the plan that has it", () => {
    const message = problemMessage(403, {
      code: "entitlement.required",
      feature: "warehouse_locations",
      suggestedPlan: "professional",
      suggestedModule: null,
    });
    expect(message).toContain("Raf/göz (lokasyon) takibi mevcut paketinizde yok.");
    expect(message).toContain("Profesyonel pakette bulunur.");
    expect(message).toContain("Paket ve modüller");
    expect(message).toContain("verileriniz silinmez");
  });

  it("entitlement.required with an add-on suggestion", () => {
    expect(problemMessage(403, { code: "entitlement.required", feature: "ai_order_parsing", suggestedPlan: null, suggestedModule: "ai_assistant" })).toContain(
      "AI WhatsApp asistanı ek modülünde bulunur.",
    );
  });

  it("entitlement.limit_exceeded names the limit with current/limit", () => {
    const message = problemMessage(403, {
      code: "entitlement.limit_exceeded",
      feature: "max_warehouses",
      limit: 1,
      current: 1,
      suggestedPlan: "professional",
      suggestedModule: null,
    });
    expect(message).toContain("Aktif depo sayısı sınırına ulaşıldı (1/1).");
    expect(message).toContain("Daha yüksek sınır Profesyonel pakette.");
  });

  it("monthly AI quota with large numbers", () => {
    expect(
      problemMessage(403, { code: "entitlement.limit_exceeded", feature: "ai_parses_per_month", limit: 1000, current: 1000, suggestedPlan: null, suggestedModule: "ai_assistant" }),
    ).toContain("Aylık AI ayrıştırma sınırına ulaşıldı (1.000/1.000).");
  });

  it("no suggestion and unknown codes still produce a clear sentence", () => {
    const message = problemMessage(403, { code: "entitlement.required", feature: "dealer_portal", suggestedPlan: null, suggestedModule: null });
    expect(message.startsWith("Bayi portalı mevcut paketinizde yok.")).toBe(true);
    expect(message).not.toContain("bulunur");
    expect(problemMessage(403, { code: "entitlement.required" })).toContain("Bu özellik mevcut paketinizde yok.");
    expect(problemMessage(403, { code: "entitlement.required", feature: "new_feature_x" })).toContain("new_feature_x mevcut paketinizde yok.");
  });

  it("other codes are untouched", () => {
    expect(entitlementMessage({ code: "auth.forbidden" })).toBeNull();
    expect(problemMessage(403, { code: "auth.forbidden" })).toBe("Bu işlem için yetkiniz yok. Gerekirse işletme yöneticinizle görüşün.");
  });
});

describe("report problems", () => {
  it("maps the report-specific codes", () => {
    expect(problemMessage(403, { code: "reports.orders_and_customers_only" })).toContain("yalnızca sipariş raporlarını");
    expect(problemMessage(422, { code: "reports.export_too_large" })).toContain("20.000 satır");
  });
});

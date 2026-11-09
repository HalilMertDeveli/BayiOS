import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { createEntitlements, featureGate, warehouseGate } from "@/lib/billing/entitlements";
import { SessionContext } from "@/lib/session/session-context";
import { entitlementsFor, type FixtureRole } from "@/test/me-fixtures";
import { mockSession } from "@/test/session";
import { LockedFeature, gateBadgeText, gateDescription } from "./locked-feature";

const basic = createEntitlements(entitlementsFor("basic"));
const locations = featureGate(basic, "warehouse_locations");
const ai = featureGate(createEntitlements(entitlementsFor("professional")), "ai_order_parsing");
// Basic allows 999 warehouses (OD-02); an operator limit of 1 shows the locked state.
const warehouses = warehouseGate(createEntitlements({ ...entitlementsFor("basic"), limits: [{ code: "max_warehouses", value: 1 }] }), [{ status: "active" }]);

function renderLocked(role: FixtureRole) {
  if (!locations.locked) throw new Error("expected a locked gate");
  return render(
    <SessionContext value={mockSession(role, { plan: "basic" })}>
      <LockedFeature gate={locations} title="Yeni lokasyon eklenemiyor" />
    </SessionContext>,
  );
}

describe("LockedFeature", () => {
  it("names where the feature is available", () => {
    if (!locations.locked || !ai.locked || !warehouses.locked) throw new Error("expected locked gates");
    expect(gateBadgeText(locations)).toBe("Profesyonel pakette");
    expect(gateBadgeText(ai)).toBe("AI WhatsApp asistanı modülünde");
    expect(gateDescription(warehouses)).toBe("Aktif depo sayısı sınırı paketinizde 1; şu an 1 kullanılıyor.");
    expect(gateDescription(locations)).toBe("Raf/göz (lokasyon) takibi mevcut paketinizde yok.");
  });

  it("owner gets a link to the plan page and is told existing data stays visible", () => {
    renderLocked("owner");
    const note = screen.getByRole("note");
    expect(note.textContent).toContain("Profesyonel pakette");
    expect(note.textContent).toContain("Mevcut kayıtlarınız görünmeye devam eder.");
    expect(screen.getByRole("link", { name: "Paket ve modülleri gör" }).getAttribute("href")).toBe("/ayarlar/paket");
  });

  it("a manager (no billing.read) is pointed to the owner instead", () => {
    renderLocked("manager");
    expect(screen.queryByRole("link", { name: "Paket ve modülleri gör" })).toBeNull();
    expect(screen.getByRole("note").textContent).toContain("işletme sahibiyle görüşün");
  });
});

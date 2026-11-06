import { describe, expect, it } from "vitest";
import { createEntitlements } from "@/lib/billing/entitlements";
import { entitlementsFor } from "@/test/me-fixtures";
import { accessFor } from "@/test/session";
import { countText, dashboardTiles, quickLinks, summarizeOrders } from "./tiles";

const trial = createEntitlements(entitlementsFor("trial"));
const basic = createEntitlements(entitlementsFor("basic"));

describe("dashboardTiles — one tile per endpoint the role may call (MOCK /me permissions)", () => {
  it("owner and admin see everything, including the plan", () => {
    for (const role of ["owner", "admin"] as const) {
      expect(dashboardTiles(accessFor(role), trial)).toEqual({
        pendingApproval: true,
        todayOrders: true,
        criticalStock: true,
        myTasks: true,
        whatsappReview: true,
        plan: true,
      });
    }
  });

  it("manager: no plan tile (no billing.read)", () => {
    expect(dashboardTiles(accessFor("manager"), trial)).toMatchObject({ pendingApproval: true, criticalStock: true, myTasks: true, plan: false });
  });

  it("sales: order reports only (footnote 4) — no critical stock, no tasks", () => {
    expect(dashboardTiles(accessFor("sales"), trial)).toEqual({
      pendingApproval: true,
      todayOrders: true,
      criticalStock: false,
      myTasks: false,
      whatsappReview: true,
      plan: false,
    });
  });

  it("viewer: read-only overview without WhatsApp; warehouse staff: only their tasks", () => {
    expect(dashboardTiles(accessFor("viewer"), trial)).toMatchObject({ pendingApproval: true, todayOrders: true, criticalStock: true, whatsappReview: false, myTasks: false });
    expect(dashboardTiles(accessFor("warehouse_staff"), trial)).toEqual({
      pendingApproval: false,
      todayOrders: false,
      criticalStock: false,
      myTasks: true,
      whatsappReview: false,
      plan: false,
    });
  });

  it("the WhatsApp tile needs the plan feature too (Temel without the add-on)", () => {
    expect(dashboardTiles(accessFor("owner"), basic).whatsappReview).toBe(false);
    expect(quickLinks(accessFor("owner"), basic).map((l) => l.label)).not.toContain("WhatsApp'tan sipariş");
  });

  it("quick links follow the screens' main permissions", () => {
    expect(quickLinks(accessFor("owner"), trial).map((l) => l.href)).toEqual(["/siparisler/yeni", "/siparisler/whatsapp", "/urunler/yeni", "/depo/mal-kabul"]);
    expect(quickLinks(accessFor("sales"), trial).map((l) => l.href)).toEqual(["/siparisler/yeni", "/siparisler/whatsapp"]);
    expect(quickLinks(accessFor("viewer"), trial)).toEqual([]);
  });
});

describe("dashboard numbers", () => {
  it("countText marks a full page", () => {
    expect(countText(3, false)).toBe("3");
    expect(countText(50, true)).toBe("50+");
  });

  it("summarizeOrders: counts every status, amounts per currency without cancelled, null amounts ignored", () => {
    expect(
      summarizeOrders([
        { status: "pending_approval", currency: "TRY", orderCount: 2, grandTotal: "100.50" },
        { status: "approved", currency: "TRY", orderCount: "1", grandTotal: 20 },
        { status: "cancelled", currency: "TRY", orderCount: 1, grandTotal: 999 },
        { status: "approved", currency: "USD", orderCount: 1, grandTotal: null },
        { status: "draft", currency: "TRY", orderCount: 0, grandTotal: 0 },
      ]),
    ).toEqual({
      total: 5,
      byStatus: [
        { status: "pending_approval", count: 2 },
        { status: "approved", count: 2 },
        { status: "cancelled", count: 1 },
      ],
      amounts: [{ currency: "TRY", amount: 120.5 }],
    });
  });
});

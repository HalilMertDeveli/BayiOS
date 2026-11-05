import { describe, expect, it } from "vitest";
import { NO_ACCESS, createAccess } from "@/lib/session/access";
import type { FixtureRole } from "@/test/me-fixtures";
import { accessFor } from "@/test/session";
import { activeHref, findNavItem, homeFor, isActive, navFor, navSections } from "./navigation";

const labelsFor = (role: FixtureRole) => navFor(accessFor(role)).flatMap((s) => s.items.map((i) => i.label));

describe("navigation — menu from GET /me effective permissions", () => {
  it("shows the full menu to owner and admin", () => {
    const all = ["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar", "Müşteriler", "Fiyat listeleri", "Raporlar", "İçe aktarma", "Ayarlar"];
    expect(labelsFor("owner")).toEqual(all);
    expect(labelsFor("admin")).toEqual(all);
  });

  it("follows the permissions for restricted roles", () => {
    // sales: tasks.read.all K¹² (tasks/shipments of its own orders), reports K⁴, no imports or settings
    expect(labelsFor("sales")).toEqual(["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar", "Müşteriler", "Fiyat listeleri", "Raporlar"]);
    // viewer: read-only; tasks.read.all also opens shipments (x-permission-any-of)
    expect(labelsFor("viewer")).toEqual(["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar", "Müşteriler", "Fiyat listeleri", "Raporlar"]);
    // warehouse_staff: task-linked orders (K⁸) keep Siparişler; no dashboard, customers, prices, reports, imports, settings
    expect(labelsFor("warehouse_staff")).toEqual(["Siparişler", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar"]);
    expect(labelsFor("manager")).toEqual(expect.arrayContaining(["İçe aktarma", "Ayarlar", "Fiyat listeleri"]));
  });

  it("hides Siparişler only when there is no order permission at all", () => {
    const labels = (grants: Parameters<typeof createAccess>[0]) => navFor(createAccess(grants)).flatMap((s) => s.items.map((i) => i.label));
    expect(labels([{ permission: "orders.read", scope: "conditional", condition: "x" }])).toContain("Siparişler");
    expect(labels([{ permission: "catalog.read", scope: "all", condition: null }])).toEqual(["Ürünler"]);
  });

  it("drops sections that would be empty; nothing without a tenant", () => {
    expect(navFor(accessFor("warehouse_staff")).map((s) => s.id)).toEqual(["operasyon"]);
    expect(navFor(NO_ACCESS)).toEqual([]);
  });

  it("has unique hrefs", () => {
    const hrefs = navSections.flatMap((s) => s.items.map((i) => i.href));
    expect(new Set(hrefs).size).toBe(hrefs.length);
  });

  it("finds items by href", () => {
    expect(findNavItem("/ice-aktarma").label).toBe("İçe aktarma");
    expect(() => findNavItem("/yok")).toThrow();
  });

  it("matches the active item including sub-pages, but not prefixes", () => {
    expect(isActive("/urunler", "/urunler")).toBe(true);
    expect(isActive("/urunler/42", "/urunler")).toBe(true);
    expect(isActive("/urunlerx", "/urunler")).toBe(false);
  });

  it("marks only the most specific item as current", () => {
    const hrefs = ["/depo", "/depo/gorevler", "/depo/sevkiyatlar"];
    expect(activeHref("/depo/gorevler/42", hrefs)).toBe("/depo/gorevler");
    expect(activeHref("/depo/mal-kabul", hrefs)).toBe("/depo");
    expect(activeHref("/stok", hrefs)).toBeNull();
  });

  it("sends task-only users (warehouse staff) to their tasks, everyone else to the dashboard", () => {
    expect(homeFor(accessFor("warehouse_staff"))).toBe("/depo/gorevler");
    expect(homeFor(accessFor("owner"))).toBe("/pano");
    expect(homeFor(accessFor("sales"))).toBe("/pano");
    expect(homeFor(accessFor("viewer"))).toBe("/pano");
    expect(homeFor(NO_ACCESS)).toBe("/pano");
  });
});

import { describe, expect, it } from "vitest";
import { NO_ACCESS, createAccess } from "@/lib/session/access";
import type { FixtureRole } from "@/test/me-fixtures";
import { accessFor } from "@/test/session";
import { activeHref, breadcrumbsFor, findNavItem, homeFor, isActive, navFor, navSections } from "./navigation";

const labelsFor = (role: FixtureRole) => navFor(accessFor(role)).flatMap((s) => s.items.map((i) => i.label));

describe("navigation — menu from GET /me effective permissions", () => {
  it("shows the full menu to owner and admin", () => {
    const all = ["Pano", "Raporlar", "Siparişler", "Müşteriler", "Fiyat listeleri", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar", "Transferler", "Sayımlar", "İçe aktarma", "Ayarlar"];
    expect(labelsFor("owner")).toEqual(all);
    expect(labelsFor("admin")).toEqual(all);
  });

  it("follows the permissions for restricted roles", () => {
    // sales: tasks.read.all K¹² (tasks/shipments of its own orders), reports K⁴, no imports or settings
    expect(labelsFor("sales")).toEqual(["Pano", "Raporlar", "Siparişler", "Müşteriler", "Fiyat listeleri", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar"]);
    // viewer: read-only; tasks.read.all also opens shipments (x-permission-any-of)
    expect(labelsFor("viewer")).toEqual(["Pano", "Raporlar", "Siparişler", "Müşteriler", "Fiyat listeleri", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar"]);
    // warehouse_staff: task-linked orders (K⁸) keep Siparişler; receives transfers and counts (no create); no dashboard, customers, prices, reports, imports, settings
    expect(labelsFor("warehouse_staff")).toEqual(["Siparişler", "Ürünler", "Stok", "Depo", "Görevler", "Sevkiyatlar", "Transferler", "Sayımlar"]);
    // sales and viewer hold no transfer or count permission.
    expect(labelsFor("viewer")).not.toContain("Transferler");
    expect(labelsFor("manager")).toEqual(expect.arrayContaining(["İçe aktarma", "Ayarlar", "Fiyat listeleri"]));
  });

  it("hides Siparişler only when there is no order permission at all", () => {
    const labels = (grants: Parameters<typeof createAccess>[0]) => navFor(createAccess(grants)).flatMap((s) => s.items.map((i) => i.label));
    expect(labels([{ permission: "orders.read", scope: "conditional", condition: "x", parameters: null }])).toContain("Siparişler");
    expect(labels([{ permission: "catalog.read", scope: "all", condition: null, parameters: null }])).toEqual(["Ürünler"]);
  });

  it("drops sections that would be empty; nothing without a tenant", () => {
    expect(navFor(accessFor("warehouse_staff")).map((s) => s.id)).toEqual(["satis", "stok"]);
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

  it("groups the menu by area of work", () => {
    expect(navSections.map((s) => s.label)).toEqual(["Genel", "Satış", "Stok ve depo", "Yönetim"]);
  });
});

describe("breadcrumbsFor", () => {
  const sections = navFor(accessFor("owner"));

  it("names the section and the current item on a menu page", () => {
    expect(breadcrumbsFor("/siparisler", sections)).toEqual([{ label: "Satış" }, { label: "Siparişler", href: undefined }]);
  });

  it("links the item from its sub-pages and names known segments", () => {
    expect(breadcrumbsFor("/siparisler/yeni", sections)).toEqual([
      { label: "Satış" },
      { label: "Siparişler", href: "/siparisler" },
      { label: "Yeni", href: undefined },
    ]);
    expect(breadcrumbsFor("/ayarlar/ekip", sections).map((c) => c.label)).toEqual(["Yönetim", "Ayarlar", "Ekip"]);
    expect(breadcrumbsFor("/raporlar/kritik-stok", sections).map((c) => c.label)).toEqual(["Genel", "Raporlar", "Kritik stok"]);
  });

  it("reads record ids as Ayrıntı and skips grouping segments", () => {
    expect(breadcrumbsFor("/depo/gorevler/42", sections).map((c) => c.label)).toEqual(["Stok ve depo", "Görevler", "Ayrıntı"]);
    expect(breadcrumbsFor("/siparisler/whatsapp/7", sections)).toEqual([
      { label: "Satış" },
      { label: "Siparişler", href: "/siparisler" },
      { label: "WhatsApp'tan sipariş", href: "/siparisler/whatsapp" },
      { label: "Ayrıntı", href: undefined },
    ]);
    expect(breadcrumbsFor("/stok/varyant/9", sections).map((c) => c.label)).toEqual(["Stok ve depo", "Stok", "Varyant stoğu"]);
  });

  it("is empty outside the menu", () => {
    expect(breadcrumbsFor("/kurulum", sections)).toEqual([]);
  });
});

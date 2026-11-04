import { describe, expect, it } from "vitest";
import { ROLES, findNavItem, isActive, navForRole, navSections, type Role } from "./navigation";

const labelsFor = (role: Role) => navForRole(role).flatMap((s) => s.items.map((i) => i.label));

describe("navigation", () => {
  it("shows the full menu to owner and admin", () => {
    const all = ["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Müşteriler", "Fiyat listeleri", "Raporlar", "İçe aktarma", "Ayarlar"];
    expect(labelsFor("owner")).toEqual(all);
    expect(labelsFor("admin")).toEqual(all);
  });

  it("follows the permission matrix for restricted roles", () => {
    // sales: prices.read.sell, no imports.run, no tenant settings / members.read
    expect(labelsFor("sales")).toEqual(["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Müşteriler", "Fiyat listeleri", "Raporlar"]);
    // viewer: read-only, no imports or settings
    expect(labelsFor("viewer")).toEqual(["Pano", "Siparişler", "Ürünler", "Stok", "Depo", "Müşteriler", "Fiyat listeleri", "Raporlar"]);
    // warehouse_staff: task-focused; no customers, prices, reports, imports, settings
    expect(labelsFor("warehouse_staff")).toEqual(["Siparişler", "Ürünler", "Stok", "Depo"]);
    // manager: imports.run.stock and members.read
    expect(labelsFor("manager")).toContain("İçe aktarma");
    expect(labelsFor("manager")).toContain("Ayarlar");
    expect(labelsFor("manager")).toContain("Fiyat listeleri");
  });

  it("drops sections that would be empty", () => {
    expect(navForRole("warehouse_staff").map((s) => s.id)).toEqual(["operasyon"]);
  });

  it("has unique hrefs and only known roles", () => {
    const hrefs = navSections.flatMap((s) => s.items.map((i) => i.href));
    expect(new Set(hrefs).size).toBe(hrefs.length);
    navSections.forEach((s) => s.items.forEach((i) => i.roles.forEach((r) => expect(ROLES).toContain(r))));
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
});

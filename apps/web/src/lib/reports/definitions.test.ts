import { describe, expect, it } from "vitest";
import { accessFor } from "@/test/session";
import { ordersOnly, reportAccess, reportDefinition, reportHref, visibleReports } from "./definitions";

const ids = (role: Parameters<typeof accessFor>[0]) => visibleReports(accessFor(role)).map((r) => r.id);

describe("report visibility (MOCK /me permissions)", () => {
  it("owner and admin see every report including AI usage (billing.read)", () => {
    const all = ["orders", "top-products", "critical-stock", "stock-movements", "picking-tasks", "ai-usage"];
    expect(ids("owner")).toEqual(all);
    expect(ids("admin")).toEqual(all);
  });

  it("manager and viewer see the business reports but not AI usage", () => {
    expect(ids("manager")).toEqual(["orders", "top-products", "critical-stock", "stock-movements", "picking-tasks"]);
    expect(ids("viewer")).toEqual(["orders", "top-products", "critical-stock", "stock-movements", "picking-tasks"]);
  });

  it("sales (footnote 4) sees and exports only the order reports", () => {
    const sales = accessFor("sales");
    expect(ids("sales")).toEqual(["orders", "top-products"]);
    expect(ordersOnly(sales)).toBe(true);
    expect(reportAccess(sales, reportDefinition("orders"))).toEqual({ read: true, export: true });
    expect(reportAccess(sales, reportDefinition("critical-stock"))).toEqual({ read: false, export: false });
    expect(ordersOnly(accessFor("owner"))).toBe(false);
  });

  it("warehouse staff sees none", () => {
    expect(ids("warehouse_staff")).toEqual([]);
  });

  it("builds Turkish URLs", () => {
    expect(reportHref("critical-stock")).toBe("/raporlar/kritik-stok");
    expect(reportHref("ai-usage")).toBe("/raporlar/ai-kullanimi");
  });
});

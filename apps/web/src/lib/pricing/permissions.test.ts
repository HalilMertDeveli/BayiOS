import { describe, expect, it } from "vitest";
import { customerPermissions } from "@/lib/customers/permissions";
import { catalogPermissions } from "@/lib/catalog/permissions";
import { NO_ACCESS } from "@/lib/session/access";
import type { FixtureRole } from "@/test/me-fixtures";
import { accessFor } from "@/test/session";
import { pricingPermissions } from "./permissions";

const ROLES: FixtureRole[] = ["owner", "admin", "manager", "sales", "warehouse_staff", "viewer"];

describe("pricingPermissions — derived from GET /me effective permissions", () => {
  it("per role (default tenant rules)", () => {
    const table = Object.fromEntries(ROLES.map((r) => [r, pricingPermissions(accessFor(r))]));
    expect(table).toEqual({
      owner: { readSell: true, readCost: true, write: true, assign: true },
      admin: { readSell: true, readCost: true, write: true, assign: true },
      // prices.read.cost depends on a tenant rule for manager/viewer (K³): off by default.
      manager: { readSell: true, readCost: false, write: false, assign: true },
      sales: { readSell: true, readCost: false, write: false, assign: false },
      warehouse_staff: { readSell: false, readCost: false, write: false, assign: false },
      viewer: { readSell: true, readCost: false, write: false, assign: false },
    });
  });

  it("follows the tenant rule for cost prices (the API reports it as granted when the rule is on)", () => {
    expect(pricingPermissions(accessFor("manager", { showCostToManager: true })).readCost).toBe(true);
    expect(pricingPermissions(accessFor("viewer", { showCostToViewer: true })).readCost).toBe(true);
  });

  it("grants nothing without a tenant", () => {
    expect(pricingPermissions(NO_ACCESS)).toEqual({ readSell: false, readCost: false, write: false, assign: false });
  });
});

describe("customerPermissions — anonymize = tenant.settings.manage", () => {
  it("per role", () => {
    const table = Object.fromEntries(ROLES.map((r) => [r, customerPermissions(accessFor(r))]));
    expect(table).toEqual({
      owner: { read: true, write: true, manageGroups: true, assignPriceLists: true, anonymize: true },
      admin: { read: true, write: true, manageGroups: true, assignPriceLists: true, anonymize: true },
      manager: { read: true, write: true, manageGroups: true, assignPriceLists: true, anonymize: false },
      sales: { read: true, write: true, manageGroups: false, assignPriceLists: false, anonymize: false },
      warehouse_staff: { read: false, write: false, manageGroups: false, assignPriceLists: false, anonymize: false },
      viewer: { read: true, write: false, manageGroups: false, assignPriceLists: false, anonymize: false },
    });
  });
});

describe("catalogPermissions", () => {
  it("per role", () => {
    expect(catalogPermissions(accessFor("owner"))).toEqual({ write: true, manageFields: true });
    expect(catalogPermissions(accessFor("manager"))).toEqual({ write: true, manageFields: false });
    expect(catalogPermissions(accessFor("sales"))).toEqual({ write: false, manageFields: false });
    expect(catalogPermissions(accessFor("viewer"))).toEqual({ write: false, manageFields: false });
    expect(catalogPermissions(NO_ACCESS)).toEqual({ write: false, manageFields: false });
  });
});

import { describe, expect, it } from "vitest";
import { customerPermissions } from "@/lib/customers/permissions";
import { ROLES } from "@/lib/navigation";
import { pricingPermissions } from "./permissions";

describe("pricingPermissions (roles-permissions §3.2)", () => {
  it("matches the API's unconditional grants per role", () => {
    const table = Object.fromEntries(ROLES.map((r) => [r, pricingPermissions(r)]));
    expect(table).toEqual({
      owner: { readSell: true, readCost: true, write: true, assign: true },
      admin: { readSell: true, readCost: true, write: true, assign: true },
      // prices.read.cost is conditional (K³) for manager/viewer: purchase lists stay hidden.
      manager: { readSell: true, readCost: false, write: false, assign: true },
      sales: { readSell: true, readCost: false, write: false, assign: false },
      warehouse_staff: { readSell: false, readCost: false, write: false, assign: false },
      viewer: { readSell: true, readCost: false, write: false, assign: false },
    });
  });

  it("grants nothing without a role", () => {
    expect(pricingPermissions(null)).toEqual({ readSell: false, readCost: false, write: false, assign: false });
  });
});

describe("customerPermissions (roles-permissions §3.5 + anonymize = tenant.settings.manage)", () => {
  it("matches the API per role", () => {
    const table = Object.fromEntries(ROLES.map((r) => [r, customerPermissions(r)]));
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

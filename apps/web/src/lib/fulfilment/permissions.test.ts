import { describe, expect, it } from "vitest";
import { NO_ACCESS } from "@/lib/session/access";
import { accessFor } from "@/test/session";
import { fulfilmentPermissions } from "./permissions";

describe("fulfilment permissions — derived from GET /me effective permissions", () => {
  it("staff work their own and pool tasks but cannot assign or list all tasks", () => {
    expect(fulfilmentPermissions(accessFor("warehouse_staff"))).toEqual({
      readAll: false,
      readOwn: true,
      readDetail: true,
      assign: false,
      claim: true,
      pick: true,
      pack: true,
      reportShortage: true,
      ship: true,
    });
  });

  it("supervisors assign and see everything; only the manager also claims", () => {
    for (const role of ["owner", "admin", "manager"] as const) {
      const p = fulfilmentPermissions(accessFor(role));
      expect(p.assign && p.readAll && p.readOwn && p.pick && p.ship, role).toBe(true);
    }
    expect(fulfilmentPermissions(accessFor("manager")).claim).toBe(true);
    expect(fulfilmentPermissions(accessFor("owner")).claim).toBe(false);
  });

  it("viewer and sales (K¹², own orders' tasks) read the list and details, but do no warehouse work", () => {
    for (const role of ["viewer", "sales"] as const) {
      expect(fulfilmentPermissions(accessFor(role))).toEqual({
        readAll: true,
        readOwn: false,
        readDetail: true,
        assign: false,
        claim: false,
        pick: false,
        pack: false,
        reportShortage: false,
        ship: false,
      });
    }
  });

  it("no tenant means nothing", () => {
    expect(Object.values(fulfilmentPermissions(NO_ACCESS)).some(Boolean)).toBe(false);
  });
});

import { describe, expect, it } from "vitest";
import { ROLES } from "@/lib/navigation";
import { fulfilmentPermissions } from "./permissions";

describe("fulfilment permissions (roles-permissions §3.6, only ✓ grants)", () => {
  it("staff work their own and pool tasks but cannot assign or see all tasks", () => {
    expect(fulfilmentPermissions("warehouse_staff")).toEqual({
      readAll: false,
      readOwn: true,
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
      const p = fulfilmentPermissions(role);
      expect(p.assign && p.readAll && p.readOwn && p.pick && p.ship, role).toBe(true);
    }
    expect(fulfilmentPermissions("manager").claim).toBe(true);
    expect(fulfilmentPermissions("owner").claim).toBe(false);
    expect(fulfilmentPermissions("admin").claim).toBe(false);
  });

  it("viewer reads the task list only; sales (K¹²) and no role get nothing", () => {
    expect(fulfilmentPermissions("viewer")).toMatchObject({ readAll: true, readOwn: false, pick: false, ship: false, assign: false });
    for (const p of [fulfilmentPermissions("sales"), fulfilmentPermissions(null)]) expect(Object.values(p).some(Boolean)).toBe(false);
    expect(ROLES.length).toBe(6);
  });
});

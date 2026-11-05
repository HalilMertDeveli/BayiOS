import { describe, expect, it } from "vitest";
import { NO_ACCESS } from "@/lib/session/access";
import type { FixtureRole } from "@/test/me-fixtures";
import { accessFor } from "@/test/session";
import { inventoryPermissions } from "./permissions";

const ROLES: FixtureRole[] = ["owner", "admin", "manager", "sales", "warehouse_staff", "viewer"];

describe("inventoryPermissions — derived from GET /me effective permissions", () => {
  it("per role (default tenant rules)", () => {
    const table = Object.fromEntries(ROLES.map((r) => [r, inventoryPermissions(accessFor(r))]));
    const full = { read: true, movements: true, movementsOwnOnly: false, receive: true, adjust: true, manageWarehouses: true };
    expect(table).toEqual({
      owner: full,
      admin: full,
      manager: full,
      sales: { read: true, movements: false, movementsOwnOnly: false, receive: false, adjust: false, manageWarehouses: false },
      // K⁵: own movements and the task context — the history is shown, the API filters the rows.
      warehouse_staff: { read: true, movements: true, movementsOwnOnly: true, receive: true, adjust: false, manageWarehouses: false },
      viewer: { read: true, movements: true, movementsOwnOnly: false, receive: false, adjust: false, manageWarehouses: false },
    });
  });

  it("grants nothing without a tenant", () => {
    expect(Object.values(inventoryPermissions(NO_ACCESS)).some(Boolean)).toBe(false);
  });
});

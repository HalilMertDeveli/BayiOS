import { describe, expect, it } from "vitest";
import { ROLES } from "@/lib/navigation";
import { inventoryPermissions } from "./permissions";

describe("inventoryPermissions (roles-permissions §3.4)", () => {
  it("matches the API's unconditional grants per role", () => {
    const table = Object.fromEntries(ROLES.map((r) => [r, inventoryPermissions(r)]));
    expect(table).toEqual({
      owner: { read: true, movements: true, receive: true, adjust: true, manageWarehouses: true },
      admin: { read: true, movements: true, receive: true, adjust: true, manageWarehouses: true },
      manager: { read: true, movements: true, receive: true, adjust: true, manageWarehouses: true },
      sales: { read: true, movements: false, receive: false, adjust: false, manageWarehouses: false },
      warehouse_staff: { read: true, movements: false, receive: true, adjust: false, manageWarehouses: false },
      viewer: { read: true, movements: true, receive: false, adjust: false, manageWarehouses: false },
    });
  });

  it("grants nothing without a role", () => {
    expect(inventoryPermissions(null)).toEqual({ read: false, movements: false, receive: false, adjust: false, manageWarehouses: false });
  });
});

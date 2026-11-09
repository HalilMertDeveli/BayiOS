import { describe, expect, it } from "vitest";
import { accessFor } from "@/test/session";
import { transitLabel, transitTransferId, transferNumberFromId } from "./transit";
import { countPermissions, transferPermissions } from "./permissions";

describe("transfer and count permissions (roles-permissions §3.4, MOCK /me fixtures)", () => {
  it("managers create and receive; warehouse staff only receive and count", () => {
    expect(transferPermissions(accessFor("manager"))).toEqual({ read: true, manage: true, receive: true, relocate: true });
    expect(transferPermissions(accessFor("warehouse_staff"))).toEqual({ read: true, manage: false, receive: true, relocate: false });
    expect(transferPermissions(accessFor("viewer")).read).toBe(false);
    expect(countPermissions(accessFor("owner"))).toEqual({ read: true, manage: true, perform: true });
    expect(countPermissions(accessFor("warehouse_staff"))).toEqual({ read: true, manage: false, perform: true });
    expect(countPermissions(accessFor("sales")).read).toBe(false);
  });
});

describe("in-transit movements", () => {
  it("recognises the transfer's own ledger point and names it", () => {
    const id = "01937a2b-0000-7000-8000-00001a2b3c4d";
    expect(transitTransferId({ warehouseId: id, sourceId: id })).toBe(id);
    expect(transitTransferId({ warehouseId: "w-1", sourceId: id })).toBeNull();
    expect(transitTransferId({ warehouseId: "w-1", sourceId: null })).toBeNull();
    expect(transferNumberFromId(id)).toBe("TRF-1A2B3C4D");
    expect(transitLabel(id)).toBe("Yolda (Transfer TRF-1A2B3C4D)");
  });
});

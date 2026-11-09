import { describe, expect, it } from "vitest";
import { accessFor } from "@/test/session";
import { movementReference, transitLabel, transitTransferId } from "./transit";
import { countPermissions, transferPermissions } from "./permissions";

describe("transfer and count permissions (roles-permissions §3.4, MOCK /me fixtures)", () => {
  it("managers create and receive; warehouse staff only receive and count", () => {
    expect(transferPermissions(accessFor("manager"))).toEqual({ read: true, manage: true, receive: true, relocate: true });
    // #98: shelf moves have their own permission, which warehouse staff hold (inter-warehouse transfers stay managerial).
    expect(transferPermissions(accessFor("warehouse_staff"))).toEqual({ read: true, manage: false, receive: true, relocate: true });
    expect(transferPermissions(accessFor("sales")).relocate).toBe(false);
    expect(transferPermissions(accessFor("viewer")).relocate).toBe(false);
    expect(transferPermissions(accessFor("viewer")).read).toBe(false);
    expect(countPermissions(accessFor("owner"))).toEqual({ read: true, manage: true, perform: true });
    expect(countPermissions(accessFor("warehouse_staff"))).toEqual({ read: true, manage: false, perform: true });
    expect(countPermissions(accessFor("sales")).read).toBe(false);
  });
});

describe("in-transit movements", () => {
  it("uses the API's isTransit flag and transferNumber (no client-side derivation)", () => {
    const id = "01937a2b-0000-7000-8000-00001a2b3c4d";
    expect(transitTransferId({ isTransit: true, sourceId: id })).toBe(id);
    expect(transitTransferId({ isTransit: false, sourceId: id })).toBeNull();
    expect(transitTransferId({ isTransit: true, sourceId: null })).toBeNull();
    expect(transitLabel({ transferNumber: "TRF-0042" })).toBe("Yolda (Transfer TRF-0042)");
    expect(transitLabel({ transferNumber: null })).toBe("Yolda (transfer)");
    expect(movementReference({ reason: null, transferNumber: "TRF-0042" })).toBe("Transfer TRF-0042");
    expect(movementReference({ reason: "Raf düzeni", transferNumber: null })).toBe("Raf düzeni");
    expect(movementReference({ reason: null, transferNumber: null })).toBeNull();
  });
});

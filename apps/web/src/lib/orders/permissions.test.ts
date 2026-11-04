import { describe, expect, it } from "vitest";
import { orderPermissions, type OrderPermissions } from "./permissions";

describe("orderPermissions (roles-permissions §3.5, only ✓ passes — K/O fail-closed as the API enforces)", () => {
  const all: OrderPermissions = { read: true, create: true, editDraft: true, submit: true, approve: true, cancel: true, overridePrice: true };

  it.each(["owner", "admin", "manager"] as const)("%s has every order-desk permission", (role) => {
    expect(orderPermissions(role)).toEqual(all);
  });

  it("sales creates and submits; cannot edit drafts (O), approve (K⁹), cancel (K¹¹) or override prices (K¹⁰)", () => {
    expect(orderPermissions("sales")).toEqual({ read: true, create: true, editDraft: false, submit: true, approve: false, cancel: false, overridePrice: false });
  });

  it("viewer is read-only", () => {
    expect(orderPermissions("viewer")).toEqual({ read: true, create: false, editDraft: false, submit: false, approve: false, cancel: false, overridePrice: false });
  });

  it("warehouse_staff (K⁸) and no role see nothing", () => {
    const none = { read: false, create: false, editDraft: false, submit: false, approve: false, cancel: false, overridePrice: false };
    expect(orderPermissions("warehouse_staff")).toEqual(none);
    expect(orderPermissions(null)).toEqual(none);
  });
});

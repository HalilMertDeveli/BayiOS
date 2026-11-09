import { describe, expect, it } from "vitest";
import { NO_ACCESS, createAccess } from "@/lib/session/access";
import { accessFor } from "@/test/session";
import { orderPermissions } from "./permissions";

describe("orderPermissions — derived from GET /me effective permissions", () => {
  it.each(["owner", "admin", "manager"] as const)("%s has every order-desk permission, unconditionally", (role) => {
    expect(orderPermissions(accessFor(role))).toEqual({
      read: true,
      readTaskLinkedOnly: false,
      create: true,
      editDraft: true,
      editDraftOwnOnly: false,
      submit: true,
      approve: true,
      complete: true,
      cancel: true,
      cancelOwnOpenOnly: false,
      overridePrice: true,
      discount: true,
      discountLimited: false,
      maxDiscountPercent: null,
    });
  });

  it("sales: own drafts (O), own open cancels (K¹¹), no price override while the discount limit is 0", () => {
    expect(orderPermissions(accessFor("sales"))).toMatchObject({
      read: true,
      create: true,
      editDraft: true,
      editDraftOwnOnly: true,
      cancel: true,
      cancelOwnOpenOnly: true,
      overridePrice: false,
      discount: false,
      // orders.edit.approved is not granted to sales: no "Tamamla".
      complete: false,
    });
  });

  it("sales with a tenant discount limit gets discounts but no manual price (K¹⁰)", () => {
    expect(orderPermissions(accessFor("sales", { salesMaxDiscountPercent: 10 }))).toMatchObject({ overridePrice: false, discount: true, discountLimited: true, maxDiscountPercent: 10 });
  });

  it("warehouse_staff reads task-linked orders only (K⁸) and has no order-desk action", () => {
    const p = orderPermissions(accessFor("warehouse_staff"));
    expect(p).toMatchObject({ read: true, readTaskLinkedOnly: true, create: false, editDraft: false, submit: false, approve: false, complete: false, cancel: false, discount: false });
  });

  it("viewer is read-only; no tenant means nothing", () => {
    expect(orderPermissions(accessFor("viewer"))).toMatchObject({ read: true, create: false, editDraft: false, submit: false, cancel: false });
    expect(Object.values(orderPermissions(NO_ACCESS)).some(Boolean)).toBe(false);
  });

  it("an unknown condition on a restricted grant still shows the action (the API decides per record)", () => {
    const p = orderPermissions(createAccess([{ permission: "orders.cancel", scope: "conditional", condition: "future.rule", parameters: null }]));
    expect(p.cancel).toBe(true);
    expect(p.cancelOwnOpenOnly).toBe(false);
  });
});

import { describe, expect, it } from "vitest";
import { grantsFor } from "@/test/me-fixtures";
import { CONDITIONS, NO_ACCESS, createAccess } from "./access";

describe("createAccess — GET /me activeTenant.permissions", () => {
  const access = createAccess([
    { permission: "orders.read", scope: "all", condition: null },
    { permission: "orders.edit.draft", scope: "own", condition: "orders.own" },
    { permission: "orders.cancel", scope: "conditional", condition: "orders.own_draft_or_pending" },
  ]);

  it("can() is true only for unconditional grants", () => {
    expect(access.can("orders.read")).toBe(true);
    expect(access.can("orders.edit.draft")).toBe(false);
    expect(access.can("orders.cancel")).toBe(false);
    expect(access.can("orders.approve")).toBe(false);
  });

  it("canSome() is true for any scope (all, conditional, own)", () => {
    expect(access.canSome("orders.read")).toBe(true);
    expect(access.canSome("orders.edit.draft")).toBe(true);
    expect(access.canSome("orders.cancel")).toBe(true);
    expect(access.canSome("orders.approve")).toBe(false);
  });

  it("exposes scope and condition for condition-aware display", () => {
    expect(access.scope("orders.edit.draft")).toBe("own");
    expect(access.scope("orders.approve")).toBeNull();
    expect(access.condition("orders.cancel")).toBe(CONDITIONS.ordersOwnDraftOrPending);
    expect(access.condition("orders.read")).toBeNull();
    expect(access.hasCondition("orders.edit.draft", CONDITIONS.ordersOwn)).toBe(true);
    expect(access.hasCondition("orders.read", CONDITIONS.ordersOwn)).toBe(false);
  });

  it("NO_ACCESS and a null list grant nothing", () => {
    expect(NO_ACCESS.canSome("tenant.read")).toBe(false);
    expect(createAccess(null).grants).toEqual([]);
  });

  it("the MOCK fixtures mirror the API's shape: rule-only K cells become 'all' when the rule is on, else disappear", () => {
    const sales = createAccess(grantsFor("sales"));
    expect(sales.scope("orders.approve")).toBe("all"); // tenant rule on by default
    expect(createAccess(grantsFor("sales", { salesCanApproveOrders: false })).scope("orders.approve")).toBeNull();
    expect(sales.scope("orders.price.override")).toBeNull(); // limit 0
    expect(createAccess(grantsFor("sales", { salesMaxDiscountPercent: 5 })).scope("orders.price.override")).toBe("conditional");
    expect(createAccess(grantsFor("warehouse_staff")).condition("orders.read")).toBe(CONDITIONS.ordersTaskLinked);
  });
});

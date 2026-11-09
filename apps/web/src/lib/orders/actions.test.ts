import { describe, expect, it } from "vitest";
import { accessFor } from "@/test/session";
import type { FixtureRole } from "@/test/me-fixtures";
import { availableActions, isDraftEditable, isInFulfilment, isOwnOrder, isReasonAction } from "./actions";
import { ORDER_STATUSES } from "./labels";
import { orderPermissions } from "./permissions";

const ROLES: FixtureRole[] = ["owner", "admin", "manager", "sales", "warehouse_staff", "viewer"];
const ME = "user-me";
const OTHER = "user-other";

const actionsOf = (role: FixtureRole, status: string, fulfillmentStatus = "none", createdBy: string | null = OTHER, rules = {}) =>
  availableActions({ status, fulfillmentStatus, createdBy }, orderPermissions(accessFor(role, rules)), ME).map((a) => a.action);

describe("availableActions — state machine × /me permissions (inventory-and-orders §3.1)", () => {
  it.each(["owner", "admin", "manager"] as const)("%s gets every order-desk transition the state allows", (role) => {
    expect(actionsOf(role, "draft")).toEqual(["submit", "cancel"]);
    expect(actionsOf(role, "pending_approval")).toEqual(["approve", "reject", "withdraw", "cancel"]);
    expect(actionsOf(role, "approved")).toEqual(["cancel"]);
    expect(actionsOf(role, "picking")).toEqual(["cancel"]);
    expect(actionsOf(role, "packed")).toEqual(["cancel"]);
    expect(actionsOf(role, "shipped")).toEqual(["complete"]);
    expect(actionsOf(role, "completed")).toEqual([]);
    expect(actionsOf(role, "cancelled")).toEqual([]);
  });

  it("no cancel after a partial shipment (I9): only close-remaining, for orders.approve holders", () => {
    expect(actionsOf("owner", "picking", "partial")).toEqual(["close-remaining"]);
    expect(actionsOf("manager", "packed", "partial")).toEqual(["close-remaining"]);
    // Nothing shipped yet, or approved without a shipment: nothing to close.
    expect(actionsOf("owner", "picking", "none")).toEqual(["cancel"]);
    expect(actionsOf("owner", "approved", "partial")).toEqual([]);
  });

  it("close-remaining follows the approve grant (sales: tenant rule K⁹); complete needs orders.edit.approved", () => {
    expect(actionsOf("sales", "packed", "partial", ME)).toEqual(["close-remaining"]);
    expect(actionsOf("sales", "packed", "partial", ME, { salesCanApproveOrders: false })).toEqual([]);
    expect(actionsOf("sales", "shipped", "full", ME)).toEqual([]);
    expect(actionsOf("manager", "shipped", "partial")).toEqual(["complete"]);
  });

  it("sales cancels only its own draft / pending order (K¹¹); approve follows the tenant rule (K⁹)", () => {
    expect(actionsOf("sales", "draft", "none", ME)).toEqual(["submit", "cancel"]);
    expect(actionsOf("sales", "draft", "none", OTHER)).toEqual(["submit"]);
    expect(actionsOf("sales", "pending_approval", "none", ME)).toEqual(["approve", "reject", "withdraw", "cancel"]);
    expect(actionsOf("sales", "pending_approval", "none", OTHER, { salesCanApproveOrders: false })).toEqual(["withdraw"]);
    expect(actionsOf("sales", "approved", "none", ME)).toEqual([]);
  });

  it.each(["viewer", "warehouse_staff"] as const)("%s gets no actions in any state", (role) => {
    for (const status of ORDER_STATUSES) expect(actionsOf(role, status, "none", ME)).toEqual([]);
  });

  it("never offers picking, packing or shipping (Warehouse APIs)", () => {
    const all = ROLES.flatMap((role) => ORDER_STATUSES.flatMap((s) => [...actionsOf(role, s), ...actionsOf(role, s, "partial")]));
    expect(new Set(all)).toEqual(new Set(["submit", "approve", "reject", "withdraw", "cancel", "close-remaining", "complete"]));
  });

  it("reject, cancel and close-remaining ask for a reason first; complete does not", () => {
    const owner = orderPermissions(accessFor("owner"));
    const actions = availableActions({ status: "pending_approval", fulfillmentStatus: "none" }, owner);
    expect(actions.filter((a) => a.needsReason).map((a) => a.action)).toEqual(["reject", "cancel"]);
    expect(availableActions({ status: "packed", fulfillmentStatus: "partial" }, owner).map((a) => [a.action, a.needsReason])).toEqual([["close-remaining", true]]);
    expect(availableActions({ status: "shipped", fulfillmentStatus: "full" }, owner).map((a) => [a.action, a.needsReason])).toEqual([["complete", false]]);
    expect(isReasonAction("close-remaining")).toBe(true);
    expect(isReasonAction("complete")).toBe(false);
  });
});

describe("isDraftEditable / isOwnOrder / isInFulfilment", () => {
  it("only drafts are editable; sales only the drafts it created (O)", () => {
    const sales = orderPermissions(accessFor("sales"));
    expect(isDraftEditable({ status: "draft" }, orderPermissions(accessFor("manager")))).toBe(true);
    expect(isDraftEditable({ status: "draft", createdBy: ME }, sales, ME)).toBe(true);
    expect(isDraftEditable({ status: "draft", createdBy: OTHER }, sales, ME)).toBe(false);
    expect(isDraftEditable({ status: "draft", createdBy: null }, sales, ME)).toBe(false);
    expect(isDraftEditable({ status: "pending_approval", createdBy: ME }, sales, ME)).toBe(false);
    expect(isDraftEditable({ status: "draft", createdBy: ME }, orderPermissions(accessFor("viewer")), ME)).toBe(false);
  });

  it("ownership needs both a creator and a signed-in user", () => {
    expect(isOwnOrder({ createdBy: ME }, ME)).toBe(true);
    expect(isOwnOrder({ createdBy: ME }, null)).toBe(false);
    expect(isOwnOrder({ createdBy: null }, ME)).toBe(false);
  });

  it("fulfilment starts at approval", () => {
    expect(ORDER_STATUSES.filter(isInFulfilment)).toEqual(["approved", "picking", "packed", "shipped", "completed"]);
  });
});

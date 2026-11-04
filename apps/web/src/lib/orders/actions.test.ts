import { describe, expect, it } from "vitest";
import { ROLES, type Role } from "@/lib/navigation";
import { availableActions, isDraftEditable, isInFulfilment } from "./actions";
import { ORDER_STATUSES } from "./labels";
import { orderPermissions } from "./permissions";

const actionsOf = (role: Role, status: string, fulfillmentStatus = "none") =>
  availableActions({ status, fulfillmentStatus }, orderPermissions(role)).map((a) => a.action);

describe("availableActions — state machine × role (inventory-and-orders §3.1, roles-permissions §3.5)", () => {
  it.each(["owner", "admin", "manager"] as const)("%s gets every order-desk transition the state allows", (role) => {
    expect(actionsOf(role, "draft")).toEqual(["submit", "cancel"]);
    expect(actionsOf(role, "pending_approval")).toEqual(["approve", "reject", "withdraw", "cancel"]);
    expect(actionsOf(role, "approved")).toEqual(["cancel"]);
    expect(actionsOf(role, "picking")).toEqual(["cancel"]);
    expect(actionsOf(role, "packed")).toEqual(["cancel"]);
    expect(actionsOf(role, "shipped")).toEqual([]);
    expect(actionsOf(role, "completed")).toEqual([]);
    expect(actionsOf(role, "cancelled")).toEqual([]);
  });

  it("no cancel after a partial shipment (I9: only short-close, which is a fulfilment action)", () => {
    expect(actionsOf("owner", "picking", "partial")).toEqual([]);
    expect(actionsOf("manager", "packed", "partial")).toEqual([]);
  });

  it("sales submits and withdraws, but approve/reject/cancel stay fail-closed (K)", () => {
    expect(actionsOf("sales", "draft")).toEqual(["submit"]);
    expect(actionsOf("sales", "pending_approval")).toEqual(["withdraw"]);
    expect(actionsOf("sales", "approved")).toEqual([]);
  });

  it.each(["viewer", "warehouse_staff"] as const)("%s gets no actions in any state", (role) => {
    for (const status of ORDER_STATUSES) expect(actionsOf(role, status)).toEqual([]);
  });

  it("never offers fulfilment transitions (picking, packing, shipping, short-close move to Warehouse)", () => {
    const all = ROLES.flatMap((role) => ORDER_STATUSES.flatMap((s) => [...actionsOf(role, s), ...actionsOf(role, s, "partial")]));
    expect(new Set(all)).toEqual(new Set(["submit", "approve", "reject", "withdraw", "cancel"]));
  });

  it("reject and cancel ask for a reason first", () => {
    const actions = availableActions({ status: "pending_approval", fulfillmentStatus: "none" }, orderPermissions("owner"));
    expect(actions.filter((a) => a.needsReason).map((a) => a.action)).toEqual(["reject", "cancel"]);
  });
});

describe("isDraftEditable / isInFulfilment", () => {
  it("only drafts are editable, and only with orders.edit.draft", () => {
    expect(isDraftEditable({ status: "draft" }, orderPermissions("manager"))).toBe(true);
    expect(isDraftEditable({ status: "draft" }, orderPermissions("sales"))).toBe(false);
    expect(isDraftEditable({ status: "pending_approval" }, orderPermissions("owner"))).toBe(false);
  });

  it("fulfilment starts at approval", () => {
    expect(ORDER_STATUSES.filter(isInFulfilment)).toEqual(["approved", "picking", "packed", "shipped", "completed"]);
  });
});

import type { OrderTransition } from "./api";
import type { OrderPermissions } from "./permissions";

export type OrderAction = {
  action: OrderTransition;
  label: string;
  variant: "primary" | "secondary" | "danger";
  /** Opens a reason dialog first (reject, cancel: the API requires a reason). */
  needsReason: boolean;
};

const ACTIONS: Record<OrderTransition, OrderAction> = {
  submit: { action: "submit", label: "Onaya gönder", variant: "primary", needsReason: false },
  approve: { action: "approve", label: "Onayla ve stok ayır", variant: "primary", needsReason: false },
  withdraw: { action: "withdraw", label: "Onaydan geri çek", variant: "secondary", needsReason: false },
  reject: { action: "reject", label: "Reddet", variant: "secondary", needsReason: true },
  cancel: { action: "cancel", label: "Siparişi iptal et", variant: "danger", needsReason: true },
};

export function orderAction(action: OrderTransition): OrderAction {
  return ACTIONS[action];
}

/** The order fields the record-level checks look at. */
export type OrderRecord = { status: string; fulfillmentStatus: string; createdBy?: string | null };

/** True when the signed-in user created the order (own-records grants). Unknown creator or user → false. */
export function isOwnOrder(order: Pick<OrderRecord, "createdBy">, userId: string | null): boolean {
  return userId !== null && order.createdBy != null && order.createdBy === userId;
}

/** orders.cancel for this record: unconditional, or the caller's own draft / pending-approval order (K¹¹). */
function mayCancel(order: OrderRecord, permissions: OrderPermissions, userId: string | null): boolean {
  if (!permissions.cancel) return false;
  if (!permissions.cancelOwnOpenOnly) return true;
  return isOwnOrder(order, userId) && (order.status === "draft" || order.status === "pending_approval");
}

/**
 * Which order-desk transitions apply to an order in this state for this user — a mirror of OrderStateMachine.Next
 * (inventory-and-orders §3.1) restricted to what this screen offers. The API decides; a stale view gets
 * 409 order.invalid_transition and a record the user may not touch gets 403. Fulfilment is not offered here.
 */
export function availableActions(order: OrderRecord, permissions: OrderPermissions, userId: string | null = null): OrderAction[] {
  const result: OrderTransition[] = [];
  const cancel = mayCancel(order, permissions, userId);
  switch (order.status) {
    case "draft":
      if (permissions.submit) result.push("submit");
      if (cancel) result.push("cancel");
      break;
    case "pending_approval":
      if (permissions.approve) result.push("approve", "reject");
      if (permissions.submit) result.push("withdraw");
      if (cancel) result.push("cancel");
      break;
    case "approved":
    case "picking":
    case "packed":
      // After a partial shipment only short-close is possible (I9) — a fulfilment action, not offered here.
      if (cancel && order.fulfillmentStatus === "none") result.push("cancel");
      break;
    default:
      break;
  }
  return result.map((a) => ACTIONS[a]);
}

/**
 * Lines and header fields change only in draft (OrderStateMachine.IsEditable), and only for users who may edit this
 * draft: everyone with orders.edit.draft, or — for own-records grants — the user who created it.
 */
export function isDraftEditable(order: Pick<OrderRecord, "status" | "createdBy">, permissions: OrderPermissions, userId: string | null = null): boolean {
  if (order.status !== "draft" || !permissions.editDraft) return false;
  return !permissions.editDraftOwnOnly || isOwnOrder(order, userId);
}

/** States in which the order holds stock reservations / has entered fulfilment. */
export function isInFulfilment(status: string): boolean {
  return status === "approved" || status === "picking" || status === "packed" || status === "shipped" || status === "completed";
}

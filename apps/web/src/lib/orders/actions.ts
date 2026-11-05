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

/**
 * Which order-desk transitions apply to an order in this state for this role — a mirror of OrderStateMachine.Next
 * (inventory-and-orders §3.1) restricted to what this screen offers. The API decides; a stale view gets
 * 409 order.invalid_transition. Fulfilment (picking, packing, shipping, short-close) is not offered here.
 */
export function availableActions(order: { status: string; fulfillmentStatus: string }, permissions: OrderPermissions): OrderAction[] {
  const result: OrderTransition[] = [];
  switch (order.status) {
    case "draft":
      if (permissions.submit) result.push("submit");
      if (permissions.cancel) result.push("cancel");
      break;
    case "pending_approval":
      if (permissions.approve) result.push("approve", "reject");
      if (permissions.submit) result.push("withdraw");
      if (permissions.cancel) result.push("cancel");
      break;
    case "approved":
    case "picking":
    case "packed":
      // After a partial shipment only short-close is possible (I9) — a fulfilment action, not offered here.
      if (permissions.cancel && order.fulfillmentStatus === "none") result.push("cancel");
      break;
    default:
      break;
  }
  return result.map((a) => ACTIONS[a]);
}

/** Lines and header fields change only in draft (OrderStateMachine.IsEditable). */
export function isDraftEditable(order: { status: string }, permissions: OrderPermissions): boolean {
  return order.status === "draft" && permissions.editDraft;
}

/** States in which the order holds stock reservations / has entered fulfilment. */
export function isInFulfilment(status: string): boolean {
  return status === "approved" || status === "picking" || status === "packed" || status === "shipped" || status === "completed";
}

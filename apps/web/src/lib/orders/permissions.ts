import { CONDITIONS, type Access } from "@/lib/session/access";

export type OrderPermissions = {
  /** orders.read — list and detail (any scope; a restricted reader gets only the orders the API lets through). */
  read: boolean;
  /** orders.read is limited to orders behind the caller's tasks, without prices (warehouse staff, K⁸). */
  readTaskLinkedOnly: boolean;
  /** orders.create — manual draft. */
  create: boolean;
  /** orders.edit.draft — change a draft's header and lines (any scope). */
  editDraft: boolean;
  /** orders.edit.draft only for drafts the caller created (sales, O). */
  editDraftOwnOnly: boolean;
  /** orders.submit — send for approval, withdraw. */
  submit: boolean;
  /** orders.approve — approve (reserve stock), reject, and close the rest after a partial shipment (close-remaining). */
  approve: boolean;
  /** orders.edit.approved — complete a shipped order (shipped → completed). */
  complete: boolean;
  /** orders.cancel — cancel with a reason (any scope). */
  cancel: boolean;
  /** orders.cancel only for the caller's own draft / pending-approval orders (sales, K¹¹). */
  cancelOwnOpenOnly: boolean;
  /** orders.price.override unconditionally: manual unit price and discounts. */
  overridePrice: boolean;
  /** Line discounts may be entered: full override, or the sales discount limit (K¹⁰; the API enforces the limit). */
  discount: boolean;
  /** Discounts are capped by the tenant's sales limit and manual unit prices are not allowed. */
  discountLimited: boolean;
  /** The cap of a limited discount grant in percent (/me `parameters.maxDiscountPercent`, #111); null when unknown or uncapped. */
  maxDiscountPercent: number | null;
};

/**
 * Order-desk actions from the effective permissions (GET /me). Conditional / own grants show the action; the record
 * checks below use the order's `createdBy` where the condition is known, and the API decides in every case.
 */
export function orderPermissions(access: Access): OrderPermissions {
  const editDraft = access.canSome("orders.edit.draft");
  const cancel = access.canSome("orders.cancel");
  const overridePrice = access.can("orders.price.override");
  const discount = access.canSome("orders.price.override");
  return {
    read: access.canSome("orders.read"),
    readTaskLinkedOnly: access.hasCondition("orders.read", CONDITIONS.ordersTaskLinked),
    create: access.can("orders.create"),
    editDraft,
    editDraftOwnOnly: editDraft && (access.scope("orders.edit.draft") === "own" || access.hasCondition("orders.edit.draft", CONDITIONS.ordersOwn)),
    submit: access.can("orders.submit"),
    approve: access.canSome("orders.approve"),
    complete: access.canSome("orders.edit.approved"),
    cancel,
    cancelOwnOpenOnly: access.hasCondition("orders.cancel", CONDITIONS.ordersOwnDraftOrPending),
    overridePrice,
    discount,
    discountLimited: discount && !overridePrice,
    maxDiscountPercent: discount && !overridePrice ? access.maxDiscountPercent("orders.price.override") : null,
  };
}

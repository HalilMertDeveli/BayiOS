import type { Role } from "@/lib/navigation";

export type OrderPermissions = {
  /** orders.read — list and detail. warehouse_staff has it only conditionally (K⁸), which the API does not grant yet. */
  read: boolean;
  /** orders.create — manual draft. */
  create: boolean;
  /** orders.edit.draft — change a draft's header and lines. sales has it for own records only (O): fail-closed (#69). */
  editDraft: boolean;
  /** orders.submit — send for approval, withdraw. */
  submit: boolean;
  /** orders.approve — approve (reserve stock) and reject. sales is conditional (K⁹): fail-closed. */
  approve: boolean;
  /** orders.cancel — cancel with a reason. sales is conditional (K¹¹): fail-closed. */
  cancel: boolean;
  /** orders.price.override — manual unit price and discounts. sales is conditional (K¹⁰): fail-closed. */
  overridePrice: boolean;
};

const MANAGERS: readonly Role[] = ["owner", "admin", "manager"];

/**
 * roles-permissions.md §3.5 as the API enforces it (RolePermissions.IsGranted: only unconditional ✓ passes; K and O
 * stay fail-closed until tenant rules / record ownership are evaluated). /me returns the role only, so the UI derives
 * what to show from it. Visibility only: the API checks every permission again.
 */
export function orderPermissions(role: Role | null): OrderPermissions {
  const manager = role !== null && MANAGERS.includes(role);
  const desk = manager || role === "sales";
  return {
    read: desk || role === "viewer",
    create: desk,
    editDraft: manager,
    submit: desk,
    approve: manager,
    cancel: manager,
    overridePrice: manager,
  };
}

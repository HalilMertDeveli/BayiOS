import type { Access } from "@/lib/session/access";

export type InventoryPermissions = {
  /** inventory.read — balances, warehouses and locations. */
  read: boolean;
  /**
   * inventory.movements.read — movement history. A restricted grant (warehouse staff: own movements and the task
   * context) still shows the history; the API returns only the rows the caller may see.
   */
  movements: boolean;
  /** True when the movement history is filtered to the caller's own records / task context. */
  movementsOwnOnly: boolean;
  /** inventory.receive — goods receipt. */
  receive: boolean;
  /** inventory.adjust — manual stock adjustment. */
  adjust: boolean;
  /** catalog.write — set and remove minimum stock thresholds (#137; read with inventory.read). */
  manageThresholds: boolean;
  /** warehouses.manage — create/edit warehouses and locations. */
  manageWarehouses: boolean;
};

/** Inventory actions from the effective permissions (GET /me). Visibility only: the API checks every request again. */
export function inventoryPermissions(access: Access): InventoryPermissions {
  return {
    read: access.canSome("inventory.read"),
    movements: access.canSome("inventory.movements.read"),
    movementsOwnOnly: access.canSome("inventory.movements.read") && !access.can("inventory.movements.read"),
    receive: access.can("inventory.receive"),
    adjust: access.can("inventory.adjust"),
    manageThresholds: access.can("catalog.write"),
    manageWarehouses: access.can("warehouses.manage"),
  };
}

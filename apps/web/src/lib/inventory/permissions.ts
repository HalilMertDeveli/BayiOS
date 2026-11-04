import type { Role } from "@/lib/navigation";

export type InventoryPermissions = {
  /** inventory.read — balances, warehouses and locations (every role). */
  read: boolean;
  /** inventory.movements.read — movement history. warehouse_staff has it only for own records (O), which the API does not grant at endpoint level yet. */
  movements: boolean;
  /** inventory.receive — goods receipt. */
  receive: boolean;
  /** inventory.adjust — manual stock adjustment. */
  adjust: boolean;
  /** warehouses.manage — create/edit warehouses and locations. */
  manageWarehouses: boolean;
};

const MANAGERS: readonly Role[] = ["owner", "admin", "manager"];

/**
 * roles-permissions.md §3.4 (as enforced by the API's RolePermissions: only unconditional ✓ grants pass).
 * /me returns the role only, so the UI derives what to show from it. Visibility only: the API checks again.
 */
export function inventoryPermissions(role: Role | null): InventoryPermissions {
  const manager = role !== null && MANAGERS.includes(role);
  return {
    read: role !== null,
    movements: manager || role === "viewer",
    receive: manager || role === "warehouse_staff",
    adjust: manager,
    manageWarehouses: manager,
  };
}

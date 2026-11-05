import type { Role } from "@/lib/navigation";

export type FulfilmentPermissions = {
  /** tasks.read.all — GET /picking-tasks (every task; list only — the detail needs tasks.read.own). */
  readAll: boolean;
  /** tasks.read.own — "my tasks" + pool, task detail, shipments. */
  readOwn: boolean;
  /** tasks.assign — supervisor: assign/unassign any task and act on every task. */
  assign: boolean;
  /** tasks.claim — take a pool task. owner/admin do not have it (they assign instead). */
  claim: boolean;
  /** tasks.pick — start, scan, complete. */
  pick: boolean;
  /** tasks.pack — pack check. */
  pack: boolean;
  /** tasks.report_shortage — picking and packing shortages. */
  reportShortage: boolean;
  /** orders.ship — confirm a shipment (staff: only for their own task's shipment; the API checks). */
  ship: boolean;
};

const MANAGERS: readonly Role[] = ["owner", "admin", "manager"];

/**
 * roles-permissions.md §3.6 as the API enforces it (RolePermissions: only unconditional ✓ passes; sales' K¹² on
 * tasks.read.all stays fail-closed). /me returns the role only today; this is the single place to switch to the
 * effective permissions once /me carries them (#69/#86). Visibility only: the API checks every request again.
 */
export function fulfilmentPermissions(role: Role | null): FulfilmentPermissions {
  const manager = role !== null && MANAGERS.includes(role);
  const staff = role === "warehouse_staff";
  const worker = manager || staff;
  return {
    readAll: manager || role === "viewer",
    readOwn: worker,
    assign: manager,
    claim: role === "manager" || staff,
    pick: worker,
    pack: worker,
    reportShortage: worker,
    ship: worker,
  };
}

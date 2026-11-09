import type { Access } from "@/lib/session/access";

export type FulfilmentPermissions = {
  /**
   * tasks.read.all — GET /picking-tasks ("Tüm görevler"). A conditional grant (sales: tasks of its own orders) shows the
   * tab too; the API returns only the tasks the caller may see.
   */
  readAll: boolean;
  /** tasks.read.own — "my tasks" + pool. */
  readOwn: boolean;
  /** Task detail and shipments: the API accepts tasks.read.own or tasks.read.all (x-permission-any-of). */
  readDetail: boolean;
  /** tasks.assign — supervisor: assign/unassign any task and act on every task. */
  assign: boolean;
  /** tasks.claim — take a pool task. */
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

/** Warehouse task actions from the effective permissions (GET /me). Visibility only: the API checks every request. */
export function fulfilmentPermissions(access: Access): FulfilmentPermissions {
  const readAll = access.canSome("tasks.read.all");
  const readOwn = access.canSome("tasks.read.own");
  return {
    readAll,
    readOwn,
    readDetail: readAll || readOwn,
    assign: access.can("tasks.assign"),
    claim: access.can("tasks.claim"),
    pick: access.can("tasks.pick"),
    pack: access.can("tasks.pack"),
    reportShortage: access.can("tasks.report_shortage"),
    ship: access.canSome("orders.ship"),
  };
}

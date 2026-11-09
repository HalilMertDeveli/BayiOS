import type { Access } from "@/lib/session/access";

export type TransferPermissions = {
  /** inventory.transfer or inventory.transfer.receive — list and detail. */
  read: boolean;
  /** inventory.transfer — create, dispatch, close (discrepancy) and cancel. */
  manage: boolean;
  /** inventory.transfer.receive — receive at the destination (warehouse staff included). */
  receive: boolean;
  /**
   * inventory.relocate (#98) — instant shelf-to-shelf move inside one warehouse (POST /inventory/relocations); warehouse
   * staff included, unlike inter-warehouse transfers.
   */
  relocate: boolean;
};

export type CountPermissions = {
  /** inventory.count.manage or inventory.count.perform — list and detail. */
  read: boolean;
  /** inventory.count.manage — start, recount, apply, cancel; sees system quantities of blind counts. */
  manage: boolean;
  /** inventory.count.perform — enter counted quantities and submit. */
  perform: boolean;
};

/** Visibility only: the API checks every request again (roles-permissions §3.4). */
export function transferPermissions(access: Access): TransferPermissions {
  const manage = access.can("inventory.transfer");
  const receive = access.can("inventory.transfer.receive");
  return {
    read: access.canSome("inventory.transfer") || access.canSome("inventory.transfer.receive"),
    manage,
    receive,
    relocate: access.can("inventory.relocate"),
  };
}

export function countPermissions(access: Access): CountPermissions {
  return {
    read: access.canSome("inventory.count.manage") || access.canSome("inventory.count.perform"),
    manage: access.can("inventory.count.manage"),
    perform: access.can("inventory.count.perform"),
  };
}

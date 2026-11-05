import type { Access } from "@/lib/session/access";

export type CustomerPermissions = {
  /** customers.read — list, detail, addresses, dealer groups. */
  read: boolean;
  /** customers.write — create/edit customers and addresses, archive. */
  write: boolean;
  /** customers.groups.manage — create/edit/archive dealer groups. */
  manageGroups: boolean;
  /** pricelists.assign — dealer group / customer price list assignment. */
  assignPriceLists: boolean;
  /** POST /customers/{id}/anonymize — the API requires tenant.settings.manage. */
  anonymize: boolean;
};

/** Customer actions from the effective permissions (GET /me). Visibility only: the API checks every request again. */
export function customerPermissions(access: Access): CustomerPermissions {
  return {
    read: access.canSome("customers.read"),
    write: access.can("customers.write"),
    manageGroups: access.can("customers.groups.manage"),
    assignPriceLists: access.can("pricelists.assign"),
    anonymize: access.can("tenant.settings.manage"),
  };
}

import type { Role } from "@/lib/navigation";

export type CustomerPermissions = {
  /** customers.read — list, detail, addresses, dealer groups. */
  read: boolean;
  /** customers.write — create/edit customers and addresses, archive. */
  write: boolean;
  /** customers.groups.manage — create/edit/archive dealer groups. */
  manageGroups: boolean;
  /** pricelists.assign — dealer group / customer price list assignment. */
  assignPriceLists: boolean;
  /** POST /customers/{id}/anonymize — the API requires tenant.settings.manage (owner/admin). */
  anonymize: boolean;
};

/**
 * roles-permissions.md §3.2 and §3.5. /me returns the role only, so the UI derives what to show from it.
 * Visibility only: the API checks every permission again on each request.
 */
export function customerPermissions(role: Role | null): CustomerPermissions {
  const ownerAdmin = role === "owner" || role === "admin";
  const managers = ownerAdmin || role === "manager";
  return {
    read: managers || role === "sales" || role === "viewer",
    write: managers || role === "sales",
    manageGroups: managers,
    assignPriceLists: managers,
    anonymize: ownerAdmin,
  };
}

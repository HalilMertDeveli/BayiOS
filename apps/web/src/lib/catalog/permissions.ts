import type { Role } from "@/lib/navigation";

export type CatalogPermissions = {
  /** catalog.write — create, edit and archive products, variants and brands. */
  write: boolean;
  /** catalog.fields.manage — custom fields, categories and units. */
  manageFields: boolean;
};

/**
 * roles-permissions.md §3.2. /me returns the role only, so the UI derives what to show from it.
 * Visibility only: the API checks every permission again on each request.
 */
export function catalogPermissions(role: Role | null): CatalogPermissions {
  return {
    write: role === "owner" || role === "admin" || role === "manager",
    manageFields: role === "owner" || role === "admin",
  };
}

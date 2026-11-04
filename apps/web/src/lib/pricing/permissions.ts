import type { Role } from "@/lib/navigation";

export type PricingPermissions = {
  /** prices.read.sell — price lists, items and the price quote. */
  readSell: boolean;
  /**
   * prices.read.cost — purchase lists. manager/viewer hold it only conditionally (tenant rule, roles-permissions §4)
   * and the API grants only unconditional ✓, so the UI follows: owner and admin.
   */
  readCost: boolean;
  /** prices.write — create/edit/archive price lists and their items. */
  write: boolean;
  /** pricelists.assign — assign a list to a customer or dealer group. */
  assign: boolean;
};

/**
 * roles-permissions.md §3.2. /me returns the role only, so the UI derives what to show from it.
 * Visibility only: the API checks every permission again on each request.
 */
export function pricingPermissions(role: Role | null): PricingPermissions {
  const ownerAdmin = role === "owner" || role === "admin";
  return {
    readSell: role !== null && role !== "warehouse_staff",
    readCost: ownerAdmin,
    write: ownerAdmin,
    assign: ownerAdmin || role === "manager",
  };
}

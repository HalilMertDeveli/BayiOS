import type { Access } from "@/lib/session/access";

export type PricingPermissions = {
  /** prices.read.sell — price lists, items and the price quote. */
  readSell: boolean;
  /**
   * prices.read.cost — purchase lists. For manager/viewer it depends on a tenant rule; the API reports it as granted
   * (scope all) only when the rule is on.
   */
  readCost: boolean;
  /** prices.write — create/edit/archive price lists and their items. */
  write: boolean;
  /** pricelists.assign — assign a list to a customer or dealer group. */
  assign: boolean;
};

/** Pricing actions from the effective permissions (GET /me). Visibility only: the API checks every request again. */
export function pricingPermissions(access: Access): PricingPermissions {
  return {
    readSell: access.canSome("prices.read.sell"),
    readCost: access.can("prices.read.cost"),
    write: access.can("prices.write"),
    assign: access.can("pricelists.assign"),
  };
}

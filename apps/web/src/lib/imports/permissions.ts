import type { Access } from "@/lib/session/access";
import type { ImportType } from "./api";

export type ImportPermissions = {
  /** imports.run — every import endpoint. */
  run: boolean;
  /** imports.run.stock only (operations manager): stock imports are not built yet, so the screen is locked for them. */
  stockOnly: boolean;
  /** Product imports also need catalog.write. */
  products: boolean;
  /** Price list imports — and a product import that maps the price column — also need prices.write. */
  prices: boolean;
  /** imports.rollback (plus the run permissions of the import's type). */
  rollback: boolean;
};

/**
 * What the import screens may offer, from the effective permissions (GET /me). Visibility only: the API checks
 * imports.run + catalog.write / prices.write (ImportPermissions.RunDenied) and imports.rollback on every call.
 */
export function importPermissions(access: Access): ImportPermissions {
  const run = access.can("imports.run");
  return {
    run,
    stockOnly: !run && access.canSome("imports.run.stock"),
    products: run && access.can("catalog.write"),
    prices: run && access.can("prices.write"),
    rollback: run && access.can("imports.rollback"),
  };
}

export function canRunType(permissions: ImportPermissions, type: string): boolean {
  if (type === "products") return permissions.products;
  if (type === "prices") return permissions.prices;
  return false;
}

/** Types the caller can start (the API supports products and prices). */
export function runnableTypes(permissions: ImportPermissions): ImportType[] {
  return (["products", "prices"] as const).filter((t) => canRunType(permissions, t));
}

/** Rollback re-checks the type's run permissions and prices.write when the import wrote a price list. */
export function canRollbackJob(permissions: ImportPermissions, job: { type: string; priceListId: string | null }): boolean {
  return permissions.rollback && canRunType(permissions, job.type) && (job.priceListId === null || permissions.prices);
}

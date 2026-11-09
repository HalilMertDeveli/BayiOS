import type { components } from "@/lib/api/schema";

/**
 * Effective permissions of the active membership, exactly as `GET /api/v1/me` (with X-Tenant-Id) returns them in
 * `activeTenant.permissions`. The web keeps no role → permission table: every "may I show this?" decision goes through
 * this module. Visibility only — the API checks every permission (and every per-record condition) again.
 */
export type PermissionGrant = components["schemas"]["PermissionGrantResponse"];
export type PermissionScope = PermissionGrant["scope"];

/** Permission ids the web asks about (docs/product/roles-permissions.md §3). Ids only — which role holds them is the API's business. */
export type Permission =
  | "tenant.read"
  | "tenant.settings.manage"
  | "members.read"
  | "members.invite"
  | "members.manage"
  | "ownership.transfer"
  | "billing.read"
  | "billing.manage"
  | "catalog.read"
  | "catalog.write"
  | "catalog.barcode.assign"
  | "catalog.fields.manage"
  | "prices.read.sell"
  | "prices.read.cost"
  | "prices.write"
  | "pricelists.assign"
  | "imports.run"
  | "imports.run.stock"
  | "imports.rollback"
  | "reports.read"
  | "reports.export"
  | "inventory.read"
  | "inventory.movements.read"
  | "inventory.receive"
  | "inventory.adjust"
  | "inventory.transfer"
  | "inventory.transfer.receive"
  | "inventory.count.manage"
  | "inventory.count.perform"
  | "warehouses.manage"
  | "orders.read"
  | "orders.create"
  | "orders.edit.draft"
  | "orders.submit"
  | "orders.approve"
  | "orders.edit.approved"
  | "orders.price.override"
  | "orders.cancel"
  | "orders.ship"
  | "customers.read"
  | "customers.write"
  | "customers.groups.manage"
  | "tasks.read.all"
  | "tasks.read.own"
  | "tasks.assign"
  | "tasks.claim"
  | "tasks.pick"
  | "tasks.pack"
  | "tasks.report_shortage"
  | "whatsapp.parse";

/**
 * Condition codes the UI refines its display with (ConditionalGrants on the API). An unknown code is never a reason to
 * hide something: the UI then shows the action and lets the API decide per record.
 */
export const CONDITIONS = {
  /** orders.edit.draft (O): drafts the caller created. */
  ordersOwn: "orders.own",
  /** orders.cancel (K¹¹): the caller's own draft / pending-approval orders. */
  ordersOwnDraftOrPending: "orders.own_draft_or_pending",
  /** orders.price.override (K¹⁰): discounts within the tenant's sales limit, no manual unit price. */
  discountWithinLimit: "orders.discount_within_limit",
  /** orders.read (K⁸): orders behind the caller's tasks, prices hidden. */
  ordersTaskLinked: "orders.task_linked_without_prices",
  /** inventory.movements.read (K⁵): own movements plus the task context. */
  movementsOwn: "movements.own_or_task_context",
  /** tasks.read.all (K¹²): tasks of the caller's own orders. */
  tasksOwnOrders: "tasks.own_orders",
  /** reports.read / reports.export (K⁴): order and customer reports only (sales). */
  reportsOrdersOnly: "reports.orders_and_customers_only",
} as const;

export type Access = {
  /** True when the permission is granted unconditionally (scope `all`). */
  can(permission: Permission): boolean;
  /**
   * True for any grant — `all`, `conditional` or `own`. Use it to *show* an action or a screen whose records the API
   * filters or checks one by one (e.g. sales editing its own drafts); handle 403/404 gracefully.
   */
  canSome(permission: Permission): boolean;
  /** The grant's scope, or null when the permission is not granted. */
  scope(permission: Permission): PermissionScope | null;
  /** The condition code of a conditional / own grant, null otherwise. */
  condition(permission: Permission): string | null;
  /** True when the permission is granted only under the given condition code. */
  hasCondition(permission: Permission, code: string): boolean;
  /**
   * The grant's limit parameters (#111), e.g. `maxDiscountPercent` of the sales discount limit (K¹⁰) as a number; null
   * when the grant carries none or the permission is not granted.
   */
  maxDiscountPercent(permission: Permission): number | null;
  readonly grants: readonly PermissionGrant[];
};

export function createAccess(grants: readonly PermissionGrant[] | null | undefined): Access {
  const list = grants ?? [];
  const byPermission = new Map(list.map((g) => [g.permission, g] as const));
  const scope = (permission: Permission) => byPermission.get(permission)?.scope ?? null;
  return {
    can: (permission) => scope(permission) === "all",
    canSome: (permission) => scope(permission) !== null,
    scope,
    condition: (permission) => byPermission.get(permission)?.condition ?? null,
    hasCondition: (permission, code) => {
      const grant = byPermission.get(permission);
      return grant !== undefined && grant.scope !== "all" && grant.condition === code;
    },
    maxDiscountPercent: (permission) => {
      const raw = byPermission.get(permission)?.parameters?.maxDiscountPercent;
      const value = typeof raw === "number" ? raw : typeof raw === "string" && raw.trim() ? Number(raw) : NaN;
      return Number.isFinite(value) ? value : null;
    },
    grants: list,
  };
}

/** No active tenant (or not loaded yet): nothing is shown. */
export const NO_ACCESS: Access = createAccess([]);

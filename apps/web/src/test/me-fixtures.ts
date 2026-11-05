/**
 * MOCK `GET /api/v1/me` PERMISSIONS — TEST FIXTURES ONLY (Vitest + Playwright mocks). Never imported by app code.
 *
 * The web app has no role → permission table: it renders whatever `activeTenant.permissions` the API returns. Tests
 * still need realistic /me bodies per role, so this file reproduces what the API answers for each role under the given
 * tenant rules (services/api RolePermissions + ConditionalGrants, roles-permissions.md §3). It is synthetic data for
 * the mocks; when the API matrix changes, the API's own tests are the source of truth and these fixtures follow.
 * No imports on purpose: Playwright loads this file directly.
 */

export type FixtureRole = "owner" | "admin" | "manager" | "sales" | "warehouse_staff" | "viewer";
export type FixtureGrant = {
  permission: string;
  scope: "all" | "conditional" | "own";
  condition: string | null;
  parameters: { maxDiscountPercent: number | null } | null;
};

/** Tenant rules (roles-permissions §4) that switch conditional grants on or off. Defaults = the API's defaults. */
export type FixtureRules = {
  salesCanApproveOrders: boolean;
  /** > 0 gives sales a conditional orders.price.override (discounts within the limit). */
  salesMaxDiscountPercent: number;
  showCostToManager: boolean;
  showCostToViewer: boolean;
  staffCanAssignBarcode: boolean;
  staffCanApproveReceipt: boolean;
};

export const DEFAULT_RULES: FixtureRules = {
  salesCanApproveOrders: true,
  salesMaxDiscountPercent: 0,
  showCostToManager: false,
  showCostToViewer: false,
  staffCanAssignBarcode: false,
  staffCanApproveReceipt: false,
};

const ROLE_ORDER: readonly FixtureRole[] = ["owner", "admin", "manager", "sales", "warehouse_staff", "viewer"];

// Columns: owner, admin, manager, sales, warehouse_staff, viewer. Y = all, - = none, K = conditional, O = own.
const MATRIX: readonly (readonly [string, string])[] = [
  ["tenant.read", "YYYYYY"],
  ["tenant.settings.manage", "YY----"],
  ["tenant.delete.request", "Y-----"],
  ["members.read", "YYY---"],
  ["members.invite", "YY----"],
  ["members.manage", "YK----"],
  ["ownership.transfer", "Y-----"],
  ["billing.read", "YY----"],
  ["billing.manage", "Y-----"],
  ["audit.read", "YY----"],
  ["security.manage", "YY----"],
  ["catalog.read", "YYYYYY"],
  ["catalog.write", "YYY---"],
  ["catalog.barcode.assign", "YYY-K-"],
  ["catalog.fields.manage", "YY----"],
  ["prices.read.sell", "YYYY-Y"],
  ["prices.read.cost", "YYK--K"],
  ["prices.write", "YY----"],
  ["pricelists.assign", "YYY---"],
  ["imports.run", "YY----"],
  ["imports.run.stock", "YYY---"],
  ["imports.rollback", "YY----"],
  ["reports.read", "YYYK-Y"],
  ["reports.export", "YYYK-Y"],
  ["data.export.full", "Y-----"],
  ["inventory.read", "YYYYYY"],
  ["inventory.movements.read", "YYY-OY"],
  ["inventory.receive", "YYY-Y-"],
  ["inventory.receive.approve", "YYY-K-"],
  ["inventory.adjust", "YYY---"],
  ["inventory.count.manage", "YYY---"],
  ["inventory.count.perform", "YYY-Y-"],
  ["inventory.transfer", "YYY---"],
  ["inventory.transfer.receive", "YYY-Y-"],
  ["inventory.returns", "YYYKY-"],
  ["warehouses.manage", "YYY---"],
  ["orders.read", "YYYYKY"],
  ["orders.create", "YYYY--"],
  ["orders.edit.draft", "YYYO--"],
  ["orders.submit", "YYYY--"],
  ["orders.approve", "YYYK--"],
  ["orders.edit.approved", "YYY---"],
  ["orders.price.override", "YYYK--"],
  ["orders.cancel", "YYYK--"],
  ["orders.ship", "YYY-Y-"],
  ["whatsapp.parse", "YYYY--"],
  ["customers.read", "YYYY-Y"],
  ["customers.write", "YYYY--"],
  ["customers.groups.manage", "YYY---"],
  ["tasks.read.all", "YYYK-Y"],
  ["tasks.read.own", "YYY-Y-"],
  ["tasks.assign", "YYY---"],
  ["tasks.claim", "--Y-Y-"],
  ["tasks.pick", "YYY-Y-"],
  ["tasks.pack", "YYY-Y-"],
  ["tasks.report_shortage", "YYY-Y-"],
];

type Condition = { code: string; recordLevel: boolean; rule?: (r: FixtureRules) => boolean };

/** K / O cells: condition code, whether the API reports it as a per-record grant, and the tenant rule gating it. */
const CONDITIONS: Record<string, Condition> = {
  "admin|members.manage": { code: "members.below_admin", recordLevel: true },
  "warehouse_staff|catalog.barcode.assign": { code: "tenant_rule.staff_can_assign_barcode", recordLevel: false, rule: (r) => r.staffCanAssignBarcode },
  "manager|prices.read.cost": { code: "tenant_rule.show_cost_to_manager", recordLevel: false, rule: (r) => r.showCostToManager },
  "viewer|prices.read.cost": { code: "tenant_rule.show_cost_to_viewer", recordLevel: false, rule: (r) => r.showCostToViewer },
  "sales|reports.read": { code: "reports.orders_and_customers_only", recordLevel: true },
  "sales|reports.export": { code: "reports.orders_and_customers_only", recordLevel: true },
  "warehouse_staff|inventory.movements.read": { code: "movements.own_or_task_context", recordLevel: true },
  "warehouse_staff|inventory.receive.approve": { code: "tenant_rule.staff_can_approve_receipt", recordLevel: false, rule: (r) => r.staffCanApproveReceipt },
  "sales|inventory.returns": { code: "returns.draft_only", recordLevel: true },
  "warehouse_staff|orders.read": { code: "orders.task_linked_without_prices", recordLevel: true },
  "sales|orders.edit.draft": { code: "orders.own", recordLevel: true },
  "sales|orders.approve": { code: "tenant_rule.sales_can_approve_orders", recordLevel: false, rule: (r) => r.salesCanApproveOrders },
  "sales|orders.price.override": { code: "orders.discount_within_limit", recordLevel: true, rule: (r) => r.salesMaxDiscountPercent > 0 },
  "sales|orders.cancel": { code: "orders.own_draft_or_pending", recordLevel: true },
  "sales|tasks.read.all": { code: "tasks.own_orders", recordLevel: true },
};

/** `activeTenant.permissions` as the API returns it for `role` under `rules`. */
export function grantsFor(role: FixtureRole, rules: Partial<FixtureRules> = {}): FixtureGrant[] {
  const effective = { ...DEFAULT_RULES, ...rules };
  const column = ROLE_ORDER.indexOf(role);
  const result: FixtureGrant[] = [];
  for (const [permission, row] of MATRIX) {
    const cell = row[column];
    if (cell === "Y") {
      result.push({ permission, scope: "all", condition: null, parameters: null });
      continue;
    }
    if (cell === "-") continue;
    const condition = CONDITIONS[`${role}|${permission}`];
    if (!condition || (condition.rule && !condition.rule(effective))) continue;
    if (!condition.recordLevel) {
      result.push({ permission, scope: "all", condition: null, parameters: null });
      continue;
    }
    // The API sends the discount limit with footnote 10 (#111); every other condition has no parameters.
    const parameters = condition.code === "orders.discount_within_limit" ? { maxDiscountPercent: effective.salesMaxDiscountPercent } : null;
    result.push({ permission, scope: cell === "O" ? "own" : "conditional", condition: condition.code, parameters });
  }
  return result;
}

/** Unknown role strings fall back to nothing (the API never sends one; the fixtures must not invent grants). */
export function grantsForRoleName(role: string, rules: Partial<FixtureRules> = {}): FixtureGrant[] {
  return (ROLE_ORDER as readonly string[]).includes(role) ? grantsFor(role as FixtureRole, rules) : [];
}

namespace BayiOS.SharedKernel.Authorization;

/// <summary>
/// Adjustable tenant rules (roles-permissions §4) that switch conditional (K) grants on or off. Read from the active
/// tenant's settings; <see cref="Defaults"/> applies when a tenant has no settings row.
/// </summary>
public sealed record TenantAccessRules(
    bool SalesCanApproveOrders,
    decimal SalesMaxDiscountPercent,
    bool ShowCostToManager,
    bool ShowCostToViewer,
    bool StaffCanAssignBarcode,
    bool StaffCanApproveReceipt)
{
    public static TenantAccessRules Defaults { get; } = new(true, 0m, false, false, false, false);
}

/// <summary>Reads <see cref="TenantAccessRules"/> for the request's tenant (host adapter over Identity settings).</summary>
public interface ITenantAccessRules
{
    Task<TenantAccessRules> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The condition behind one K / O cell of the matrix (roles-permissions §3 footnotes).
/// </summary>
/// <param name="Footnote">The document footnote number; null for a plain "O" cell (own records, no footnote).</param>
/// <param name="Code">Stable id of the condition (also returned by <c>GET /me</c>).</param>
/// <param name="Rule">Tenant rule that must be on for the grant to exist at all; null = always on.</param>
/// <param name="RecordLevel">
/// True when the grant is limited per record (ownership, task link, discount limit): the endpoint gate lets the caller
/// through, and the application service must apply the condition through <see cref="IAuthorizationContext"/>.
/// False when the tenant rule alone decides (the grant is then unconditional or absent).
/// </param>
public sealed record GrantCondition(
    string Role,
    string Permission,
    int? Footnote,
    string Code,
    Func<TenantAccessRules, bool>? Rule,
    bool RecordLevel);

/// <summary>
/// Every K / O cell of <see cref="RolePermissions"/> with its condition. A cell without an entry here is denied
/// (fail-closed); a unit test keeps this table and the matrix in step.
/// </summary>
public static class ConditionalGrants
{
    public static IReadOnlyList<GrantCondition> All { get; } =
    [
        // ¹ admin manages manager and lower roles only (no member-management endpoint yet).
        new(Roles.Admin, Permissions.MembersManage, 1, "members.below_admin", null, RecordLevel: true),

        // ² tenant rule "warehouse staff may assign barcodes".
        new(Roles.WarehouseStaff, Permissions.CatalogBarcodeAssign, 2, "tenant_rule.staff_can_assign_barcode", r => r.StaffCanAssignBarcode, RecordLevel: false),

        // ³ tenant rule "show cost price to manager / viewer".
        new(Roles.Manager, Permissions.PricesReadCost, 3, "tenant_rule.show_cost_to_manager", r => r.ShowCostToManager, RecordLevel: false),
        new(Roles.Viewer, Permissions.PricesReadCost, 3, "tenant_rule.show_cost_to_viewer", r => r.ShowCostToViewer, RecordLevel: false),

        // ⁴ sales: order and customer reports only, nothing with cost (no report endpoint yet).
        new(Roles.Sales, Permissions.ReportsRead, 4, "reports.orders_and_customers_only", null, RecordLevel: true),
        new(Roles.Sales, Permissions.ReportsExport, 4, "reports.orders_and_customers_only", null, RecordLevel: true),

        // ⁵ warehouse staff: movements they created plus movements of products in their task context.
        new(Roles.WarehouseStaff, Permissions.InventoryMovementsRead, 5, "movements.own_or_task_context", null, RecordLevel: true),

        // ⁶ tenant rule "warehouse staff may approve receipts" (no receipt-approval endpoint yet).
        new(Roles.WarehouseStaff, Permissions.InventoryReceiveApprove, 6, "tenant_rule.staff_can_approve_receipt", r => r.StaffCanApproveReceipt, RecordLevel: false),

        // ⁷ sales records return requests as drafts only (no returns endpoint yet).
        new(Roles.Sales, Permissions.InventoryReturns, 7, "returns.draft_only", null, RecordLevel: true),

        // ⁸ warehouse staff: orders behind their assigned or pool tasks, without prices.
        new(Roles.WarehouseStaff, Permissions.OrdersRead, 8, "orders.task_linked_without_prices", null, RecordLevel: true),

        // O: sales edits drafts it created.
        new(Roles.Sales, Permissions.OrdersEditDraft, null, "orders.own", null, RecordLevel: true),

        // ⁹ tenant rule "sales may approve orders" (default on; amount threshold is P1).
        new(Roles.Sales, Permissions.OrdersApprove, 9, "tenant_rule.sales_can_approve_orders", r => r.SalesCanApproveOrders, RecordLevel: false),

        // ¹⁰ tenant rule "sales discount limit %X": 0 = no override; otherwise discounts up to X %, no manual price.
        new(Roles.Sales, Permissions.OrdersPriceOverride, 10, "orders.discount_within_limit", r => r.SalesMaxDiscountPercent > 0m, RecordLevel: true),

        // ¹¹ sales cancels its own draft / pending-approval orders only.
        new(Roles.Sales, Permissions.OrdersCancel, 11, "orders.own_draft_or_pending", null, RecordLevel: true),

        // ¹² sales sees the tasks of its own orders.
        new(Roles.Sales, Permissions.TasksReadAll, 12, "tasks.own_orders", null, RecordLevel: true),
    ];

    private static readonly Dictionary<(string Role, string Permission), GrantCondition> ByCell =
        All.ToDictionary(c => (c.Role, c.Permission));

    public static GrantCondition? Find(string? role, string permission) =>
        role is not null && ByCell.TryGetValue((role, permission), out var condition) ? condition : null;
}

using System.Collections.Frozen;

namespace BayiOS.SharedKernel.Authorization;

/// <summary>How a role holds a permission (docs/product/roles-permissions.md §3 legend).</summary>
public enum PermissionGrant
{
    /// <summary>"–": not granted.</summary>
    None,

    /// <summary>"✓": granted unconditionally.</summary>
    Allowed,

    /// <summary>"K": granted only when a tenant rule (§4) or a footnote condition holds.</summary>
    Conditional,

    /// <summary>"O": granted for the caller's own records only.</summary>
    OwnRecordsOnly,
}

/// <summary>
/// Role → permission matrix, transcribed 1:1 from docs/product/roles-permissions.md §3.
/// A unit test parses that document and fails on any drift.
/// </summary>
public static class RolePermissions
{
    // Columns follow Roles.All: owner, admin, manager, sales, warehouse_staff, viewer.
    // Y = ✓, - = –, K = conditional, O = own records only.
    private static readonly (string Permission, string Grants)[] Matrix =
    [
        // §3.1
        (Permissions.TenantRead, "YYYYYY"),
        (Permissions.TenantSettingsManage, "YY----"),
        (Permissions.TenantDeleteRequest, "Y-----"),
        (Permissions.MembersRead, "YYY---"),
        (Permissions.MembersInvite, "YY----"),
        (Permissions.MembersManage, "YK----"),
        (Permissions.OwnershipTransfer, "Y-----"),
        (Permissions.BillingRead, "YY----"),
        (Permissions.BillingManage, "Y-----"),
        (Permissions.AuditRead, "YY----"),
        (Permissions.SecurityManage, "YY----"),

        // §3.2
        (Permissions.CatalogRead, "YYYYYY"),
        (Permissions.CatalogWrite, "YYY---"),
        (Permissions.CatalogBarcodeAssign, "YYY-K-"),
        (Permissions.CatalogFieldsManage, "YY----"),
        (Permissions.PricesReadSell, "YYYY-Y"),
        (Permissions.PricesReadCost, "YYK--K"),
        (Permissions.PricesWrite, "YY----"),
        (Permissions.PricelistsAssign, "YYY---"),

        // §3.3
        (Permissions.ImportsRun, "YY----"),
        (Permissions.ImportsRunStock, "YYY---"),
        (Permissions.ImportsRollback, "YY----"),
        (Permissions.ReportsRead, "YYYK-Y"),
        (Permissions.ReportsExport, "YYYK-Y"),
        (Permissions.DataExportFull, "Y-----"),

        // §3.4
        (Permissions.InventoryRead, "YYYYYY"),
        (Permissions.InventoryMovementsRead, "YYY-OY"),
        (Permissions.InventoryReceive, "YYY-Y-"),
        (Permissions.InventoryReceiveApprove, "YYY-K-"),
        (Permissions.InventoryAdjust, "YYY---"),
        (Permissions.InventoryCountManage, "YYY---"),
        (Permissions.InventoryCountPerform, "YYY-Y-"),
        (Permissions.InventoryTransfer, "YYY---"),
        (Permissions.InventoryTransferReceive, "YYY-Y-"),
        (Permissions.InventoryReturns, "YYYKY-"),
        (Permissions.WarehousesManage, "YYY---"),

        // §3.5
        (Permissions.OrdersRead, "YYYYKY"),
        (Permissions.OrdersCreate, "YYYY--"),
        (Permissions.OrdersEditDraft, "YYYO--"),
        (Permissions.OrdersSubmit, "YYYY--"),
        (Permissions.OrdersApprove, "YYYK--"),
        (Permissions.OrdersEditApproved, "YYY---"),
        (Permissions.OrdersPriceOverride, "YYYK--"),
        (Permissions.OrdersCancel, "YYYK--"),
        (Permissions.OrdersShip, "YYY-Y-"),
        (Permissions.WhatsappParse, "YYYY--"),
        (Permissions.CustomersRead, "YYYY-Y"),
        (Permissions.CustomersWrite, "YYYY--"),
        (Permissions.CustomersGroupsManage, "YYY---"),

        // §3.6
        (Permissions.TasksReadAll, "YYYK-Y"),
        (Permissions.TasksReadOwn, "YYY-Y-"),
        (Permissions.TasksAssign, "YYY---"),
        (Permissions.TasksClaim, "--Y-Y-"),
        (Permissions.TasksPick, "YYY-Y-"),
        (Permissions.TasksPack, "YYY-Y-"),
        (Permissions.TasksReportShortage, "YYY-Y-"),
    ];

    private static readonly FrozenDictionary<(string Role, string Permission), PermissionGrant> Grants = BuildGrants();

    /// <summary>Every permission id in the matrix, in document order.</summary>
    public static IReadOnlyList<string> AllPermissions { get; } = [.. Matrix.Select(row => row.Permission)];

    public static PermissionGrant GetGrant(string? role, string permission) =>
        role is not null && Grants.TryGetValue((role, permission), out var grant) ? grant : PermissionGrant.None;

    /// <summary>
    /// Endpoint-level gate. Only unconditional (✓) grants pass. Conditional (K) and own-records (O) grants
    /// stay fail-closed here until the tenant-rule / record-ownership evaluation for those endpoints exists.
    /// </summary>
    public static bool IsGranted(string? role, string permission) =>
        GetGrant(role, permission) == PermissionGrant.Allowed;

    private static FrozenDictionary<(string Role, string Permission), PermissionGrant> BuildGrants()
    {
        var grants = new Dictionary<(string, string), PermissionGrant>();
        foreach (var (permission, row) in Matrix)
        {
            if (row.Length != Roles.All.Count)
            {
                throw new InvalidOperationException($"Permission matrix row '{permission}' must have {Roles.All.Count} cells.");
            }

            for (var i = 0; i < row.Length; i++)
            {
                grants.Add((Roles.All[i], permission), row[i] switch
                {
                    'Y' => PermissionGrant.Allowed,
                    'K' => PermissionGrant.Conditional,
                    'O' => PermissionGrant.OwnRecordsOnly,
                    '-' => PermissionGrant.None,
                    _ => throw new InvalidOperationException($"Unknown grant '{row[i]}' for '{permission}'."),
                });
            }
        }

        return grants.ToFrozenDictionary();
    }
}

using System.Text.RegularExpressions;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.UnitTests.Authorization;

public partial class RolePermissionsTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, PermissionGrant[]>> DocumentMatrix = new(ParseDocumentMatrix);

    [Fact]
    public void Code_matrix_matches_roles_permissions_document_exactly()
    {
        var document = DocumentMatrix.Value;

        Assert.Equal(document.Keys.Order(), RolePermissions.AllPermissions.Order());
        foreach (var (permission, grants) in document)
        {
            for (var i = 0; i < Roles.All.Count; i++)
            {
                Assert.True(
                    grants[i] == RolePermissions.GetGrant(Roles.All[i], permission),
                    $"{Roles.All[i]} / {permission}: document says {grants[i]}, code says {RolePermissions.GetGrant(Roles.All[i], permission)}");
            }
        }
    }

    [Fact]
    public void Document_matrix_was_parsed()
    {
        // Guards the parser itself: §3.1–§3.6 define 56 permission ids.
        Assert.Equal(56, DocumentMatrix.Value.Count);
    }

    [Theory]
    [InlineData(Roles.Owner, Permissions.TenantSettingsManage, true)]
    [InlineData(Roles.Admin, Permissions.TenantSettingsManage, true)]
    [InlineData(Roles.Manager, Permissions.TenantSettingsManage, false)]
    [InlineData(Roles.Viewer, Permissions.TenantRead, true)]
    [InlineData(Roles.WarehouseStaff, Permissions.PricesReadSell, false)]
    [InlineData(Roles.Owner, Permissions.TasksClaim, false)]
    [InlineData(Roles.WarehouseStaff, Permissions.TasksClaim, true)]
    public void IsGranted_follows_unconditional_grants(string role, string permission, bool expected)
    {
        Assert.Equal(expected, RolePermissions.IsGranted(role, permission));
    }

    [Theory]
    [InlineData(Roles.Sales, Permissions.OrdersApprove, PermissionGrant.Conditional)]
    [InlineData(Roles.Admin, Permissions.MembersManage, PermissionGrant.Conditional)]
    [InlineData(Roles.Sales, Permissions.OrdersEditDraft, PermissionGrant.OwnRecordsOnly)]
    [InlineData(Roles.WarehouseStaff, Permissions.InventoryMovementsRead, PermissionGrant.OwnRecordsOnly)]
    public void Conditional_and_own_record_grants_are_fail_closed_at_the_endpoint_gate(string role, string permission, PermissionGrant grant)
    {
        Assert.Equal(grant, RolePermissions.GetGrant(role, permission));
        Assert.False(RolePermissions.IsGranted(role, permission));
    }

    [Fact]
    public void Viewer_has_no_write_or_manage_permission()
    {
        var writeLike = RolePermissions.AllPermissions.Where(p => p is not (Permissions.ReportsExport)
            && WriteVerb().IsMatch(p));

        Assert.All(writeLike, p => Assert.False(RolePermissions.IsGranted(Roles.Viewer, p), p));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dealer_user")]
    [InlineData("OWNER")]
    [InlineData("superadmin")]
    public void Unknown_or_reserved_roles_get_nothing(string? role)
    {
        Assert.All(RolePermissions.AllPermissions, p => Assert.False(RolePermissions.IsGranted(role, p)));
        Assert.False(Roles.IsAssignable(role));
    }

    [Fact]
    public void Unknown_permission_is_never_granted()
    {
        Assert.False(RolePermissions.IsGranted(Roles.Owner, "everything.allowed"));
    }

    private static Dictionary<string, PermissionGrant[]> ParseDocumentMatrix()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "product", "roles-permissions.md");
        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l.StartsWith("## 3.", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, start, l => l.StartsWith("### Dipnotlar", StringComparison.Ordinal));
        Assert.True(start >= 0 && end > start, "Permission matrix section not found in roles-permissions.md");

        var matrix = new Dictionary<string, PermissionGrant[]>();
        foreach (var line in lines[start..end])
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length != 9 || !PermissionId().IsMatch(cells[1]))
            {
                continue; // header, separator or non-table line
            }

            var ids = cells[1].Split(" (")[0].Split(" / ", StringSplitOptions.TrimEntries);
            var grants = cells[2..8].Select(ParseCell).ToArray();
            foreach (var id in ids)
            {
                matrix.Add(id, grants);
            }
        }

        return matrix;
    }

    private static PermissionGrant ParseCell(string cell) => cell switch
    {
        "✓" => PermissionGrant.Allowed,
        "–" => PermissionGrant.None,
        _ when cell.StartsWith('K') => PermissionGrant.Conditional,
        _ when cell.StartsWith('O') => PermissionGrant.OwnRecordsOnly,
        _ => throw new InvalidDataException($"Unknown matrix cell '{cell}'"),
    };

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs", "product")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"^[a-z_]+(\.[a-z_]+)+")]
    private static partial Regex PermissionId();

    [GeneratedRegex(@"\.(write|manage|create|edit|approve|adjust|assign|delete|invite|transfer|run|rollback|ship|cancel|submit|override|receive|returns|perform|claim|pick|pack|report_shortage|parse)")]
    private static partial Regex WriteVerb();
}

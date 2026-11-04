namespace BayiOS.SharedKernel.Authorization;

/// <summary>
/// Fixed MVP role set (docs/product/roles-permissions.md §1–§2). A user has exactly one role per tenant.
/// <c>dealer_user</c> is reserved for the future dealer portal and is intentionally not assignable.
/// </summary>
public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string Sales = "sales";
    public const string WarehouseStaff = "warehouse_staff";
    public const string Viewer = "viewer";

    /// <summary>Assignable roles, in the column order of the permission matrix.</summary>
    public static readonly IReadOnlyList<string> All = [Owner, Admin, Manager, Sales, WarehouseStaff, Viewer];

    public static bool IsAssignable(string? role) => role is not null && All.Contains(role);
}

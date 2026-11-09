using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.UnitTests.Authorization;

/// <summary>#69: every K / O cell has exactly one condition, and the gate / service contract of <see cref="AuthorizationContext"/>.</summary>
public class ConditionalGrantsTests
{
    private static readonly TenantAccessRules AllOff = new(false, 0m, false, false, false, false);
    private static readonly TenantAccessRules AllOn = new(true, 10m, true, true, true, true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_conditional_or_own_cell_has_exactly_one_condition_and_nothing_else_does()
    {
        var cells = (from role in Roles.All
                     from permission in RolePermissions.AllPermissions
                     let grant = RolePermissions.GetGrant(role, permission)
                     where grant is PermissionGrant.Conditional or PermissionGrant.OwnRecordsOnly
                     select (role, permission)).ToHashSet();

        Assert.Equal(cells.Order(), ConditionalGrants.All.Select(c => (c.Role, c.Permission)).Order());
        Assert.Equal(ConditionalGrants.All.Count, ConditionalGrants.All.Select(c => (c.Role, c.Permission)).Distinct().Count());
        Assert.All(ConditionalGrants.All.Where(c => RolePermissions.GetGrant(c.Role, c.Permission) == PermissionGrant.OwnRecordsOnly), c => Assert.True(c.RecordLevel, c.Code));
    }

    [Theory]
    // ¹ ⁴ ⁵ ⁷ ⁸ ¹¹ ¹² and O: record-level regardless of tenant rules.
    [InlineData(Roles.Admin, Permissions.MembersManage, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.Sales, Permissions.ReportsRead, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.WarehouseStaff, Permissions.InventoryMovementsRead, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.Sales, Permissions.InventoryReturns, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.WarehouseStaff, Permissions.OrdersRead, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.Sales, Permissions.OrdersEditDraft, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.Sales, Permissions.OrdersCancel, PermissionScope.Restricted, PermissionScope.Restricted)]
    [InlineData(Roles.Sales, Permissions.TasksReadAll, PermissionScope.Restricted, PermissionScope.Restricted)]
    // ² ³ ⁶ ⁹: the tenant rule alone decides.
    [InlineData(Roles.WarehouseStaff, Permissions.CatalogBarcodeAssign, PermissionScope.None, PermissionScope.All)]
    [InlineData(Roles.Manager, Permissions.PricesReadCost, PermissionScope.None, PermissionScope.All)]
    [InlineData(Roles.Viewer, Permissions.PricesReadCost, PermissionScope.None, PermissionScope.All)]
    [InlineData(Roles.WarehouseStaff, Permissions.InventoryReceiveApprove, PermissionScope.None, PermissionScope.All)]
    [InlineData(Roles.Sales, Permissions.OrdersApprove, PermissionScope.None, PermissionScope.All)]
    // ¹⁰: rule on (limit > 0) and record-level (limit check).
    [InlineData(Roles.Sales, Permissions.OrdersPriceOverride, PermissionScope.None, PermissionScope.Restricted)]
    // ✓ and – cells are unaffected by rules.
    [InlineData(Roles.Owner, Permissions.PricesReadCost, PermissionScope.All, PermissionScope.All)]
    [InlineData(Roles.Sales, Permissions.PricesReadCost, PermissionScope.None, PermissionScope.None)]
    [InlineData(Roles.WarehouseStaff, Permissions.PricesReadSell, PermissionScope.None, PermissionScope.None)]
    [InlineData("dealer_user", Permissions.OrdersRead, PermissionScope.None, PermissionScope.None)]
    [InlineData(null, Permissions.TenantRead, PermissionScope.None, PermissionScope.None)]
    public void Footnote_conditions_resolve_per_tenant_rule(string? role, string permission, PermissionScope rulesOff, PermissionScope rulesOn)
    {
        Assert.Equal(rulesOff, AuthorizationContext.Evaluate(role, permission, AllOff));
        Assert.Equal(rulesOn, AuthorizationContext.Evaluate(role, permission, AllOn));
    }

    [Fact]
    public void Defaults_match_the_documented_rule_defaults()
    {
        // roles-permissions §4: sales approval on, discount 0 %, cost hidden, staff barcode / receipt approval off.
        Assert.Equal(new TenantAccessRules(true, 0m, false, false, false, false), TenantAccessRules.Defaults);
    }

    [Fact]
    public async Task Gate_remembers_record_level_passes_until_a_service_checks_them()
    {
        var context = Context(Roles.Sales);
        Assert.Equal(PermissionScope.Restricted, await context.GateAsync([Permissions.OrdersEditDraft], Ct));
        Assert.Equal([Permissions.OrdersEditDraft], context.UncheckedRestrictedGrants);

        Assert.False(await context.CanAsync(Permissions.OrdersEditDraft, recordCondition: false, Ct));
        Assert.Empty(context.UncheckedRestrictedGrants);
        Assert.True(await context.CanAsync(Permissions.OrdersEditDraft, recordCondition: true, Ct));
    }

    [Fact]
    public async Task Checking_a_different_permission_does_not_clear_a_pending_gate_pass()
    {
        var context = Context(Roles.WarehouseStaff);
        Assert.Equal(PermissionScope.Restricted, await context.GateAsync([Permissions.OrdersRead], Ct));
        await context.ScopeAsync(Permissions.InventoryMovementsRead, Ct);
        Assert.Equal([Permissions.OrdersRead], context.UncheckedRestrictedGrants);
    }

    [Fact]
    public async Task Any_of_gate_passes_on_the_best_grant_and_needs_a_check_only_when_all_are_record_level()
    {
        // viewer: tasks.read.all ✓ → nothing pending.
        var viewer = Context(Roles.Viewer);
        Assert.Equal(PermissionScope.All, await viewer.GateAsync([Permissions.TasksReadOwn, Permissions.TasksReadAll], Ct));
        Assert.Empty(viewer.UncheckedRestrictedGrants);

        // sales: tasks.read.own –, tasks.read.all K¹² → pending until the service evaluates tasks.read.all.
        var sales = Context(Roles.Sales);
        Assert.Equal(PermissionScope.Restricted, await sales.GateAsync([Permissions.TasksReadOwn, Permissions.TasksReadAll], Ct));
        Assert.Equal([Permissions.TasksReadAll], sales.UncheckedRestrictedGrants);
        await sales.ScopeAsync(Permissions.TasksReadAll, Ct);
        Assert.Empty(sales.UncheckedRestrictedGrants);

        // warehouse_staff: neither listed task permission is K for it, but orders.edit.draft is denied outright.
        var staff = Context(Roles.WarehouseStaff);
        Assert.Equal(PermissionScope.None, await staff.GateAsync([Permissions.OrdersEditDraft, Permissions.PricesReadCost], Ct));
        Assert.Empty(staff.UncheckedRestrictedGrants);
    }

    [Fact]
    public async Task Without_a_tenant_nothing_is_granted()
    {
        var context = new AuthorizationContext(new RequestContext { UserId = Guid.NewGuid(), Role = Roles.Owner }, new FixedRules(AllOn));
        Assert.Equal(PermissionScope.None, await context.GateAsync([Permissions.TenantRead], Ct));
        Assert.Empty(await context.EffectivePermissionsAsync(Ct));
    }

    [Fact]
    public async Task Tenant_rules_are_read_once_and_only_when_needed()
    {
        var rules = new FixedRules(AllOn);
        var context = new AuthorizationContext(new RequestContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), Role = Roles.Manager }, rules);
        await context.ScopeAsync(Permissions.OrdersRead, Ct);
        Assert.Equal(0, rules.Reads);
        Assert.Equal(PermissionScope.All, await context.ScopeAsync(Permissions.PricesReadCost, Ct));
        await context.EffectivePermissionsAsync(Ct);
        Assert.Equal(1, rules.Reads);
    }

    [Theory]
    // K cells behind a tenant rule that is off → that condition; ✓, –, record-level K without a rule, or rule on → null.
    [InlineData(Roles.Sales, Permissions.OrdersApprove, "tenant_rule.sales_can_approve_orders", null)]
    [InlineData(Roles.Sales, Permissions.OrdersPriceOverride, "orders.discount_within_limit", null)]
    [InlineData(Roles.WarehouseStaff, Permissions.CatalogBarcodeAssign, "tenant_rule.staff_can_assign_barcode", null)]
    [InlineData(Roles.Manager, Permissions.PricesReadCost, "tenant_rule.show_cost_to_manager", null)]
    [InlineData(Roles.Sales, Permissions.OrdersCancel, null, null)]
    [InlineData(Roles.Owner, Permissions.OrdersApprove, null, null)]
    [InlineData(Roles.Viewer, Permissions.OrdersApprove, null, null)]
    public void Switched_off_by_tenant_rule_names_the_condition_only_when_the_rule_is_off(
        string role, string permission, string? whenOff, string? whenOn)
    {
        Assert.Equal(whenOff, AuthorizationContext.SwitchedOffByTenantRule(role, permission, AllOff)?.Code);
        Assert.Equal(whenOn, AuthorizationContext.SwitchedOffByTenantRule(role, permission, AllOn)?.Code);
    }

    [Fact]
    public async Task Effective_permissions_carry_the_discount_limit_as_a_parameter()
    {
        var context = new AuthorizationContext(
            new RequestContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), Role = Roles.Sales }, new FixedRules(AllOn with { SalesMaxDiscountPercent = 7.5m }));
        var permissions = (await context.EffectivePermissionsAsync(Ct)).ToDictionary(p => p.Permission);

        Assert.Equal(new GrantParameters(7.5m), permissions[Permissions.OrdersPriceOverride].Parameters);
        Assert.All(permissions.Values.Where(p => p.Permission != Permissions.OrdersPriceOverride), p => Assert.Null(p.Parameters));
        Assert.All(ConditionalGrants.All.Where(c => c.Parameters is not null), c => Assert.Equal(10, c.Footnote));
    }

    private static AuthorizationContext Context(string role) =>
        new(new RequestContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), Role = role }, new FixedRules(TenantAccessRules.Defaults));

    private sealed class FixedRules(TenantAccessRules rules) : ITenantAccessRules
    {
        public int Reads { get; private set; }

        public Task<TenantAccessRules> GetAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(rules);
        }
    }
}

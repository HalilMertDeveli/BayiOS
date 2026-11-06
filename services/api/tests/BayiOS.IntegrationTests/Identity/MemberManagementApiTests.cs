using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Security;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>
/// #129: role changes, disable/enable and ownership transfer (roles-permissions §3.1 + footnote 1, user-flows AF-09),
/// and the adjustable tenant rules of §4 through <c>/tenant/settings</c>. Synthetic data only.
/// </summary>
public sealed class MemberManagementApiTests(ApiFixture fixture)
{
    [Theory]
    // owner manages every role and may grant admin; never owner (that is a transfer).
    [InlineData("owner", "manager", "admin", true, null)]
    [InlineData("owner", "admin", "viewer", true, null)]
    [InlineData("owner", "sales", "warehouse_staff", true, null)]
    // admin (footnote 1): manager and lower only, and never grants admin.
    [InlineData("admin", "manager", "sales", true, null)]
    [InlineData("admin", "viewer", "manager", true, null)]
    [InlineData("admin", "sales", "admin", false, "members.role_not_grantable")]
    [InlineData("admin", "admin", "viewer", false, "members.not_manageable")]
    [InlineData("admin", "owner", "viewer", false, "members.not_manageable")]
    // no members.manage at all.
    [InlineData("manager", "sales", "viewer", false, "auth.forbidden")]
    [InlineData("sales", "viewer", "sales", false, "auth.forbidden")]
    [InlineData("warehouse_staff", "viewer", "sales", false, "auth.forbidden")]
    [InlineData("viewer", "sales", "viewer", false, "auth.forbidden")]
    public async Task Role_changes_follow_the_role_hierarchy(string callerRole, string targetRole, string newRole, bool allowed, string? code)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var caller = callerRole == Roles.Owner ? owner : await owner.AddMemberAsync(fixture, callerRole);
        var target = targetRole == Roles.Owner ? await owner.AddMemberAsync(fixture, Roles.Owner) : await owner.AddMemberAsync(fixture, targetRole);

        var response = await MembersKit.ChangeRoleAsync(caller, target.UserId, newRole);
        if (allowed)
        {
            var member = await caller.OkAsync<MemberResponse>(response);
            Assert.Equal((target.UserId, newRole), (member.UserId, member.Role));
            var changed = Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberRoleChanged));
            var diff = changed.Changes!["role"];
            Assert.Equal((targetRole, newRole), (diff.GetProperty("from").GetString(), diff.GetProperty("to").GetString()));
            Assert.Equal(target.UserId, changed.Changes["userId"].GetGuid());

            // The new role applies on the target's next request.
            var me = await target.OkAsync<MeResponse>(await target.Http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "/api/v1/me") { Headers = { { "X-Tenant-Id", owner.TenantId.ToString() } } },
                TestContext.Current.CancellationToken));
            Assert.Equal(newRole, me.ActiveTenant!.Role);
        }
        else
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, code!);
            Assert.Empty(await MembersKit.AuditAsync(owner, AuditActions.MemberRoleChanged));
        }
    }

    [Fact]
    public async Task Owner_is_granted_only_by_transfer_and_the_last_active_owner_stays()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var manager = await owner.AddMemberAsync(fixture, Roles.Manager);

        await HttpAssert.ProblemAsync(await MembersKit.ChangeRoleAsync(owner, manager.UserId, Roles.Owner), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await MembersKit.ChangeRoleAsync(owner, owner.UserId, Roles.Admin), HttpStatusCode.Conflict, "members.last_owner");
        await HttpAssert.ProblemAsync(await owner.PostAsync($"/api/v1/members/{owner.UserId}/disable", new { }), HttpStatusCode.Conflict, "members.cannot_disable_self");

        // A second active owner lets the first step down; a disabled second owner does not count.
        var second = await owner.AddMemberAsync(fixture, Roles.Owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/members/{second.UserId}/disable", new { })).StatusCode);
        await HttpAssert.ProblemAsync(await MembersKit.ChangeRoleAsync(owner, owner.UserId, Roles.Admin), HttpStatusCode.Conflict, "members.last_owner");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/members/{second.UserId}/enable", new { })).StatusCode);
        var stepped = await owner.OkAsync<MemberResponse>(await MembersKit.ChangeRoleAsync(owner, owner.UserId, Roles.Admin));
        Assert.Equal(Roles.Admin, stepped.Role);
        await HttpAssert.ProblemAsync(await MembersKit.ChangeRoleAsync(second, second.UserId, Roles.Viewer), HttpStatusCode.Conflict, "members.last_owner");
        await HttpAssert.ProblemAsync(await second.PostAsync($"/api/v1/members/{second.UserId}/disable", new { }), HttpStatusCode.Conflict, "members.cannot_disable_self");
    }

    [Fact]
    public async Task Concurrent_owner_demotions_keep_one_active_owner()
    {
        var first = await CatalogClient.NewTenantOwnerAsync(fixture);
        var second = await first.AddMemberAsync(fixture, Roles.Owner);
        for (var round = 0; round < 3; round++)
        {
            var responses = await Task.WhenAll(
                MembersKit.ChangeRoleAsync(first, second.UserId, Roles.Admin),
                MembersKit.ChangeRoleAsync(second, first.UserId, Roles.Admin));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(1L, await BillingKitScalar(
                "SELECT count(*) FROM identity.memberships WHERE tenant_id = $1 AND role = 'owner' AND status = 'active'", first.TenantId));

            // Restore two owners for the next round.
            await Billing.BillingKit.ExecuteAsync(fixture,
                "UPDATE identity.memberships SET role = 'owner' WHERE tenant_id = $1 AND user_id IN ($2, $3)", first.TenantId, first.UserId, second.UserId);
        }
    }

    [Fact]
    public async Task A_disabled_member_loses_access_on_the_next_request_and_regains_it_when_enabled()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var member = await MembersKit.JoinAsync(fixture, owner, Roles.Sales);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/tenant")).StatusCode);

        var disabled = await owner.OkAsync<MemberResponse>(await owner.PostAsync($"/api/v1/members/{member.UserId}/disable", new { }));
        Assert.Equal("disabled", disabled.Status);
        await HttpAssert.ProblemAsync(await member.GetAsync("/api/v1/tenant"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/customers", new { name = "Sentetik" }), HttpStatusCode.Forbidden, "tenant.access_denied");
        Assert.Equal("disabled", (await member.OkAsync<MeResponse>(await member.Http.GetAsync("/api/v1/me", TestContext.Current.CancellationToken))).Memberships.Single().Status);

        // Idempotent: disabling again changes nothing and records nothing.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/members/{member.UserId}/disable", new { })).StatusCode);
        Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberDisabled));

        Assert.Equal("active", (await owner.OkAsync<MemberResponse>(await owner.PostAsync($"/api/v1/members/{member.UserId}/enable", new { }))).Status);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/tenant")).StatusCode);
        Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberEnabled));
    }

    [Theory]
    [InlineData("admin", "sales", true)]
    [InlineData("admin", "manager", true)]
    [InlineData("admin", "admin", false)]
    [InlineData("admin", "owner", false)]
    [InlineData("manager", "sales", false)]
    public async Task Disabling_follows_the_role_hierarchy(string callerRole, string targetRole, bool allowed)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var caller = await owner.AddMemberAsync(fixture, callerRole);
        var target = await owner.AddMemberAsync(fixture, targetRole);
        var response = await caller.PostAsync($"/api/v1/members/{target.UserId}/disable", new { });
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, callerRole == Roles.Admin ? "members.not_manageable" : "auth.forbidden");
            Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/tenant")).StatusCode);
        }
    }

    [Fact]
    public async Task Ownership_transfer_makes_the_target_owner_and_the_caller_admin()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var manager = await owner.AddMemberAsync(fixture, Roles.Manager);
        var admin = await owner.AddMemberAsync(fixture, Roles.Admin);
        var disabled = await owner.AddMemberAsync(fixture, Roles.Sales);
        await owner.OkAsync<MemberResponse>(await owner.PostAsync($"/api/v1/members/{disabled.UserId}/disable", new { }));

        await HttpAssert.ProblemAsync(await admin.PostAsync("/api/v1/members/ownership-transfer", new { userId = manager.UserId }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/members/ownership-transfer", new { userId = owner.UserId }), HttpStatusCode.Conflict, "members.transfer_to_self");
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/members/ownership-transfer", new { userId = disabled.UserId }), HttpStatusCode.Conflict, "members.not_active");
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/members/ownership-transfer", new { userId = Guid.NewGuid() }), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/members/ownership-transfer", new { }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        var transfer = await owner.OkAsync<OwnershipTransferResponse>(await owner.PostAsync("/api/v1/members/ownership-transfer", new { userId = manager.UserId }));
        Assert.Equal((manager.UserId, Roles.Owner, owner.UserId, Roles.Admin), (transfer.NewOwner.UserId, transfer.NewOwner.Role, transfer.PreviousOwner.UserId, transfer.PreviousOwner.Role));

        // The former owner is now an admin: no more transfer, no billing.manage; the new owner manages them.
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/members/ownership-transfer", new { userId = manager.UserId }), HttpStatusCode.Forbidden, "auth.forbidden");
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.ChangeRoleAsync(manager, owner.UserId, Roles.Viewer)).StatusCode);
        var events = await MembersKit.AuditAsync(manager, AuditActions.MemberOwnershipTransferred);
        Assert.Contains(owner.UserId.ToString(), MembersKit.ChangesJson(Assert.Single(events)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_of_another_tenant_cannot_be_managed()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture);
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        var victim = await a.AddMemberAsync(fixture, Roles.Sales);

        await HttpAssert.ProblemAsync(await MembersKit.ChangeRoleAsync(b, victim.UserId, Roles.Viewer), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.PostAsync($"/api/v1/members/{victim.UserId}/disable", new { }), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.PostAsync($"/api/v1/members/{a.UserId}/enable", new { }), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.PostAsync("/api/v1/members/ownership-transfer", new { userId = victim.UserId }), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).PostAsync($"/api/v1/members/{victim.UserId}/disable", new { }), HttpStatusCode.Forbidden, "tenant.access_denied");

        // A user who owns B and is a sales member of A has no members.manage in A through B's role.
        await SecurityProbes.AddMembershipAsync(fixture, a.TenantId, b.UserId, Roles.Sales);
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).PostAsync($"/api/v1/members/{victim.UserId}/disable", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/tenant")).StatusCode);
    }

    [Fact]
    public async Task Tenant_rule_changes_take_effect_and_are_audited_as_rules()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var sales = await owner.AddMemberAsync(fixture, Roles.Sales);
        var staff = await owner.AddMemberAsync(fixture, Roles.WarehouseStaff);

        async Task<IReadOnlyList<PermissionGrantResponse>> PermissionsOf(CatalogClient member)
        {
            var me = await member.OkAsync<MeResponse>(await member.Http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "/api/v1/me") { Headers = { { "X-Tenant-Id", owner.TenantId.ToString() } } },
                TestContext.Current.CancellationToken));
            return me.ActiveTenant!.Permissions;
        }

        Assert.Contains(await PermissionsOf(sales), p => p.Permission == Permissions.OrdersApprove);
        Assert.DoesNotContain(await PermissionsOf(staff), p => p.Permission == Permissions.CatalogBarcodeAssign);

        var settings = await owner.GetAsync("/api/v1/tenant/settings");
        var patch = await owner.PatchAsync(
            "/api/v1/tenant/settings",
            new { salesCanApproveOrders = false, salesMaxDiscountPercent = 7.5m, staffCanAssignBarcode = true, defaultTaxRate = 10m },
            settings.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var salesPermissions = await PermissionsOf(sales);
        Assert.DoesNotContain(salesPermissions, p => p.Permission == Permissions.OrdersApprove);
        Assert.Equal(7.5m, salesPermissions.Single(p => p.Permission == Permissions.OrdersPriceOverride).Parameters!.MaxDiscountPercent);
        Assert.Contains(await PermissionsOf(staff), p => p.Permission == Permissions.CatalogBarcodeAssign);
        var approvers = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync("/api/v1/members?permission=orders.approve"));
        Assert.DoesNotContain(approvers.Items, m => m.UserId == sales.UserId);

        // The endpoint gate answers the rule, not a generic denial.
        var gate = await HttpAssert.ProblemJsonAsync(
            await sales.PostAsync($"/api/v1/orders/{Guid.NewGuid()}/approve", new { }), HttpStatusCode.Forbidden, AuthorizationProblems.DisabledByTenantRule);
        Assert.Equal(Permissions.OrdersApprove, gate.GetProperty("permission").GetString());

        // Rules and other settings are separate audit events; stale ETag is refused.
        var rules = Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.TenantRulesChanged));
        var json = MembersKit.ChangesJson(rules);
        Assert.Contains("salesCanApproveOrders", json, StringComparison.Ordinal);
        Assert.Contains("staffCanAssignBarcode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultTaxRate", json, StringComparison.Ordinal);
        Assert.Contains("defaultTaxRate", MembersKit.ChangesJson(Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.TenantSettingsChanged))), StringComparison.Ordinal);
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync("/api/v1/tenant/settings", new { salesCanApproveOrders = true }, settings.Headers.ETag!.ToString()),
            HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync("/api/v1/tenant/settings", new { salesMaxDiscountPercent = 101m }, patch.Headers.ETag!.ToString()),
            HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Only tenant.settings.manage holders (owner, admin) change rules.
        await HttpAssert.ProblemAsync(
            await sales.PatchAsync("/api/v1/tenant/settings", new { salesCanApproveOrders = true }, patch.Headers.ETag!.ToString()),
            HttpStatusCode.Forbidden, "auth.forbidden");
    }

    private Task<long> BillingKitScalar(string sql, params object[] parameters) => Billing.BillingKit.ScalarAsync<long>(fixture, sql, parameters);
}

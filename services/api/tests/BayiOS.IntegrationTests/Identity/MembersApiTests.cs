using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Contracts;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Security;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>
/// #86: <c>GET /members</c> — members of the active tenant for the personnel picker (<c>members.read</c>: owner, admin,
/// manager; roles-permissions §3.1). Role/status filters, keyset paging, display-name fallback, permission and tenant
/// isolation. Synthetic data only.
/// </summary>
public sealed class MembersApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Lists_every_member_of_the_active_tenant_with_display_names_and_filters()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await DisplayNamesTests.SetProfileAsync(fixture, owner.UserId, "sahip@example.com", "Ayşe Yılmaz");
        var manager = await owner.AddMemberAsync(fixture, Roles.Manager);
        await DisplayNamesTests.SetProfileAsync(fixture, manager.UserId, "mudur.can@example.com", "   ");
        var picker = await owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        await DisplayNamesTests.SetProfileAsync(fixture, picker.UserId, "depo1@example.com", "Depo Ali");
        var noProfile = await owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var disabled = Guid.NewGuid();
        await SecurityProbes.AddMembershipAsync(fixture, owner.TenantId, disabled, Roles.WarehouseStaff, "disabled");
        var invited = Guid.NewGuid();
        await SecurityProbes.AddMembershipAsync(fixture, owner.TenantId, invited, Roles.Viewer, "invited");

        var all = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync("/api/v1/members"));
        Assert.Equal(6, all.Items.Count);
        Assert.False(all.Page.HasMore);
        var me = all.Items.Single(m => m.UserId == owner.UserId);
        Assert.Equal(("Ayşe Yılmaz", "Ayşe Yılmaz", "sahip@example.com", "owner", "active"), (me.DisplayName, me.FullName, me.Email, me.Role, me.Status));
        var boss = all.Items.Single(m => m.UserId == manager.UserId);
        Assert.Equal(("mudur.can", (string?)null, "mudur.can@example.com"), (boss.DisplayName, boss.FullName, boss.Email));
        var anonymous = all.Items.Single(m => m.UserId == noProfile.UserId);
        Assert.Equal(((string?)null, (string?)null), (anonymous.DisplayName, anonymous.Email));
        Assert.Equal("disabled", all.Items.Single(m => m.UserId == disabled).Status);
        Assert.Equal("invited", all.Items.Single(m => m.UserId == invited).Status);

        // Picker query: active members who can pick.
        var pickers = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync("/api/v1/members?role=warehouse_staff,manager&status=active"));
        Assert.Equal(new[] { manager.UserId, picker.UserId, noProfile.UserId }.Order(), pickers.Items.Select(m => m.UserId).Order());
        var staffOnly = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync("/api/v1/members?role=warehouse_staff"));
        Assert.Equal(3, staffOnly.Items.Count);
        var notActive = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync("/api/v1/members?status=invited,disabled"));
        Assert.Equal(new[] { disabled, invited }.Order(), notActive.Items.Select(m => m.UserId).Order());

        // Keyset paging: every member exactly once, membership order.
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync(
                "/api/v1/members?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))));
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(m => m.UserId));
            cursor = page.Page.HasMore ? page.Page.NextCursor : null;
        }
        while (cursor is not null);
        Assert.Equal(all.Items.Select(m => m.UserId), seen);
    }

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("manager", true)]
    [InlineData("sales", false)]
    [InlineData("warehouse_staff", false)]
    [InlineData("viewer", false)]
    public async Task Only_members_read_holders_may_list_members(string role, bool allowed)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var caller = role == Roles.Owner ? owner : await owner.AddMemberAsync(fixture, role);
        var response = await caller.GetAsync("/api/v1/members");
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "auth.forbidden");
        }
    }

    [Fact]
    public async Task Members_of_other_tenants_never_appear()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture);
        var aStaff = await a.AddMemberAsync(fixture, Roles.WarehouseStaff);
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        var bManager = await b.AddMemberAsync(fixture, Roles.Manager);

        // A's owner is also a viewer in B: listing A shows only A's members (not the caller's B membership), and B's
        // members never show up in A's list.
        await SecurityProbes.AddMembershipAsync(fixture, b.TenantId, a.UserId, Roles.Viewer);
        var listA = await a.OkAsync<MemberPageResponse>(await a.GetAsync("/api/v1/members"));
        Assert.Equal(new[] { a.UserId, aStaff.UserId }.Order(), listA.Items.Select(m => m.UserId).Order());
        Assert.All(listA.Items, m => Assert.NotEqual("viewer", m.Role));

        var listB = await b.OkAsync<MemberPageResponse>(await b.GetAsync("/api/v1/members"));
        Assert.Equal(new[] { b.UserId, bManager.UserId, a.UserId }.Order(), listB.Items.Select(m => m.UserId).Order());
        Assert.DoesNotContain(listB.Items, m => m.UserId == aStaff.UserId);

        // The header selects a tenant, it grants nothing: B's owner cannot list A; A's owner as B's viewer is denied.
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).GetAsync("/api/v1/members"), HttpStatusCode.Forbidden, "tenant.access_denied");
        await HttpAssert.ProblemAsync(await a.Targeting(b.TenantId).GetAsync("/api/v1/members"), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Theory]
    [InlineData("?role=boss")]
    [InlineData("?status=gone")]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?cursor=not-a-cursor")]
    [InlineData("?tenantId=00000000-0000-0000-0000-000000000001")]
    public async Task Rejects_malformed_queries(string query)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/members" + query), HttpStatusCode.BadRequest, "request.malformed");
    }
}

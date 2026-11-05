using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// Role sweep (roles-permissions.md §3, #15): every role × every permission-gated endpoint of the running API, with the
/// expected outcome taken from the code matrix (<see cref="RolePermissions"/>, itself kept equal to the document by a
/// unit test). Granted → the gate lets the call through (anything but 401/403); not granted → 403 <c>auth.forbidden</c>.
/// Probes use unknown ids and empty bodies, so a granted call stops at 400/404/412/428 and changes nothing.
/// </summary>
[Trait("Category", "Security")]
public sealed class RolePermissionSweepTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> AllRoles() => [.. Roles.All];

    [Fact]
    public async Task Every_endpoint_permission_is_a_known_matrix_permission()
    {
        var routes = await ApiRoutes.LoadAsync(fixture);
        var unknown = routes.Where(r => r.Permission is not null && !RolePermissions.AllPermissions.Contains(r.Permission))
            .Select(r => $"{r}: x-permission '{r.Permission}' is not in RolePermissions");
        SecurityProbes.AssertNone([.. unknown]);
    }

    [Theory]
    [MemberData(nameof(AllRoles))]
    public async Task Role_reaches_exactly_the_endpoints_its_permissions_allow(string role)
    {
        var routes = (await ApiRoutes.LoadAsync(fixture)).Where(r => r.IsTenantScoped).ToList();
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var member = role == Roles.Owner ? owner : await owner.AddMemberAsync(fixture, role);

        var failures = new List<string>();
        int granted = 0, denied = 0;
        foreach (var route in routes)
        {
            var allowed = RolePermissions.IsGranted(role, route.Permission!);
            var path = ApiRoutes.Fill(route.Template, (segment, _) => segment == "sector-templates" ? "zz-yok" : Guid.NewGuid().ToString());
            object? body = route.Method is "GET" or "DELETE" ? null : route.RequestContentTypes.Contains("multipart/form-data") ? "multipart" : new { };
            var response = await SecurityProbes.SendAsync(fixture, member, route, path, body, CatalogClient.ETag(1));
            var code = await SecurityProbes.CodeAsync(response);

            if (allowed)
            {
                granted++;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || (int)response.StatusCode >= 500)
                {
                    failures.Add($"{route} [{route.Permission}]: {role} is granted but got {(int)response.StatusCode} {code}");
                }
            }
            else
            {
                denied++;
                if (response.StatusCode != HttpStatusCode.Forbidden || code != "auth.forbidden")
                {
                    failures.Add($"{route} [{route.Permission}]: {role} is not granted but got {(int)response.StatusCode} {code}");
                }
            }
        }

        SecurityProbes.AssertNone(failures);
        Assert.Equal(routes.Count, granted + denied);
        TestContext.Current.TestOutputHelper?.WriteLine($"{role}: {granted} endpoints granted, {denied} denied.");
    }
}

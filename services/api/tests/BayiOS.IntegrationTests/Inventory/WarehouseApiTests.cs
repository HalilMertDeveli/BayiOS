using System.Net;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>Warehouses and locations (#28): CRUD, default warehouse, location mode rules, permissions, isolation.</summary>
public class WarehouseApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Warehouse_and_location_crud_with_default_and_etags()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var owner = kit.Owner;

        var main = await kit.WarehouseAsync(" ist-1 ");
        Assert.Equal(("IST-1", true, false, "active", 1L), (main.Code, main.IsDefault, main.UsesLocations, main.Status, main.Version));
        var second = await kit.WarehouseAsync("ANK", usesLocations: true);
        Assert.False(second.IsDefault);

        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/warehouses", new { code = "ist-1", name = "Kopya" }), HttpStatusCode.Conflict, "warehouse.code_taken");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/warehouses", new { code = "a b", name = "" }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Locations only in located warehouses; codes unique per warehouse.
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/warehouses/{main.Id}/locations", new { code = "A-01" }), HttpStatusCode.Conflict, "warehouse.locations_disabled");
        var a2 = await kit.LocationAsync(second.Id, "a-02", pickSequence: 2);
        var a1 = await kit.LocationAsync(second.Id, "A-01", pickSequence: 1);
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/warehouses/{second.Id}/locations", new { code = "A-01" }), HttpStatusCode.Conflict, "warehouse.location_code_taken");
        var locations = await owner.OkAsync<LocationListResponse>(await owner.GetAsync($"/api/v1/warehouses/{second.Id}/locations"));
        Assert.Equal([a1.Id, a2.Id], locations.Items.Select(l => l.Id));

        var renamed = await owner.OkAsync<LocationResponse>(await owner.PatchAsync($"/api/v1/locations/{a2.Id}", new { isActive = false }, "W/\"1\""));
        Assert.Equal((false, 2L), (renamed.IsActive, renamed.Version));
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/locations/{a2.Id}", new { pickSequence = 5 }, "W/\"1\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");

        // Moving the default; the default cannot be deactivated.
        var moved = await owner.OkAsync<WarehouseResponse>(await owner.PatchAsync($"/api/v1/warehouses/{second.Id}", new { isDefault = true }, "W/\"1\""));
        Assert.True(moved.IsDefault);
        var list = await owner.OkAsync<WarehouseListResponse>(await owner.GetAsync("/api/v1/warehouses"));
        Assert.Equal([second.Id, main.Id], list.Items.Select(w => w.Id));
        Assert.Single(list.Items, w => w.IsDefault);
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/warehouses/{second.Id}", new { status = "inactive" }, "W/\"2\""), HttpStatusCode.Conflict, "warehouse.default_inactive");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/warehouses/{main.Id}", new { name = "X" }, null), HttpStatusCode.PreconditionRequired, "precondition.required");

        var get = await owner.GetAsync($"/api/v1/warehouses/{main.Id}");
        Assert.Equal("W/\"2\"", get.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Location_mode_cannot_change_while_the_warehouse_holds_stock()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var warehouse = await kit.WarehouseAsync("W1");
        var variant = await kit.VariantAsync();

        var empty = await kit.Owner.OkAsync<WarehouseResponse>(
            await kit.Owner.PatchAsync($"/api/v1/warehouses/{warehouse.Id}", new { usesLocations = true }, "W/\"1\""));
        Assert.True(empty.UsesLocations);
        var back = await kit.Owner.OkAsync<WarehouseResponse>(
            await kit.Owner.PatchAsync($"/api/v1/warehouses/{warehouse.Id}", new { usesLocations = false }, "W/\"2\""));

        await kit.ReceiveAsync(warehouse.Id, (variant, 3m, null));
        await HttpAssert.ProblemAsync(
            await kit.Owner.PatchAsync($"/api/v1/warehouses/{warehouse.Id}", new { usesLocations = true }, $"W/\"{back.Version}\""),
            HttpStatusCode.Conflict,
            "warehouse.has_stock");
    }

    [Theory]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Viewer)]
    public async Task Roles_without_warehouses_manage_can_read_but_not_change(string role)
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var warehouse = await kit.WarehouseAsync("W1", usesLocations: true);
        var member = await kit.Owner.AddMemberAsync(fixture, role);

        Assert.Single((await member.OkAsync<WarehouseListResponse>(await member.GetAsync("/api/v1/warehouses"))).Items);
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/warehouses", new { code = "X", name = "X" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await member.PostAsync($"/api/v1/warehouses/{warehouse.Id}/locations", new { code = "A" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await member.PatchAsync($"/api/v1/warehouses/{warehouse.Id}", new { name = "X" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Fact]
    public async Task Another_tenants_warehouse_is_invisible()
    {
        var a = await InventoryKit.CreateAsync(fixture);
        var b = await InventoryKit.CreateAsync(fixture);
        var warehouseA = await a.WarehouseAsync("A1", usesLocations: true);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/warehouses/{warehouseA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/warehouses/{warehouseA.Id}/locations", new { code = "X" })).StatusCode);
        Assert.Empty((await b.Owner.OkAsync<WarehouseListResponse>(await b.Owner.GetAsync("/api/v1/warehouses"))).Items);
        await HttpAssert.ProblemAsync(
            await b.Owner.Targeting(a.TenantId).GetAsync("/api/v1/warehouses"), HttpStatusCode.Forbidden, "tenant.access_denied");
    }
}

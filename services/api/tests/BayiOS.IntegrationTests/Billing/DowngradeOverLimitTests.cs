using System.Net;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;

namespace BayiOS.IntegrationTests.Billing;

/// <summary>
/// Owner decision OD-03 (2026-11-09, roles-permissions §5): after a downgrade that leaves the tenant over a limit, the
/// stock moves needed to empty a warehouse stay allowed — adjustments (and transfers, #168) are never gated by a plan
/// limit or a plan feature — while creating or reactivating warehouses stays refused.
/// </summary>
public sealed class DowngradeOverLimitTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Over_the_warehouse_limit_after_a_downgrade_stock_can_be_adjusted_out_and_the_warehouses_emptied()
    {
        var kit = await InventoryKit.CreateAsync(fixture); // Professional
        var main = await kit.WarehouseAsync("MAIN");
        var izmir = await kit.WarehouseAsync("IZM");
        var shelf = await kit.WarehouseAsync("RAF", usesLocations: true);
        var location = await kit.LocationAsync(shelf.Id, "A-01");
        var variant = await kit.VariantAsync();
        await kit.ReceiveAsync(izmir.Id, (variant, 5m, null));
        await kit.ReceiveAsync(shelf.Id, (variant, 3m, location.Id));

        // Downgrade: Basic (no locations) and a warehouse limit of 1 against 3 active warehouses.
        await BillingKit.SetPlanAsync(fixture, kit.TenantId, BillingKit.Basic);
        await BillingKit.OverrideAsync(fixture, kit.TenantId, EntitlementCodes.MaxWarehouses, limit: 1);
        var usage = await kit.Owner.OkAsync<UsageResponse>(await kit.Owner.GetAsync("/api/v1/billing/usage"));
        Assert.Contains(usage.Items, i => i is { Code: EntitlementCodes.MaxWarehouses, Limit: 1, Current: 3, Remaining: 0 });
        await BillingKit.AssertProblemAsync(
            await kit.Owner.PostAsync("/api/v1/warehouses", new { code = "ANK", name = "Ankara" }), "entitlement.limit_exceeded", EntitlementCodes.MaxWarehouses);

        // Emptying: corrections in and out, also on a shelf location of the (now Pro-only) located warehouse, by the
        // owner and by a manager (inventory.adjust).
        var manager = await kit.Owner.AddMemberAsync(fixture, Roles.Manager);
        Assert.Equal(HttpStatusCode.Created, (await kit.AdjustAsync(kit.Owner, variant, izmir.Id, MovementTypes.AdjustmentIn, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await kit.AdjustAsync(manager, variant, izmir.Id, MovementTypes.AdjustmentOut, 6m)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await kit.AdjustAsync(kit.Owner, variant, shelf.Id, MovementTypes.AdjustmentOut, 3m, locationId: location.Id)).StatusCode);
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, izmir.Id));
        Assert.Equal((0m, 0m), await kit.BalanceAsync(variant, shelf.Id, location.Id));
        await kit.AssertInvariantsAsync();

        // Still never below zero (OD-09), over limit or not.
        await HttpAssert.ProblemAsync(
            await kit.AdjustAsync(kit.Owner, variant, izmir.Id, MovementTypes.AdjustmentOut, 1m), HttpStatusCode.Conflict, "inventory.insufficient_available");

        // The emptied warehouses can be deactivated, which brings the tenant back under its limit.
        foreach (var warehouse in new[] { izmir, shelf })
        {
            var current = await kit.Owner.OkAsync<WarehouseResponse>(await kit.Owner.GetAsync($"/api/v1/warehouses/{warehouse.Id}"));
            await kit.Owner.OkAsync<WarehouseResponse>(
                await kit.Owner.PatchAsync($"/api/v1/warehouses/{warehouse.Id}", new { status = "inactive" }, CatalogClient.ETag(current.Version)));
        }

        usage = await kit.Owner.OkAsync<UsageResponse>(await kit.Owner.GetAsync("/api/v1/billing/usage"));
        Assert.Contains(usage.Items, i => i is { Code: EntitlementCodes.MaxWarehouses, Limit: 1, Current: 1 });
        Assert.Equal(main.Id, Assert.Single(
            (await kit.Owner.OkAsync<WarehouseListResponse>(await kit.Owner.GetAsync("/api/v1/warehouses"))).Items, w => w.Status == "active").Id);
    }

    [Fact]
    public async Task Stock_adjustment_and_transfer_endpoints_carry_no_plan_gate()
    {
        // OD-03 guard: whatever plan the tenant is on, emptying a warehouse must stay possible, so no adjustment or
        // transfer operation may declare an entitlement (x-entitlement) or a plan limit (x-entitlement-limit).
        using var document = JsonDocument.Parse(await fixture.CreateClient().GetStringAsync("/openapi/v1.json", Ct));
        var stockMoves = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(p => p.Name.Contains("/inventory/adjustments", StringComparison.Ordinal) || p.Name.Contains("transfer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(stockMoves, p => p.Name == "/api/v1/inventory/adjustments");
        foreach (var path in stockMoves)
        {
            foreach (var operation in path.Value.EnumerateObject().Where(o => o.Value.ValueKind == JsonValueKind.Object))
            {
                Assert.False(operation.Value.TryGetProperty("x-entitlement", out _), $"{operation.Name} {path.Name} must not need a plan feature (OD-03)");
                Assert.False(operation.Value.TryGetProperty("x-entitlement-limit", out _), $"{operation.Name} {path.Name} must not enforce a plan limit (OD-03)");
            }
        }
    }
}

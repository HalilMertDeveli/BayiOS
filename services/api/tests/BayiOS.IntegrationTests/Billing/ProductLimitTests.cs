using System.Net;
using BayiOS.IntegrationTests.BulkImport;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.TestSupport.BulkImport;

namespace BayiOS.IntegrationTests.Billing;

/// <summary>
/// <c>max_products</c> (owner decision OD-02, Basic 999): non-archived products count; create and restore are refused at
/// the limit (403 <c>entitlement.limit_exceeded</c>), archiving frees a slot, concurrent creates cannot overshoot and an
/// import fails only the rows over the limit. The limit is lowered with an operator override to keep tests small.
/// </summary>
[Collection(ImportJobsTestGroup.Name)]
public sealed class ProductLimitTests(ApiFixture fixture)
{
    private static readonly Dictionary<string, string> ProductColumns = new()
    {
        ["sku"] = "Stok Kodu",
        ["name"] = "Ürün Adı",
        ["unit"] = "Birim",
        ["taxRate"] = "KDV Oranı",
        ["barcode"] = "Barkod",
    };

    [Fact]
    public async Task Basic_counts_non_archived_products_and_archiving_frees_a_slot()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await BillingKit.SetPlanAsync(fixture, owner.TenantId, BillingKit.Basic);
        var unit = await owner.CreateUnitAsync();
        Assert.Contains((await BillingKit.SubscriptionAsync(owner)).Limits, l => l is { Code: EntitlementCodes.MaxProducts, Value: 999 });

        await BillingKit.OverrideAsync(fixture, owner.TenantId, EntitlementCodes.MaxProducts, limit: 2);
        var first = await owner.CreateSimpleProductAsync(unit.Id, "Ürün 1", "LIM-1");
        await owner.CreateSimpleProductAsync(unit.Id, "Ürün 2", "LIM-2");

        var refused = await HttpAssert.ProblemJsonAsync(
            await owner.PostAsync("/api/v1/products", new { name = "Ürün 3", defaultUnitId = unit.Id, taxRate = 20m, sku = "LIM-3" }),
            HttpStatusCode.Forbidden,
            "entitlement.limit_exceeded");
        Assert.Equal((EntitlementCodes.MaxProducts, 2L, 2L), (
            refused.GetProperty("feature").GetString(), refused.GetProperty("limit").GetInt64(), refused.GetProperty("current").GetInt64()));
        Assert.Equal(2L, await CountAsync(owner.TenantId, "SELECT count(*) FROM catalog.products WHERE tenant_id = $1"));
        Assert.Equal(0L, await CountAsync(owner.TenantId, "SELECT count(*) FROM catalog.product_variants WHERE tenant_id = $1 AND sku = 'LIM-3'"));

        // Archiving frees a slot; restoring the archived product then takes one again and is refused while full.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/products/{first.Id}", CatalogClient.ETag(first.Version))).StatusCode);
        await owner.CreateSimpleProductAsync(unit.Id, "Ürün 3", "LIM-3");
        var archived = await owner.OkAsync<ProductResponse>(await owner.GetAsync($"/api/v1/products/{first.Id}"));
        await BillingKit.AssertProblemAsync(
            await owner.PatchAsync($"/api/v1/products/{first.Id}", new { status = "active" }, CatalogClient.ETag(archived.Version)),
            "entitlement.limit_exceeded",
            EntitlementCodes.MaxProducts);

        var usage = await owner.OkAsync<UsageResponse>(await owner.GetAsync("/api/v1/billing/usage"));
        Assert.Contains(usage.Items, i => i is { Code: EntitlementCodes.MaxProducts, Limit: 2, Current: 2, Remaining: 0 });

        // Permission comes before the entitlement: a viewer gets auth.forbidden, not the limit.
        var viewer = await owner.AddMemberAsync(fixture, Roles.Viewer);
        await HttpAssert.ProblemAsync(
            await viewer.PostAsync("/api/v1/products", new { name = "Ürün 4", defaultUnitId = unit.Id, taxRate = 20m, sku = "LIM-4" }),
            HttpStatusCode.Forbidden,
            "auth.forbidden");
    }

    [Fact]
    public async Task Concurrent_creates_cannot_overshoot_max_products_and_other_tenants_are_unaffected()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var otherUnit = await other.CreateUnitAsync();
        await BillingKit.OverrideAsync(fixture, owner.TenantId, EntitlementCodes.MaxProducts, limit: 1);

        var creates = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            owner.PostAsync("/api/v1/products", new { name = $"Eşzamanlı {i}", defaultUnitId = unit.Id, taxRate = 20m, sku = $"CC-{i}" })));
        Assert.Single(creates, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var response in creates.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "entitlement.limit_exceeded");
        }

        Assert.Equal(1L, await CountAsync(owner.TenantId, "SELECT count(*) FROM catalog.products WHERE tenant_id = $1"));

        // The override belongs to one tenant only.
        await other.CreateSimpleProductAsync(otherUnit.Id, "Başka 1", "CC-1");
        await other.CreateSimpleProductAsync(otherUnit.Id, "Başka 2", "CC-2");
    }

    [Fact]
    public async Task An_import_fails_only_the_rows_over_the_product_limit()
    {
        var kit = await ImportsKit.CreateAsync(fixture, "led_electronics");
        await BillingKit.OverrideAsync(fixture, kit.TenantId, EntitlementCodes.MaxProducts, limit: 2);
        var file = SyntheticFiles.Csv(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(4, "LIM"), ';', SyntheticFiles.Windows1254);

        var job = await kit.ImportAsync(file, "limit.csv", ProductColumns);

        Assert.Equal(("completed", 2, 2), (job.Status, job.Progress.Created, job.Progress.Failed));
        Assert.Equal(2L, await kit.VariantCountAsync());
        var failed = (await kit.RowsAsync(job.Id)).Items.Where(r => r.Outcome == "failed").ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, r => Assert.Contains(r.Errors, e => e.Code == EntitlementProblems.LimitExceeded));
    }

    private Task<long> CountAsync(Guid tenantId, string sql) => BillingKit.ScalarAsync<long>(fixture, sql, tenantId);
}

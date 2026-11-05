using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>
/// #86: text search on <c>GET /inventory/balances?q=</c> — SKU / variant name / product name contains (case-insensitive)
/// or exact barcode, resolved through the Catalog port (no cross-module join), combined with the other filters and
/// keyset paging; tenant isolation. Synthetic data only.
/// </summary>
public sealed class BalanceSearchTests(ApiFixture fixture)
{
    [Fact]
    public async Task Search_matches_sku_name_and_barcode_and_keeps_keyset_paging()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var unit = await kit.Owner.CreateUnitAsync("kutu", "Kutu");
        var pad = Assert.Single((await kit.Owner.CreateSimpleProductAsync(unit.Id, "Sentetik Fren Balatası CG125", "FRN-0001", "8690000001001")).Variants).Id;
        var disc = Assert.Single((await kit.Owner.CreateSimpleProductAsync(unit.Id, "Sentetik Fren Diski", "FRN-0002")).Variants).Id;
        var chain = Assert.Single((await kit.Owner.CreateSimpleProductAsync(unit.Id, "Sentetik Zincir 428", "ZNC-0428")).Variants).Id;
        var odd = Assert.Single((await kit.Owner.CreateSimpleProductAsync(unit.Id, "Yüzde %50 indirimli_ürün", "ODD-0001")).Variants).Id;
        var main = await kit.WarehouseAsync("ANA");
        var second = await kit.WarehouseAsync("YDK");
        await kit.ReceiveAsync(main.Id, (pad, 5m, null), (disc, 2m, null), (chain, 7m, null), (odd, 1m, null));
        await kit.ReceiveAsync(second.Id, (pad, 1m, null), (chain, 1m, null));

        async Task<List<BalanceResponse>> SearchAsync(string query)
        {
            var page = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync("/api/v1/inventory/balances" + query));
            return [.. page.Items];
        }

        // Product name contains (case-insensitive), SKU contains, exact barcode, LIKE wildcards taken literally.
        Assert.Equal(new[] { pad, pad, disc }.Order(), (await SearchAsync("?q=fren")).Select(b => b.VariantId).Order());
        Assert.Equal([chain, chain], (await SearchAsync("?q=znc-04")).Select(b => b.VariantId));
        Assert.Equal([pad, pad], (await SearchAsync("?q=8690000001001")).Select(b => b.VariantId));
        Assert.Empty(await SearchAsync("?q=869000000100"));
        Assert.Equal([odd], (await SearchAsync("?q=" + Uri.EscapeDataString("%50"))).Select(b => b.VariantId));
        Assert.Empty(await SearchAsync("?q=" + Uri.EscapeDataString("%_%x")));
        Assert.Empty(await SearchAsync("?q=yok-boyle-bir-sey"));

        // Combined with the warehouse filter; blank q = no search.
        Assert.Equal([pad], (await SearchAsync($"?q=FRN&warehouseId={second.Id}")).Select(b => b.VariantId));
        Assert.Equal(6, (await SearchAsync("?q=%20%20")).Count);

        // Keyset paging over a search: every match exactly once, in id order.
        var all = await SearchAsync("?q=sentetik&limit=200");
        Assert.Equal(5, all.Count);
        var walked = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync(
                "/api/v1/inventory/balances?q=sentetik&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))));
            walked.AddRange(page.Items.Select(b => b.Id));
            cursor = page.Page.HasMore ? page.Page.NextCursor : null;
        }
        while (cursor is not null);
        Assert.Equal(all.Select(b => b.Id), walked);

        // inventory.read is ✓ for every role, warehouse_staff included.
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        Assert.Equal(3, (await staff.OkAsync<BalancePageResponse>(await staff.GetAsync("/api/v1/inventory/balances?q=fren"))).Items.Count);

        await HttpAssert.ProblemAsync(
            await kit.Owner.GetAsync("/api/v1/inventory/balances?q=" + new string('x', 101)), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Another_tenant_searching_the_same_terms_finds_nothing()
    {
        var kit = await InventoryKit.CreateAsync(fixture);
        var unit = await kit.Owner.CreateUnitAsync("kutu", "Kutu");
        var variant = Assert.Single((await kit.Owner.CreateSimpleProductAsync(unit.Id, "Sentetik Gizli Parça", "GZL-0001", "8690000002008")).Variants).Id;
        await kit.ReceiveAsync(null, (variant, 3m, null));
        Assert.Single((await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync("/api/v1/inventory/balances?q=gizli"))).Items);

        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        foreach (var term in new[] { "gizli", "GZL-0001", "8690000002008" })
        {
            Assert.Empty((await other.OkAsync<BalancePageResponse>(await other.GetAsync("/api/v1/inventory/balances?q=" + term))).Items);
        }

        await HttpAssert.ProblemAsync(await other.Targeting(kit.TenantId).GetAsync("/api/v1/inventory/balances?q=gizli"), HttpStatusCode.Forbidden, "tenant.access_denied");
    }
}

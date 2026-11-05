using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Performance;

/// <summary>Performance tests run alone, after the parallel tests, so their timings are not other tests' noise.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceTestGroup
{
    public const string Name = "Performance";
}

/// <summary>
/// #20 — 10,000-product synthetic catalog: search/list latency through the real API (auth, tenant middleware, RLS
/// transaction, EF, PostgreSQL 17), keyset paging consistency, and permission/tenant checks on the large data set.
/// Budget (ROADMAP W4): product search p95 &lt; 500 ms. Filter with <c>--filter-trait "Category=Performance"</c>.
/// </summary>
[Collection(PerformanceTestGroup.Name)]
[Trait("Category", "Performance")]
public sealed class CatalogPerformanceTests(ApiFixture fixture)
{
    private const double BudgetMs = 500;
    private const int Warmup = 5;
    private const int Runs = 40;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static SyntheticCatalog? _catalog;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Product_search_and_listing_stay_within_the_p95_budget()
    {
        var catalog = await CatalogAsync();
        var owner = catalog.Owner;
        string[] names = ["Balata", "debriyaj", "cg125", "Filtre", "amortis", "Panel", "220V", "pcx160", "Rulman", "Ampul"];
        int N(int run) => 1 + (run * 7919 % SyntheticCatalog.ProductCount);

        // Cursors deep into the default list (pages 100–180 of 50), found by walking it once.
        var deepCursors = await CursorsAsync(owner, "/api/v1/products?limit=50", from: 100, to: 180);

        var scenarios = new (string Name, Func<int, string> Url)[]
        {
            ("List: default page (-createdAt, 50)", _ => "/api/v1/products"),
            ("List: by name (50)", _ => "/api/v1/products?sort=name"),
            ("Search: name substring (q)", r => "/api/v1/products?q=" + Uri.EscapeDataString(names[r % names.Length])),
            ("Search: SKU substring (q)", r => $"/api/v1/products?q=MTR-0{r * 37 % 999:D3}"),
            ("Search: barcode (q)", r => "/api/v1/products?q=" + SyntheticCatalog.Barcode(N(r))),
            ("Search: exact SKU (/variants?sku)", r => "/api/v1/variants?sku=" + SyntheticCatalog.Sku(N(r))),
            ("Scan: barcode lookup (/variants/lookup)", r => "/api/v1/variants/lookup?barcode=" + SyntheticCatalog.Barcode(N(r))),
            ("Filter: root category incl. descendants", r => $"/api/v1/products?categoryId={(r % 2 == 0 ? catalog.MotorRoot : catalog.LedRoot)}"),
            ("Filter: leaf category + name search", r => $"/api/v1/products?categoryId={catalog.MotorCategories[r % 5]}&q=Sentetik"),
            ("Filter: attr.voltaj (enum)", r => "/api/v1/products?attr.voltaj=" + SyntheticCatalog.Voltages[r % 3]),
            ("Filter: attr.guc_w.min (number range)", r => $"/api/v1/products?attr.guc_w.min={10 + r % 80}"),
            ("Filter: variant attr.renk + name", r => $"/api/v1/products?attr.renk={Uri.EscapeDataString(SyntheticCatalog.Colours[r % 3])}&q=LED"),
            ("Paging: deep keyset page (pages 100–180)", r => "/api/v1/products?limit=50&cursor=" + Uri.EscapeDataString(deepCursors[r % deepCursors.Count])),
        };

        var results = new List<(string Name, double P50, double P95, double Max)>();
        foreach (var (name, url) in scenarios)
        {
            var samples = new List<double>();
            for (var run = 0; run < Warmup + Runs; run++)
            {
                var watch = Stopwatch.StartNew();
                var response = await owner.GetAsync(url(run));
                await response.Content.ReadAsByteArrayAsync(Ct);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{name}: GET {url(run)} → {(int)response.StatusCode}");
                if (run >= Warmup)
                {
                    samples.Add(elapsed);
                }
            }

            results.Add((name, Percentile(samples, 50), Percentile(samples, 95), samples.Max()));
        }

        Report(catalog, results);
        var over = results.Where(r => r.P95 >= BudgetMs).Select(r => $"{r.Name}: p95 {r.P95:F1} ms").ToList();
        Assert.True(over.Count == 0, $"p95 budget {BudgetMs} ms exceeded:\n{string.Join('\n', over)}\n\nAll results:\n{Table(results)}");
    }

    [Fact]
    public async Task Keyset_paging_returns_every_product_exactly_once()
    {
        var catalog = await CatalogAsync();
        var owner = catalog.Owner;
        var listed = await catalog.IdsAsync(fixture, "SELECT id FROM catalog.products WHERE tenant_id = $1 AND status IN ('active', 'inactive')");
        var all = await catalog.IdsAsync(fixture, "SELECT id FROM catalog.products WHERE tenant_id = $1");
        var variants = await catalog.IdsAsync(fixture, "SELECT id FROM catalog.product_variants WHERE tenant_id = $1 AND status IN ('active', 'inactive')");
        Assert.Equal(SyntheticCatalog.ProductCount, all.Count);
        Assert.True(variants.Count > 12_000, $"{variants.Count} variants");

        // Default order (-createdAt, id): timestamps repeat in groups of three, so the id tie-break is exercised.
        await AssertExactlyOnceAsync(owner, "/api/v1/products?limit=200", listed);
        await AssertExactlyOnceAsync(owner, "/api/v1/products?sort=name&limit=97", listed);
        await AssertExactlyOnceAsync(owner, "/api/v1/products?status=active,inactive,archived&limit=200", all);
        await AssertExactlyOnceAsync(owner, "/api/v1/variants?limit=200", variants);
    }

    [Fact]
    public async Task Search_and_filters_return_exactly_the_matching_products()
    {
        var catalog = await CatalogAsync();
        var owner = catalog.Owner;
        const string Active = "status IN ('active', 'inactive')";

        await AssertExactlyOnceAsync(owner, "/api/v1/products?q=Balata&limit=200", await catalog.IdsAsync(fixture,
            $"SELECT id FROM catalog.products WHERE tenant_id = $1 AND {Active} AND name ILIKE '%balata%'"));
        await AssertExactlyOnceAsync(owner, "/api/v1/products?q=MTR-012&limit=200", await catalog.IdsAsync(fixture,
            $"SELECT p.id FROM catalog.products p WHERE p.tenant_id = $1 AND p.{Active} AND EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v.tenant_id = p.tenant_id AND v.product_id = p.id AND v.sku ILIKE '%MTR-012%')"));
        await AssertExactlyOnceAsync(owner, "/api/v1/products?q=" + SyntheticCatalog.Barcode(4244, 2), await catalog.IdsAsync(fixture,
            $"SELECT p.id FROM catalog.products p JOIN catalog.product_variants v ON v.tenant_id = p.tenant_id AND v.product_id = p.id WHERE p.tenant_id = $1 AND p.{Active} AND v.barcode = $2",
            SyntheticCatalog.Barcode(4244, 2)));
        await AssertExactlyOnceAsync(owner, $"/api/v1/products?categoryId={catalog.LedRoot}&limit=200", await catalog.IdsAsync(fixture,
            $"SELECT p.id FROM catalog.products p JOIN catalog.categories c ON c.tenant_id = p.tenant_id AND c.id = p.category_id WHERE p.tenant_id = $1 AND p.{Active} AND c.path LIKE (SELECT path FROM catalog.categories WHERE id = $2) || '%'",
            catalog.LedRoot));
        await AssertExactlyOnceAsync(owner, "/api/v1/products?attr.voltaj=24V&limit=200", await catalog.IdsAsync(fixture,
            $"SELECT id FROM catalog.products WHERE tenant_id = $1 AND {Active} AND attributes @> '{{\"voltaj\": \"24V\"}}'"));
        await AssertExactlyOnceAsync(owner, "/api/v1/products?attr.guc_w.min=80&limit=200", await catalog.IdsAsync(fixture,
            $"SELECT id FROM catalog.products WHERE tenant_id = $1 AND {Active} AND (attributes ->> 'guc_w')::int >= 80"));
        await AssertExactlyOnceAsync(owner, "/api/v1/products?attr.renk=Mavi&limit=200", await catalog.IdsAsync(fixture,
            $"SELECT p.id FROM catalog.products p WHERE p.tenant_id = $1 AND p.{Active} AND EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v.tenant_id = p.tenant_id AND v.product_id = p.id AND v.status <> 'archived' AND v.attributes @> '{{\"renk\": \"Mavi\"}}')"));

        var lookup = await owner.GetAsync("/api/v1/variants/lookup?barcode=" + SyntheticCatalog.Barcode(777));
        using var variant = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(Ct));
        Assert.Equal(SyntheticCatalog.Sku(777), variant.RootElement.GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Permissions_and_tenant_isolation_hold_on_the_large_catalog()
    {
        var catalog = await CatalogAsync();
        var someProduct = (await catalog.IdsAsync(fixture, "SELECT id FROM catalog.products WHERE tenant_id = $1 ORDER BY id LIMIT 1"))[0];

        // Another tenant searching for the big tenant's names, SKUs and barcodes finds nothing.
        var other = await CatalogClient.NewTenantOwnerAsync(fixture, "general");
        foreach (var path in new[] { "/api/v1/products?q=Balata", "/api/v1/products?q=MTR-0", "/api/v1/products?q=" + SyntheticCatalog.Barcode(1), "/api/v1/variants?sku=" + SyntheticCatalog.Sku(1), "/api/v1/products?status=active,inactive,archived&limit=200" })
        {
            using var page = JsonDocument.Parse(await (await other.GetAsync(path)).Content.ReadAsStringAsync(Ct));
            Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/products/{someProduct}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/variants/lookup?barcode=" + SyntheticCatalog.Barcode(1))).StatusCode);
        await HttpAssert.ProblemAsync(await other.Targeting(catalog.TenantId).GetAsync("/api/v1/products?q=Balata"), HttpStatusCode.Forbidden, "tenant.access_denied");

        // Inside the big tenant, roles still decide (roles-permissions §3).
        var viewer = await catalog.Owner.AddMemberAsync(fixture, Roles.Viewer);
        var staff = await catalog.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        using (var page = JsonDocument.Parse(await (await viewer.GetAsync("/api/v1/products?q=Balata")).Content.ReadAsStringAsync(Ct)))
        {
            Assert.True(page.RootElement.GetProperty("items").GetArrayLength() > 0);
        }

        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/variants/lookup?barcode=" + SyntheticCatalog.Barcode(2))).StatusCode);
        await HttpAssert.ProblemAsync(await viewer.PostAsync("/api/v1/products", new { name = "X", sku = "X-1", taxRate = 20m }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await staff.GetAsync("/api/v1/price-lists"), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await viewer.PatchAsync($"/api/v1/products/{someProduct}", new { name = "X" }, CatalogClient.ETag(1)), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    private async Task<SyntheticCatalog> CatalogAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            return _catalog ??= await SyntheticCatalog.CreateAsync(fixture);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task AssertExactlyOnceAsync(CatalogClient client, string path, List<Guid> expected)
    {
        var (ids, _) = await PageAsync(client, path);
        var duplicates = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"{path}: {duplicates.Count} products returned more than once");
        var missing = expected.Except(ids).Count();
        var extra = ids.Except(expected).Count();
        Assert.True(missing == 0 && extra == 0, $"{path}: {ids.Count} returned, {expected.Count} expected — {missing} missing, {extra} unexpected");
        Assert.True(expected.Count > 0, $"{path}: the truth query matched nothing, the check proves nothing");
    }

    private static Task<(List<Guid> Ids, List<double> PageMs)> PageAsync(CatalogClient client, string path) =>
        SyntheticCatalog.PageAllAsync(client, path);

    private static async Task<List<string>> CursorsAsync(CatalogClient client, string path, int from, int to)
    {
        var cursors = new List<string>();
        string? cursor = null;
        for (var page = 1; page <= to; page++)
        {
            var url = cursor is null ? path : $"{path}&cursor={Uri.EscapeDataString(cursor)}";
            using var document = JsonDocument.Parse(await (await client.GetAsync(url)).Content.ReadAsStringAsync(Ct));
            cursor = document.RootElement.GetProperty("page").GetProperty("nextCursor").GetString();
            Assert.NotNull(cursor);
            if (page >= from)
            {
                cursors.Add(cursor);
            }
        }

        return cursors;
    }

    /// <summary>Nearest-rank percentile.</summary>
    private static double Percentile(List<double> samples, int percentile)
    {
        var sorted = samples.Order().ToList();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static string Table(List<(string Name, double P50, double P95, double Max)> results)
    {
        var builder = new StringBuilder("| Scenario | p50 ms | p95 ms | max ms |\n|---|---:|---:|---:|\n");
        foreach (var (name, p50, p95, max) in results)
        {
            builder.Append(CultureInfo.InvariantCulture, $"| {name} | {p50:F1} | {p95:F1} | {max:F1} |\n");
        }

        return builder.ToString();
    }

    private static void Report(SyntheticCatalog catalog, List<(string Name, double P50, double P95, double Max)> results)
    {
        var report = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"### Catalog performance — {SyntheticCatalog.ProductCount:N0} synthetic products\n\n")
            .Append(CultureInfo.InvariantCulture, $"{Runs} measured runs per scenario after {Warmup} warm-up runs, in-process HTTP → API → PostgreSQL 17 (Testcontainers). ")
            .Append(CultureInfo.InvariantCulture, $"Bulk load: {catalog.LoadTime.TotalSeconds:F1} s. Budget: p95 < {BudgetMs} ms.\n\n")
            .Append(Table(results))
            .ToString();

        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Console.WriteLine(report);

        // On GitHub Actions the table lands in the job summary of the API workflow (no workflow change needed).
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            File.AppendAllText(summary, report + "\n");
        }
    }
}

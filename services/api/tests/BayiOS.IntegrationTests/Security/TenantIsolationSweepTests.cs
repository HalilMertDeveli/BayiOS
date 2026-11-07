using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// Tenant isolation matrix over every endpoint of the running API (test-strategy §4 #8, threat-model "tenant escape" /
/// IDOR, #15). Routes come from <c>/openapi/v1.json</c>: a new endpoint is swept automatically, and one the sweep
/// cannot build a probe for fails until it is classified here or in <see cref="TenantWorld.SegmentKinds"/> /
/// <see cref="SecurityProbes.TryBody"/>. Tenant A is the victim, B the attacker; both are complete tenants built through
/// the API, and A's rows in every tenant table are snapshotted before and after each attack; any difference is
/// reported row by row (table, primary key, changed columns).
/// <para>
/// The class runs in a non-parallel collection: other tests legitimately run global actors over the shared database —
/// <c>JobsTests</c> drives an <c>OutboxDispatcher</c> that marks every tenant's pending outbox messages processed
/// (<c>attempts</c>, <c>processed_at</c>), including the ones A's order approvals published. Run concurrently, that
/// changed A's snapshot and failed the sweep intermittently in CI (#103, #106) although no probe wrote anything.
/// Isolating the class keeps the check exact instead of excluding any table or column.
/// </para>
/// </summary>
[Collection(TenantSnapshotTestGroup.Name)]
[Trait("Category", "Security")]
public sealed class TenantIsolationSweepTests(ApiFixture fixture)
{
    /// <summary>
    /// Id-routes the IDOR sweep does not probe, with the reason. Everything else with a path parameter is probed.
    /// </summary>
    private static readonly Dictionary<string, string> NotIdorTargets = new()
    {
        ["POST /api/v1/sector-templates/{code}/apply"] = "{code} names a global, read-only sector template, not a tenant resource",
        ["POST /api/v1/billing/modules/{code}/activate"] = "{code} names a global catalog add-on; the change applies to the caller's own tenant only (BillingApiTests)",
        ["POST /api/v1/billing/modules/{code}/deactivate"] = "{code} names a global catalog add-on; the change applies to the caller's own tenant only (BillingApiTests)",
    };

    /// <summary>Query parameters that carry a resource id → the kind of TenantWorld resource to put there.</summary>
    private static readonly Dictionary<string, string> IdQueryParameters = new()
    {
        ["categoryId"] = "category",
        ["brandId"] = "brand",
        ["productId"] = "product",
        ["variantId"] = "variant",
        ["variantIds"] = "variant",
        ["ids"] = "variant",
        ["warehouseId"] = "warehouse",
        ["locationId"] = "location",
        ["customerId"] = "customer",
        ["priceListId"] = "priceList",
        ["dealerGroupId"] = "dealerGroup",
        ["orderId"] = "order",
        ["entityId"] = "product",
        ["actorUserId"] = "owner",
        ["sourceId"] = "receipt",
    };

    private static readonly HttpStatusCode[] Hidden = [HttpStatusCode.NotFound, HttpStatusCode.Forbidden];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_id_route_lets_tenant_B_read_or_change_tenant_A_resources()
    {
        var routes = await ApiRoutes.LoadAsync(fixture);
        var a = await TenantWorld.CreateAsync(fixture, "IDA");
        var b = await TenantWorld.CreateAsync(fixture, "IDB");

        // A's owner is also a member of B: with B's header, A's ids must still be invisible (the header selects the
        // tenant; the user's other memberships grant nothing).
        await SecurityProbes.AddMembershipAsync(fixture, b.TenantId, a.Owner.UserId, Roles.Owner);
        var attackers = new (string Name, CatalogClient Client)[] { ("B owner", b.Owner), ("A owner via B header", a.Owner.Targeting(b.TenantId)) };

        var before = await a.SnapshotAsync();
        var failures = new List<string>();
        var probed = 0;
        foreach (var route in routes.Where(r => r.IsTenantScoped && r.PathParameters.Count > 0))
        {
            if (NotIdorTargets.ContainsKey(route.Key))
            {
                continue;
            }

            var unresolved = new List<string>();
            string Victim(string segment, string name) => Resolve(a, segment, name, unresolved);
            var path = ApiRoutes.Fill(route.Template, Victim);
            if (unresolved.Count > 0)
            {
                failures.Add($"{route}: cannot build a probe for {string.Join(", ", unresolved)} — add the collection to TenantWorld.SegmentKinds or NotIdorTargets");
                continue;
            }

            if (!SecurityProbes.TryBody(route, a, b, out var body))
            {
                failures.Add($"{route}: add a request body to SecurityProbes.TryBody");
                continue;
            }

            var kinds = new List<string>();
            _ = ApiRoutes.Fill(route.Template, (segment, _) => { kinds.Add(KindOf(segment)!); return string.Empty; });

            // If-Match carries the current version of what the path addresses, so a probe is never stopped by a stale ETag.
            var paths = new List<(string Path, string ETag)> { (path, a.ETag(kinds)) };
            if (route.PathParameters.Count > 1)
            {
                // Own parent, foreign child: /customers/{B}/addresses/{A}.
                var lastParameter = route.PathParameters[^1];
                paths.Add((
                    ApiRoutes.Fill(route.Template, (segment, name) => name == lastParameter ? Victim(segment, name) : Resolve(b, segment, name, unresolved)),
                    a.HasVersion(kinds[^1]) ? a.ETag(kinds[^1]) : b.ETag(kinds[0])));
            }

            foreach (var (attacker, client) in attackers)
            {
                foreach (var (probe, etag) in paths)
                {
                    probed++;
                    var response = await SecurityProbes.SendAsync(fixture, client, route, probe, body, etag);
                    var text = await response.Content.ReadAsStringAsync(Ct);
                    if (!Hidden.Contains(response.StatusCode))
                    {
                        failures.Add($"{route} as {attacker}, {probe} → {(int)response.StatusCode} (expected 404/403): {Trim(text)}");
                    }

                    var leaks = SecurityProbes.Leaks(text, probe, a);
                    if (leaks.Count > 0)
                    {
                        failures.Add($"{route} as {attacker}: response contains tenant A data ({string.Join(", ", leaks)})");
                    }
                }
            }

            if (route.Method == "GET")
            {
                // Positive control: the same path read by its owner must find the resource, or the probe proved nothing.
                var control = await a.Owner.GetAsync(path);
                if (control.StatusCode == HttpStatusCode.NotFound)
                {
                    failures.Add($"{route}: control as A's owner → 404, the probe does not address a real resource");
                }
            }
        }

        failures.AddRange(StaleEntries(NotIdorTargets.Keys, routes));
        SecurityProbes.AssertNone(failures);
        SecurityProbes.AssertNone(before.Diff(await a.SnapshotAsync()));
        Assert.True(probed >= 100, $"Only {probed} probes ran — is the OpenAPI scan broken?");
        TestContext.Current.TestOutputHelper?.WriteLine($"IDOR sweep: {probed} probes over {routes.Count(r => r.IsTenantScoped && r.PathParameters.Count > 0) - NotIdorTargets.Count} id routes.");
    }

    [Fact]
    public async Task Tenant_B_lists_and_searches_never_return_tenant_A_data()
    {
        var routes = await ApiRoutes.LoadAsync(fixture);
        var a = await TenantWorld.CreateAsync(fixture, "LSA");
        var b = await TenantWorld.CreateAsync(fixture, "LSB");

        var failures = new List<string>();
        foreach (var route in routes.Where(r => r.IsTenantScoped && r.Method == "GET" && r.PathParameters.Count == 0))
        {
            var queries = new List<string> { string.Empty };
            foreach (var parameter in route.QueryParameters)
            {
                var value = parameter switch
                {
                    "q" => a.Token,
                    "sku" => a.Sku,
                    "barcode" => a.Barcode,
                    _ when IdQueryParameters.TryGetValue(parameter, out var kind) => a.Id(kind).ToString(),
                    _ when parameter.EndsWith("Id", StringComparison.Ordinal) => null,
                    _ => string.Empty,
                };
                if (value is null)
                {
                    failures.Add($"{route}: id query parameter '{parameter}' is not mapped — add it to IdQueryParameters");
                }
                else if (value.Length > 0)
                {
                    queries.Add($"?{parameter}={Uri.EscapeDataString(value)}");
                }
            }

            foreach (var query in queries)
            {
                var path = route.Template + query;
                var response = await b.Owner.GetAsync(path);
                var text = await response.Content.ReadAsStringAsync(Ct);
                var acceptable = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound
                    || (query.Length == 0 && response.StatusCode == HttpStatusCode.BadRequest); // e.g. lookup without its required filter
                if (!acceptable)
                {
                    failures.Add($"GET {path} as B → {(int)response.StatusCode}: {Trim(text)}");
                }

                var leaks = SecurityProbes.Leaks(text, path, a);
                if (leaks.Count > 0)
                {
                    failures.Add($"GET {path} as B: response contains tenant A data ({string.Join(", ", leaks)})");
                }
            }
        }

        SecurityProbes.AssertNone(failures);
    }

    [Fact]
    public async Task Tenant_B_cannot_reference_tenant_A_resources_in_request_bodies()
    {
        var a = await TenantWorld.CreateAsync(fixture, "RFA");
        var b = await TenantWorld.CreateAsync(fixture, "RFB");
        var before = await a.SnapshotAsync();
        string Hack() => "HACK-" + Guid.NewGuid().ToString("N")[..8];

        // Each probe is valid with B's own ids; exactly one reference points into tenant A.
        var probes = new (string Name, HttpMethod Method, string Path, object Body, string? IfMatch)[]
        {
            ("product with A's unit", HttpMethod.Post, "/api/v1/products", new { name = Hack(), sku = Hack(), taxRate = 20m, defaultUnitId = a.Id("unit") }, null),
            ("product with A's category", HttpMethod.Post, "/api/v1/products", new { name = Hack(), sku = Hack(), taxRate = 20m, defaultUnitId = b.Id("unit"), categoryId = a.Id("category") }, null),
            ("product with A's brand", HttpMethod.Post, "/api/v1/products", new { name = Hack(), sku = Hack(), taxRate = 20m, defaultUnitId = b.Id("unit"), brandId = a.Id("brand") }, null),
            ("move B's product into A's category", HttpMethod.Patch, $"/api/v1/products/{b.Id("product")}", new { categoryId = a.Id("category") }, b.ETag("product")),
            ("variant with A's unit", HttpMethod.Post, $"/api/v1/products/{b.Id("product")}/variants", new { sku = Hack(), unitId = a.Id("unit") }, null),
            ("receipt into A's warehouse", HttpMethod.Post, "/api/v1/inventory/receipts", new { warehouseId = a.Id("warehouse"), reference = Hack(), lines = new[] { new { variantId = b.Id("variant"), quantity = 1m, locationId = (Guid?)a.Id("location") } } }, null),
            ("receipt of A's variant", HttpMethod.Post, "/api/v1/inventory/receipts", new { warehouseId = b.Id("warehouse"), reference = Hack(), lines = new[] { new { variantId = a.Id("variant"), quantity = 1m, locationId = (Guid?)b.Id("location") } } }, null),
            ("receipt into A's location", HttpMethod.Post, "/api/v1/inventory/receipts", new { warehouseId = b.Id("warehouse"), reference = Hack(), lines = new[] { new { variantId = b.Id("variant"), quantity = 1m, locationId = (Guid?)a.Id("location") } } }, null),
            ("adjust A's variant", HttpMethod.Post, "/api/v1/inventory/adjustments", new { variantId = a.Id("variant"), warehouseId = b.Id("warehouse"), locationId = b.Id("location"), type = "increase", quantity = 1m, reason = Hack() }, null),
            ("adjust in A's warehouse", HttpMethod.Post, "/api/v1/inventory/adjustments", new { variantId = b.Id("variant"), warehouseId = a.Id("warehouse"), locationId = a.Id("location"), type = "increase", quantity = 1m, reason = Hack() }, null),
            ("threshold for A's variant", HttpMethod.Post, "/api/v1/inventory/thresholds", new { variantId = a.Id("variant"), minQuantity = 1m }, null),
            ("threshold in A's warehouse", HttpMethod.Post, "/api/v1/inventory/thresholds", new { variantId = b.Id("variant"), warehouseId = a.Id("warehouse"), minQuantity = 1m }, null),
            ("bulk threshold for A's variant", HttpMethod.Post, "/api/v1/inventory/thresholds/bulk", new { items = new[] { new { variantId = a.Id("variant"), minQuantity = 1m } } }, null),
            ("bulk removal of A's threshold", HttpMethod.Post, "/api/v1/inventory/thresholds/bulk", new { items = new object[] { new { variantId = a.Id("variant"), warehouseId = a.Id("warehouse"), remove = true } } }, null),
            ("price A's variant on B's list", HttpMethod.Put, $"/api/v1/price-lists/{b.Id("priceList")}/items", new { items = new[] { new { variantId = a.Id("variant"), minQuantity = 1m, unitPrice = 1m } } }, null),
            ("price list based on A's list", HttpMethod.Post, "/api/v1/price-lists", new { name = Hack(), basePriceListId = a.Id("priceList"), discountPercent = 10m }, null),
            ("dealer group on A's list", HttpMethod.Post, "/api/v1/dealer-groups", new { name = Hack(), priceListId = a.Id("priceList") }, null),
            ("customer in A's dealer group", HttpMethod.Post, "/api/v1/customers", new { code = Hack()[..10], name = Hack(), dealerGroupId = a.Id("dealerGroup") }, null),
            ("customer on A's price list", HttpMethod.Post, "/api/v1/customers", new { code = Hack()[..10], name = Hack(), priceListId = a.Id("priceList") }, null),
            ("move B's customer to A's group", HttpMethod.Patch, $"/api/v1/customers/{b.Id("customer")}", new { dealerGroupId = a.Id("dealerGroup") }, b.ETag("customer")),
            ("quote for A's customer", HttpMethod.Post, "/api/v1/pricing/quote", new { customerId = a.Id("customer"), lines = new[] { new { variantId = b.Id("variant"), quantity = 1m } } }, null),
            ("quote for A's variant", HttpMethod.Post, "/api/v1/pricing/quote", new { customerId = b.Id("customer"), lines = new[] { new { variantId = a.Id("variant"), quantity = 1m } } }, null),
            ("order for A's customer", HttpMethod.Post, "/api/v1/orders", new { customerId = a.Id("customer"), warehouseId = b.Id("warehouse"), lines = new[] { new { variantId = b.Id("variant"), quantity = 1m } } }, null),
            ("order from A's warehouse", HttpMethod.Post, "/api/v1/orders", new { customerId = b.Id("customer"), warehouseId = a.Id("warehouse"), lines = new[] { new { variantId = b.Id("variant"), quantity = 1m } } }, null),
            ("order of A's variant", HttpMethod.Post, "/api/v1/orders", new { customerId = b.Id("customer"), warehouseId = b.Id("warehouse"), lines = new[] { new { variantId = a.Id("variant"), quantity = 1m, unitPrice = (decimal?)10m } } }, null),
            ("add A's variant to B's draft", HttpMethod.Patch, $"/api/v1/orders/{b.Id("order")}", new { lines = new object[] { new { variantId = a.Id("variant"), quantity = 1m, unitPrice = 10m } } }, b.ETag("order")),
            ("transfer B's ownership to A's member", HttpMethod.Post, "/api/v1/members/ownership-transfer", new { userId = a.Id("member") }, null),
            ("import mapping onto A's list", HttpMethod.Put, $"/api/v1/import-jobs/{b.Id("importJob")}/mapping", new { columns = new Dictionary<string, string> { ["sku"] = "Stok Kodu", ["name"] = "Ürün Adı", ["unit"] = "Birim", ["taxRate"] = "KDV" }, priceListId = a.Id("priceList") }, null),
        };

        var failures = new List<string>();
        foreach (var (name, method, path, body, ifMatch) in probes)
        {
            var response = await b.Owner.SendAsync(method, path, body, ifMatch, method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json", Guid.NewGuid().ToString());
            var text = await response.Content.ReadAsStringAsync(Ct);
            var status = (int)response.StatusCode;
            if (status is < 400 or >= 500 || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                failures.Add($"{name}: {method} {path} → {status} (expected a 4xx rejection): {Trim(text)}");
            }

            var leaks = SecurityProbes.Leaks(text, path, a);
            if (leaks.Count > 0)
            {
                failures.Add($"{name}: response contains tenant A data ({string.Join(", ", leaks)})");
            }
        }

        SecurityProbes.AssertNone(failures);
        SecurityProbes.AssertNone(before.Diff(await a.SnapshotAsync()));
    }

    [Fact]
    public async Task X_Tenant_Id_is_a_selection_not_a_grant_on_every_tenant_scoped_route()
    {
        var routes = await ApiRoutes.LoadAsync(fixture);
        var a = await TenantWorld.CreateAsync(fixture, "SPA");
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        var noTenant = Guid.NewGuid();
        var invited = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        await SecurityProbes.AddMembershipAsync(fixture, a.TenantId, invited, Roles.Owner, "invited");
        await SecurityProbes.AddMembershipAsync(fixture, a.TenantId, disabled, Roles.Owner, "disabled");

        var callers = new (string Name, CatalogClient Client)[]
        {
            ("owner of another tenant", b.Targeting(a.TenantId)),
            ("user without any tenant", new CatalogClient(fixture.CreateClientFor(noTenant), a.TenantId, noTenant)),
            ("invited (not active) owner", new CatalogClient(fixture.CreateClientFor(invited), a.TenantId, invited)),
            ("disabled owner", new CatalogClient(fixture.CreateClientFor(disabled), a.TenantId, disabled)),
            ("owner of another tenant, unknown tenant id", b.Targeting(Guid.NewGuid())),
        };

        var before = await a.SnapshotAsync();
        var failures = new List<string>();
        var tenantScoped = routes.Where(r => r.IsTenantScoped).ToList();
        foreach (var route in tenantScoped)
        {
            var path = ApiRoutes.Fill(route.Template, (segment, name) =>
                segment == "sector-templates" ? "motorcycle_parts" : Resolve(a, segment, name, []));
            _ = SecurityProbes.TryBody(route, a, a, out var body);
            foreach (var (name, client) in callers)
            {
                var response = await SecurityProbes.SendAsync(fixture, client, route, path, body ?? new { }, a.ETag("product"));
                var code = await SecurityProbes.CodeAsync(response);
                if (response.StatusCode != HttpStatusCode.Forbidden || code != "tenant.access_denied")
                {
                    failures.Add($"{route} as {name} → {(int)response.StatusCode} {code} (expected 403 tenant.access_denied)");
                }
            }
        }

        SecurityProbes.AssertNone(failures);

        // #89: the one expected change in A is its access-denial audit — exactly one auth.membership_denied row per caller
        // that named A (deduplicated over every route), written on its own transaction. Nothing else of A changed; the
        // unknown tenant id wrote nothing anywhere.
        var diff = before.Diff(await a.SnapshotAsync());
        SecurityProbes.AssertNone([.. diff.Where(d => !(d.StartsWith("audit.audit_events [", StringComparison.Ordinal)
            && d.Contains("] inserted: ", StringComparison.Ordinal)
            && d.Contains("\"action\": \"auth.membership_denied\"", StringComparison.Ordinal)))]);
        Guid[] namedA = [b.UserId, noTenant, invited, disabled];
        Assert.Equal(namedA.Length, diff.Count);
        Assert.All(namedA, user => Assert.Single(diff, d => d.Contains($"\"actor_user_id\": \"{user}\"", StringComparison.Ordinal)));
        Assert.True(tenantScoped.Count >= 90, $"Only {tenantScoped.Count} tenant-scoped routes found");
    }

    [Fact]
    public async Task Every_route_except_the_public_ones_requires_a_token()
    {
        string[] anonymous = ["GET /api/v1/meta"];
        var routes = await ApiRoutes.LoadAsync(fixture);
        using var client = fixture.CreateClient();
        var failures = new List<string>();
        foreach (var route in routes.Where(r => !anonymous.Contains(r.Key)))
        {
            using var request = new HttpRequestMessage(route.HttpMethod, ApiRoutes.Fill(route.Template, (_, _) => Guid.NewGuid().ToString()));
            request.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString());
            request.Headers.Add(IdempotentRequests.Header, Guid.NewGuid().ToString());
            var response = await client.SendAsync(request, Ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{route} without a token → {(int)response.StatusCode}");
            }
        }

        failures.AddRange(StaleEntries(anonymous, routes));
        SecurityProbes.AssertNone(failures);
    }

    private static string Resolve(TenantWorld world, string segment, string name, List<string> unresolved)
    {
        if (KindOf(segment) is { } kind && world.Ids.ContainsKey(kind))
        {
            return world.Id(kind).ToString();
        }

        unresolved.Add($"{{{name}}} after '{segment}'");
        return Guid.Empty.ToString();
    }

    private static string? KindOf(string collection) =>
        TenantWorld.SegmentKinds.TryGetValue(collection, out var kind) || TenantWorld.SegmentKinds.TryGetValue(collection.Split('/')[^1], out kind) ? kind : null;

    private static IEnumerable<string> StaleEntries(IEnumerable<string> keys, IReadOnlyList<ApiRoute> routes) =>
        keys.Where(k => routes.All(r => r.Key != k)).Select(k => $"{k}: listed as an exception but no longer exists — remove it");

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

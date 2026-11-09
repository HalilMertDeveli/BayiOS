using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Pricing;

/// <summary>Price lists, items and price resolution over HTTP (#78, #31): order, tiers, base discount, no-price, money precision.</summary>
public class PricingApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Price_list_crud_with_default_per_currency_validation_and_etags()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var owner = kit.Owner;

        var retail = await kit.PriceListAsync("Perakende");
        Assert.Equal(("TRY", true, "sales", false, 1L), (retail.Currency, retail.IsDefault, retail.Kind, retail.PricesIncludeTax, retail.Version));
        var dealerA = await kit.PriceListAsync("Bayi A");
        Assert.False(dealerA.IsDefault);
        // #102: an explicit false on create means "not the default", it is not rejected.
        var explicitNotDefault = await kit.PriceListAsync("Bayi C", isDefault: false);
        Assert.False(explicitNotDefault.IsDefault);
        var dealerB = await kit.PriceListAsync("Bayi B", retail.Id, 12.5m);
        Assert.Equal((retail.Id, 12.5m), (dealerB.BasePriceListId!.Value, dealerB.DiscountPercent!.Value));

        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/price-lists", new { name = "Perakende" }), HttpStatusCode.Conflict, "pricing.name_taken");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/price-lists", new { name = "X", basePriceListId = retail.Id }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/price-lists", new { name = "X", basePriceListId = retail.Id, discountPercent = 100.001m }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/price-lists", new { name = "Zincir", basePriceListId = dealerB.Id, discountPercent = 5m }),
            HttpStatusCode.Conflict,
            "pricing.base_chain");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/price-lists", new { name = "KDV dahil", basePriceListId = retail.Id, discountPercent = 5m, pricesIncludeTax = true }),
            HttpStatusCode.Conflict,
            "pricing.tax_mode_mismatch");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync("/api/v1/price-lists", new { name = "Dolar", currency = "USD", basePriceListId = retail.Id, discountPercent = 5m }),
            HttpStatusCode.Conflict,
            "pricing.currency_mismatch");

        // Another currency gets its own default; moving the TRY default keeps exactly one.
        var usdList = await owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "USD", currency = "usd" });
        Assert.Equal(("USD", true), (usdList.Currency, usdList.IsDefault));

        var moved = await owner.OkAsync<PriceListResponse>(await owner.PatchAsync($"/api/v1/price-lists/{dealerA.Id}", new { isDefault = true }, "W/\"1\""));
        Assert.True(moved.IsDefault);
        var lists = await owner.OkAsync<PriceListListResponse>(await owner.GetAsync("/api/v1/price-lists"));
        Assert.Single(lists.Items, l => l.IsDefault && l.Currency == "TRY");
        Assert.Equal(dealerA.Id, lists.Items[0].Id);

        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/price-lists/{dealerA.Id}", new { status = "inactive" }, "W/\"2\""), HttpStatusCode.Conflict, "pricing.default_inactive");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/price-lists/{retail.Id}", new { status = "archived" }, "W/\"2\""), HttpStatusCode.Conflict, "pricing.list_in_use");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/price-lists/{retail.Id}", new { name = "X" }, "W/\"1\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        await HttpAssert.ProblemAsync(
            await owner.PatchAsync($"/api/v1/price-lists/{retail.Id}", new { name = "X" }, null), HttpStatusCode.PreconditionRequired, "precondition.required");

        // Merge patch: null clears the rule; validity window round-trips as dates.
        var cleared = await owner.OkAsync<PriceListResponse>(await owner.PatchAsync(
            $"/api/v1/price-lists/{dealerB.Id}", new { basePriceListId = (Guid?)null, discountPercent = (decimal?)null, validFrom = "2026-01-01", validTo = "2026-12-31" }, "W/\"1\""));
        Assert.Equal(((Guid?)null, (decimal?)null, new DateOnly(2026, 1, 1), 2L), (cleared.BasePriceListId, cleared.DiscountPercent, cleared.ValidFrom!.Value, cleared.Version));
        Assert.Equal("W/\"2\"", (await owner.GetAsync($"/api/v1/price-lists/{dealerB.Id}")).Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Item_upsert_is_keyed_by_variant_and_tier_with_keyset_paging_and_delete()
    {
        var kit = await PricingKit.CreateAsync(fixture, "led_electronics");
        var list = await kit.PriceListAsync("Perakende");
        var p10 = await kit.VariantAsync("LED-P10-RGB");
        var driver = await kit.VariantAsync("LED-DRV-12V");

        var first = await kit.ItemsAsync(list.Id, (p10, 1m, 450m), (p10, 10m, 425.5m), (driver, 1m, 89.9m));
        Assert.Equal((3, 0), (first.Created, first.Updated));
        var second = await kit.ItemsAsync(list.Id, (p10, 10m, 420m), (p10, 1m, 450m));
        Assert.Equal((0, 1), (second.Created, second.Updated));

        var page1 = await kit.Owner.OkAsync<PriceListItemPageResponse>(await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items?limit=2"));
        Assert.Equal((2, true), (page1.Items.Count, page1.Page.HasMore));
        var page2 = await kit.Owner.OkAsync<PriceListItemPageResponse>(
            await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items?limit=2&cursor={page1.Page.NextCursor}"));
        Assert.Equal((1, false, (string?)null), (page2.Items.Count, page2.Page.HasMore, page2.Page.NextCursor));
        Assert.Equal(3, page1.Items.Concat(page2.Items).Select(i => i.Id).Distinct().Count());
        var filtered = await kit.Owner.OkAsync<PriceListItemPageResponse>(await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items?variantId={p10}"));
        Assert.Equal([420m, 450m], filtered.Items.Select(i => i.UnitPrice).Order());

        // Validation: unknown variant, too many decimals, duplicate key, negative price.
        var bad = await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/price-lists/{list.Id}/items", new
        {
            items = new object[]
            {
                new { variantId = Guid.NewGuid(), unitPrice = 1m },
                new { variantId = p10, unitPrice = 1.00001m },
                new { variantId = driver, minQuantity = 5m, unitPrice = 1m },
                new { variantId = driver, minQuantity = 5.000m, unitPrice = 2m },
                new { variantId = driver, minQuantity = 2m, unitPrice = -1m },
            },
        }, idempotencyKey: Guid.NewGuid().ToString());
        await HttpAssert.ProblemAsync(bad, HttpStatusCode.UnprocessableEntity, "validation.failed");
        using (var body = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(Ct)))
        {
            var errors = body.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty("items[1].unitPrice", out _));
            Assert.True(errors.TryGetProperty("items[3]", out _));
            Assert.True(errors.TryGetProperty("items[4].unitPrice", out _));
        }

        var item = filtered.Items.First(i => i.MinQuantity == 10m);
        Assert.Equal(HttpStatusCode.NoContent, (await kit.Owner.DeleteAsync($"/api/v1/price-lists/{list.Id}/items/{item.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kit.Owner.DeleteAsync($"/api/v1/price-lists/{list.Id}/items/{item.Id}", null)).StatusCode);
        await HttpAssert.ProblemAsync(
            await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items?cursor=nope"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Creates_and_item_upserts_are_idempotent()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var key = Guid.NewGuid().ToString();

        var first = await kit.Owner.PostAsync("/api/v1/price-lists", new { name = "Perakende" }, key);
        var replay = await kit.Owner.PostAsync("/api/v1/price-lists", new { name = "Perakende" }, key);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, replay.StatusCode));
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        var created = (await first.Content.ReadFromJsonAsync<PriceListResponse>(Ct))!;
        Assert.Equal(created.Id, (await replay.Content.ReadFromJsonAsync<PriceListResponse>(Ct))!.Id);
        await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync("/api/v1/price-lists", new { name = "Başka" }, key), HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");

        var variant = await kit.VariantAsync();
        var itemsKey = Guid.NewGuid().ToString();
        var body = new { items = new[] { new { variantId = variant, minQuantity = 1m, unitPrice = 10m } } };
        var put1 = await kit.Owner.OkAsync<UpsertPriceListItemsResponse>(
            await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/price-lists/{created.Id}/items", body, idempotencyKey: itemsKey));
        var put2 = await kit.Owner.OkAsync<UpsertPriceListItemsResponse>(
            await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/price-lists/{created.Id}/items", body, idempotencyKey: itemsKey));
        Assert.Equal((1, 1), (put1.Created, put2.Created));
        Assert.Single((await kit.Owner.OkAsync<PriceListItemPageResponse>(await kit.Owner.GetAsync($"/api/v1/price-lists/{created.Id}/items"))).Items);
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/price-lists/{created.Id}/items", body), HttpStatusCode.PreconditionRequired, "precondition.required");
    }

    [Fact]
    public async Task Quote_resolves_customer_then_group_then_default_with_tiers_and_base_discount()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var balata = await kit.VariantAsync("FRN-CG125-ON");
        var zincir = await kit.VariantAsync("ZNC-428H-118L");
        var filtre = await kit.VariantAsync("FLT-YAG-HF204");
        var orphan = await kit.VariantAsync("NO-PRICE-1");

        var retail = await kit.PriceListAsync("Perakende");
        var dealerA = await kit.PriceListAsync("Bayi A");
        var dealerB = await kit.PriceListAsync("Bayi B", retail.Id, 12.5m);
        var special = await kit.PriceListAsync("Özel Müşteri");
        await kit.ItemsAsync(retail.Id, (balata, 1m, 1234.5678m), (zincir, 1m, 600m), (filtre, 1m, 150m));
        await kit.ItemsAsync(dealerA.Id, (balata, 1m, 1000m), (balata, 10m, 950m), (balata, 50m, 900m));
        await kit.ItemsAsync(dealerB.Id, (zincir, 1m, 500m));
        await kit.ItemsAsync(special.Id, (filtre, 1m, 99.99m));

        var groupA = await kit.GroupAsync("A Grubu", dealerA.Id, 5m);
        var groupB = await kit.GroupAsync("B Grubu", dealerB.Id);
        var customerA = await kit.CustomerAsync("C-A", groupA.Id);
        var customerB = await kit.CustomerAsync("C-B", groupB.Id, special.Id);
        var walkIn = await kit.CustomerAsync("C-0");

        // Group list with quantity tiers; missing items fall back to the default list; nothing at all → no_price.
        var a = await kit.QuoteAsync(customerA.Id, (balata, 9m), (balata, 10m), (balata, 50.5m), (zincir, 2m), (orphan, 1m));
        Assert.Equal(("TRY", 5m), (a.Currency, a.DealerGroupDiscountPercent!.Value));
        Assert.Equal([1000m, 950m, 900m, 600m, null], a.Lines.Select(l => l.UnitPrice));
        Assert.Equal(
            [PriceSources.DealerGroupPriceList, PriceSources.DealerGroupPriceList, PriceSources.DealerGroupPriceList, PriceSources.DefaultPriceList, null],
            a.Lines.Select(l => l.Source));
        Assert.Equal([1m, 10m, 50m, 1m, null], a.Lines.Select(l => l.MinQuantity));
        Assert.Equal((PriceStatuses.NoPrice, 20m), (a.Lines[4].Status, a.Lines[4].TaxRate));

        // Customer list first; the group's derived list: own item overrides, otherwise base − 12.5 %.
        var b = await kit.QuoteAsync(customerB.Id, (filtre, 1m), (zincir, 1m), (balata, 1m));
        Assert.Equal([99.99m, 500m, 1080.2468m], b.Lines.Select(l => l.UnitPrice));
        Assert.Equal([PriceSources.CustomerPriceList, PriceSources.DealerGroupPriceList, PriceSources.DealerGroupPriceList], b.Lines.Select(l => l.Source));
        Assert.Equal([PriceRules.Item, PriceRules.Item, PriceRules.BaseDiscount], b.Lines.Select(l => l.Rule));
        Assert.Equal((retail.Id, dealerB.Id, 1234.5678m, 12.5m), (b.Lines[2].ItemPriceListId!.Value, b.Lines[2].PriceListId!.Value, b.Lines[2].BaseUnitPrice!.Value, b.Lines[2].DiscountPercent!.Value));

        // No customer / no assignment: the tenant default list only.
        var anonymous = await kit.QuoteAsync(null, (balata, 1m));
        var plain = await kit.QuoteAsync(walkIn.Id, (balata, 1m));
        Assert.Equal((1234.5678m, PriceSources.DefaultPriceList), (anonymous.Lines[0].UnitPrice!.Value, anonymous.Lines[0].Source));
        Assert.Equal((1234.5678m, (decimal?)null), (plain.Lines[0].UnitPrice!.Value, plain.DealerGroupDiscountPercent));
    }

    [Fact]
    public async Task Money_is_exact_decimal_on_the_wire_and_missing_prices_are_null_never_zero()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var cheap = await kit.VariantAsync();
        var none = await kit.VariantAsync();
        var retail = await kit.PriceListAsync("Perakende");
        var dealer = await kit.PriceListAsync("Bayi", retail.Id, 33.33m);
        await kit.ItemsAsync(retail.Id, (variant, 1m, 0.1m), (cheap, 1m, 0m));
        await kit.ItemsAsync(dealer.Id, (variant, 0.001m, 0.3m));
        var group = await kit.GroupAsync("Bayi Grubu", dealer.Id);
        var customer = await kit.CustomerAsync(dealerGroupId: group.Id);

        var response = await kit.QuoteResponseAsync(kit.Owner, customer.Id, (variant, 0.001m), (cheap, 3m), (none, 1m));
        var json = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(json);
        var lines = body.RootElement.GetProperty("lines");

        // Decimal end to end: the wire carries the stored numeric(18,4) text, no binary floating point.
        Assert.Equal("0.3000", lines[0].GetProperty("unitPrice").GetRawText());
        Assert.Equal("0.001", lines[0].GetProperty("quantity").GetRawText());
        // A zero price is a real price (here derived from the base list: 0 × 66.67 %).
        Assert.Equal((0m, "priced"), (lines[1].GetProperty("unitPrice").GetDecimal(), lines[1].GetProperty("status").GetString()));
        // No list has the third variant: explicit no_price with a null unit price.
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("unitPrice").ValueKind);
        Assert.Equal("no_price", lines[2].GetProperty("status").GetString());

        var derived = await kit.QuoteAsync(customer.Id, (variant, 1m), (cheap, 1m));
        // The dealer list's own 0.001 tier wins for the first variant; the second derives from the base: 0 × 66.67 % = 0.
        Assert.Equal([0.3m, 0m], derived.Lines.Select(l => l.UnitPrice));
        Assert.Equal(PriceRules.BaseDiscount, derived.Lines[1].Rule);
    }

    [Fact]
    public async Task Quote_validates_lines_customer_and_currency()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();

        foreach (var body in new object[]
        {
            new { lines = Array.Empty<object>() },
            new { lines = new[] { new { variantId = Guid.NewGuid(), quantity = 1m } } },
            new { lines = new[] { new { variantId = variant, quantity = 0m } } },
            new { lines = new[] { new { variantId = variant, quantity = 1.0001m } } },
            new { customerId = Guid.NewGuid(), lines = new[] { new { variantId = variant, quantity = 1m } } },
            new { currency = "TL", lines = new[] { new { variantId = variant, quantity = 1m } } },
        })
        {
            await HttpAssert.ProblemAsync(
                await kit.Owner.SendAsync(HttpMethod.Post, "/api/v1/pricing/quote", body), HttpStatusCode.UnprocessableEntity, "validation.failed");
        }
    }

    [Fact]
    public async Task Resolver_contract_runs_inside_the_callers_unit_of_work()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var retail = await kit.PriceListAsync("Perakende", pricesIncludeTax: true);
        await kit.ItemsAsync(retail.Id, (variant, 1m, 240m));

        var result = await kit.ResolveInUnitOfWorkAsync(new PriceQuoteRequest(null, new DateOnly(2026, 11, 5), [new PriceQuoteLine(variant, 2m)]));

        Assert.True(result.Succeeded);
        var line = Assert.Single(result.Value!.Lines);
        Assert.Equal((240m, true, 20m, "TRY"), (line.UnitPrice!.Value, line.PricesIncludeTax!.Value, line.TaxRate, result.Value.Currency));

        // A list outside its validity window on the order date is skipped → explicit no-price.
        await kit.Owner.OkAsync<PriceListResponse>(await kit.Owner.PatchAsync($"/api/v1/price-lists/{retail.Id}", new { validFrom = "2027-01-01" }, "W/\"1\""));
        var later = await kit.ResolveInUnitOfWorkAsync(new PriceQuoteRequest(null, new DateOnly(2026, 11, 5), [new PriceQuoteLine(variant, 2m)]));
        Assert.Equal(PriceStatuses.NoPrice, Assert.Single(later.Value!.Lines).Status);
    }

    [Fact]
    public async Task Purchase_lists_are_hidden_without_unconditional_cost_permission()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var sales = await kit.PriceListAsync("Perakende");
        var purchase = await kit.PriceListAsync("Alış", kind: "purchase");
        Assert.False(purchase.IsDefault);
        var manager = await kit.Owner.AddMemberAsync(fixture, Roles.Manager);

        var visible = await manager.OkAsync<PriceListListResponse>(await manager.GetAsync("/api/v1/price-lists"));
        Assert.Equal([sales.Id], visible.Items.Select(l => l.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/price-lists/{purchase.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/price-lists/{purchase.Id}/items")).StatusCode);
        Assert.Equal(2, (await kit.Owner.OkAsync<PriceListListResponse>(await kit.Owner.GetAsync("/api/v1/price-lists"))).Items.Count);

        // A purchase list can never be assigned to a dealer group.
        await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync("/api/v1/dealer-groups", new { name = "G", priceListId = purchase.Id }), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.Viewer)]
    public async Task Roles_without_prices_write_can_read_and_quote_but_not_change(string role)
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var list = await kit.PriceListAsync("Perakende");
        var variant = await kit.VariantAsync();
        var member = await kit.Owner.AddMemberAsync(fixture, role);

        Assert.Single((await member.OkAsync<PriceListListResponse>(await member.GetAsync("/api/v1/price-lists"))).Items);
        Assert.Equal(HttpStatusCode.OK, (await kit.QuoteResponseAsync(member, null, (variant, 1m))).StatusCode);
        await HttpAssert.ProblemAsync(await member.PostAsync("/api/v1/price-lists", new { name = "X" }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await member.PatchAsync($"/api/v1/price-lists/{list.Id}", new { name = "X" }, "W/\"1\""), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(
            await member.SendAsync(HttpMethod.Put, $"/api/v1/price-lists/{list.Id}/items", new { items = new[] { new { variantId = variant, unitPrice = 1m } } }, idempotencyKey: Guid.NewGuid().ToString()),
            HttpStatusCode.Forbidden,
            "auth.forbidden");
    }

    [Fact]
    public async Task Warehouse_staff_sees_no_prices()
    {
        var kit = await PricingKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync();
        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);

        await HttpAssert.ProblemAsync(await staff.GetAsync("/api/v1/price-lists"), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await kit.QuoteResponseAsync(staff, null, (variant, 1m)), HttpStatusCode.Forbidden, "auth.forbidden");
    }

    [Fact]
    public async Task Another_tenants_lists_items_and_customers_are_invisible_to_pricing()
    {
        var a = await PricingKit.CreateAsync(fixture);
        var b = await PricingKit.CreateAsync(fixture);
        var listA = await a.PriceListAsync("A Perakende");
        var variantA = await a.VariantAsync();
        await a.ItemsAsync(listA.Id, (variantA, 1m, 10m));
        var customerA = await a.CustomerAsync();
        var variantB = await b.VariantAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/price-lists/{listA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/price-lists/{listA.Id}/items")).StatusCode);
        Assert.Empty((await b.Owner.OkAsync<PriceListListResponse>(await b.Owner.GetAsync("/api/v1/price-lists"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.SendAsync(
            HttpMethod.Put, $"/api/v1/price-lists/{listA.Id}/items", new { items = new[] { new { variantId = variantB, unitPrice = 1m } } }, idempotencyKey: Guid.NewGuid().ToString())).StatusCode);
        // Tenant A's variant and customer do not exist for tenant B's resolver.
        await HttpAssert.ProblemAsync(await b.QuoteResponseAsync(b.Owner, null, (variantA, 1m)), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await b.QuoteResponseAsync(b.Owner, customerA.Id, (variantB, 1m)), HttpStatusCode.UnprocessableEntity, "validation.failed");
        // Tenant A's list cannot be assigned in tenant B.
        await HttpAssert.ProblemAsync(
            await b.Owner.PostAsync("/api/v1/dealer-groups", new { name = "G", priceListId = listA.Id }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await b.Owner.Targeting(a.TenantId).GetAsync("/api/v1/price-lists"), HttpStatusCode.Forbidden, "tenant.access_denied");
    }
}

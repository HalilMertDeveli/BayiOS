using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;

namespace BayiOS.IntegrationTests.Contracts;

/// <summary>#86: API contract gaps found by the web team (CORS, batch variants, conflict detail, OpenAPI documentation).</summary>
public sealed class ContractGapsTests(ApiFixture fixture)
{
    /// <summary>appsettings.Development.json (the test host runs as Development).</summary>
    private const string WebOrigin = "http://localhost:3200";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cors_exposes_retry_after_and_idempotency_headers()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenant");
        request.Headers.Add("Origin", WebOrigin);
        request.Headers.Add("X-Tenant-Id", owner.TenantId.ToString());
        var response = await owner.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        var exposed = string.Join(',', response.Headers.GetValues("Access-Control-Expose-Headers"))
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Retry-After", exposed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Idempotent-Replayed", exposed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ETag", exposed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Batch_variants_returns_display_fields_for_own_tenant_ids_only()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var first = Assert.Single((await owner.CreateSimpleProductAsync(unit.Id, "Sentetik H4 ampul", "H4-0001", "8690000000011")).Variants);
        var second = Assert.Single((await owner.CreateSimpleProductAsync(unit.Id, "Sentetik zincir", "ZN-0002")).Variants);
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        var foreign = Assert.Single((await other.CreateSimpleProductAsync((await other.CreateUnitAsync()).Id, "Yabancı ürün", "YB-0001")).Variants);

        var ids = string.Join(',', second.Id, first.Id, first.Id, foreign.Id, Guid.NewGuid());
        var result = await owner.OkAsync<ListResponse<VariantSummaryResponse>>(await owner.GetAsync($"/api/v1/variants/batch?ids={ids}"));

        // Sorted by SKU, duplicates collapsed, foreign and unknown ids silently absent.
        Assert.Equal([first.Id, second.Id], result.Items.Select(v => v.Id));
        var h4 = result.Items[0];
        Assert.Equal(("H4-0001", "Sentetik H4 ampul", "8690000000011", "active", (string?)null), (h4.Sku, h4.ProductName, h4.Barcode, h4.Status, h4.Name));
        Assert.DoesNotContain("YB-0001", await (await owner.GetAsync($"/api/v1/variants/batch?ids={foreign.Id}")).Content.ReadAsStringAsync(Ct));
        Assert.Empty((await owner.OkAsync<ListResponse<VariantSummaryResponse>>(await owner.GetAsync($"/api/v1/variants/batch?ids={foreign.Id}"))).Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?ids=")]
    [InlineData("?ids=not-a-uuid")]
    [InlineData("?ids=OVER")]
    [InlineData("?id=00000000-0000-0000-0000-000000000001")]
    public async Task Batch_variants_rejects_missing_malformed_or_oversized_id_lists(string query)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var path = "/api/v1/variants/batch" + query.Replace("OVER", string.Join(',', Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())), StringComparison.Ordinal);
        await HttpAssert.ProblemAsync(await owner.GetAsync(path), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Batch_variants_accepts_exactly_one_hundred_ids()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var ids = string.Join(',', Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()));
        Assert.Empty((await owner.OkAsync<ListResponse<VariantSummaryResponse>>(await owner.GetAsync($"/api/v1/variants/batch?ids={ids}"))).Items);
    }

    [Fact]
    public async Task Sku_and_barcode_conflicts_name_the_clashing_variant()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var existing = await owner.CreateSimpleProductAsync(unit.Id, "Sentetik far", "FAR-0001", "8690000000028");
        var holder = Assert.Single(existing.Variants);

        var skuClash = await owner.PostAsync("/api/v1/products", new { name = "Kopya", defaultUnitId = unit.Id, taxRate = 20m, sku = "far-0001" });
        var sku = await ConflictAsync(skuClash, "catalog.sku_taken");
        Assert.Equal(("sku", "FAR-0001", holder.Id, existing.Id, "FAR-0001"), sku);

        var other = Assert.Single((await owner.CreateSimpleProductAsync(unit.Id, "Başka", "BSK-0001")).Variants);
        var barcodeClash = await owner.PatchAsync($"/api/v1/variants/{other.Id}", new { barcode = "8690000000028" }, CatalogClient.ETag(other.Version));
        var barcode = await ConflictAsync(barcodeClash, "catalog.barcode_taken");
        Assert.Equal(("barcode", "8690000000028", holder.Id, existing.Id, "FAR-0001"), barcode);

        // Nothing was written.
        Assert.Null((await owner.OkAsync<VariantResponse>(await owner.GetAsync($"/api/v1/variants/{other.Id}"))).Barcode);
    }

    [Fact]
    public async Task OpenApi_documents_enums_problem_extensions_retry_after_and_attribute_filters()
    {
        using var client = fixture.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", Ct))!;
        var schemas = document["components"]!["schemas"]!;

        Assert.Equal(["product", "variant"], Enum(schemas["CustomFieldResponse"]!["properties"]!["entityType"]!));
        Assert.Contains("multi_enum", Enum(schemas["CustomFieldResponse"]!["properties"]!["dataType"]!));
        Assert.Equal(["all", "conditional", "own"], Enum(schemas["PermissionGrantResponse"]!["properties"]!["scope"]!));
        Assert.NotNull(schemas["StockProblemDetails"]!["allOf"]![1]!["properties"]!["shortages"]);
        Assert.NotNull(schemas["StockProblemDetails"]!["allOf"]![1]!["properties"]!["confirmationRequired"]);
        Assert.NotNull(schemas["CodeConflictProblemDetails"]!["allOf"]![1]!["properties"]!["conflict"]);

        var paths = document["paths"]!;
        var approve = paths["/api/v1/orders/{id}/approve"]!["post"]!["responses"]!["409"]!;
        Assert.NotNull(approve["headers"]!["Retry-After"]);
        Assert.EndsWith("StockProblemDetails", approve["content"]!["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(paths["/api/v1/inventory/adjustments"]!["post"]!["responses"]!["409"]!["headers"]!["Retry-After"]);
        Assert.EndsWith(
            "CodeConflictProblemDetails",
            paths["/api/v1/products"]!["post"]!["responses"]!["409"]!["content"]!["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>(),
            StringComparison.Ordinal);

        var patterns = paths["/api/v1/products"]!["get"]!["x-query-parameter-patterns"]!.AsArray().Select(p => p!["pattern"]!.GetValue<string>());
        Assert.Equal(["attr.{key}", "attr.{key}.min", "attr.{key}.max"], patterns);
        Assert.Equal(
            ["tasks.read.own", "tasks.read.all"],
            paths["/api/v1/picking-tasks/{id}"]!["get"]!["x-permission-any-of"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Contains(paths["/api/v1/me"]!["get"]!["parameters"]!.AsArray(), p => p!["name"]!.GetValue<string>() == "X-Tenant-Id" && p["required"]?.GetValue<bool>() != true);
    }

    private static string[] Enum(JsonNode property) => [.. property["enum"]!.AsArray().Select(v => v!.GetValue<string>())];

    private static async Task<(string Field, string Value, Guid VariantId, Guid ProductId, string Sku)> ConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        var conflict = problem.RootElement.GetProperty("conflict");
        return (
            conflict.GetProperty("field").GetString()!,
            conflict.GetProperty("value").GetString()!,
            conflict.GetProperty("variantId").GetGuid(),
            conflict.GetProperty("productId").GetGuid(),
            conflict.GetProperty("sku").GetString()!);
    }
}

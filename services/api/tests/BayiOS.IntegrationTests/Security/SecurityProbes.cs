using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using Npgsql;

namespace BayiOS.IntegrationTests.Security;

/// <summary>Request bodies and helpers shared by the OpenAPI-driven security sweeps.</summary>
internal static class SecurityProbes
{
    private static readonly Dictionary<string, string> ImportColumns = new() { ["sku"] = "Stok Kodu", ["name"] = "Ürün Adı", ["unit"] = "Birim", ["taxRate"] = "KDV" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A well-formed body for every operation that takes one, so a probe reaches the resource lookup instead of
    /// stopping at validation. <paramref name="victim"/> owns the resources named in the path; <paramref name="own"/>
    /// is the caller's tenant (its ids are used for any reference inside the body, so the path id is the only
    /// foreign thing). Returns <c>false</c> for an operation with a body that is not listed here.
    /// </summary>
    public static bool TryBody(ApiRoute route, TenantWorld victim, TenantWorld own, out object? body)
    {
        var hack = "HACK-" + Guid.NewGuid().ToString("N")[..8];
        body = route.Key switch
        {
            "PATCH /api/v1/tenant" => new { name = hack },
            "PATCH /api/v1/tenant/settings" => new { require2fa = true },
            "POST /api/v1/units" => new { code = hack, name = hack, allowsFraction = false },
            "PATCH /api/v1/units/{id}" => new { name = hack },
            "POST /api/v1/brands" => new { name = hack },
            "PATCH /api/v1/brands/{id}" => new { name = hack },
            "POST /api/v1/categories" => new { name = hack },
            "PATCH /api/v1/categories/{id}" => new { name = hack },
            "POST /api/v1/products" => new { name = hack, defaultUnitId = own.Id("unit"), taxRate = 20m, sku = hack },
            "PATCH /api/v1/products/{id}" => new { name = hack },
            "POST /api/v1/products/{id}/variants" => new { sku = hack },
            "PATCH /api/v1/variants/{id}" => new { name = hack },
            "POST /api/v1/custom-fields" => new { entityType = "product", key = hack.ToLowerInvariant().Replace('-', '_'), label = hack, dataType = "text" },
            "PATCH /api/v1/custom-fields/{id}" => new { label = hack },
            "POST /api/v1/inventory/receipts" => new { warehouseId = own.Id("warehouse"), reference = hack, lines = new[] { new { variantId = own.Id("variant"), quantity = 1m, locationId = own.Id("location") } } },
            "POST /api/v1/inventory/adjustments" => new { variantId = own.Id("variant"), warehouseId = own.Id("warehouse"), locationId = own.Id("location"), type = "increase", quantity = 1m, reason = hack },
            "POST /api/v1/inventory/thresholds" => new { variantId = own.Id("variant"), warehouseId = own.Id("warehouse"), minQuantity = 7m },
            "POST /api/v1/inventory/thresholds/bulk" => new { items = new[] { new { variantId = own.Id("variant"), minQuantity = 7m } } },
            "PUT /api/v1/inventory/thresholds/{id}" => new { minQuantity = 7m },
            "POST /api/v1/warehouses" => new { code = hack[..10], name = hack },
            "PATCH /api/v1/warehouses/{id}" => new { name = hack },
            "POST /api/v1/warehouses/{id}/locations" => new { code = hack[..10] },
            "PATCH /api/v1/locations/{id}" => new { code = hack[..10] },
            "POST /api/v1/price-lists" => new { name = hack },
            "PATCH /api/v1/price-lists/{id}" => new { name = hack },
            "PUT /api/v1/price-lists/{id}/items" => new { items = new[] { new { variantId = own.Id("variant"), minQuantity = 1m, unitPrice = 1m } } },
            "POST /api/v1/pricing/quote" => new { customerId = own.Id("customer"), lines = new[] { new { variantId = own.Id("variant"), quantity = 1m } } },
            "POST /api/v1/customers" => new { code = hack[..10], name = hack },
            "PATCH /api/v1/customers/{id}" => new { name = hack },
            "POST /api/v1/customers/{id}/addresses" => new { line1 = hack, city = "İzmir" },
            "PATCH /api/v1/customers/{id}/addresses/{addressId}" => new { line1 = hack },
            "POST /api/v1/dealer-groups" => new { name = hack },
            "PATCH /api/v1/dealer-groups/{id}" => new { name = hack },
            "PUT /api/v1/import-jobs/{id}/mapping" => new { columns = ImportColumns },
            "POST /api/v1/import-mapping-templates" => new { name = hack, type = "products", columns = ImportColumns },
            "PUT /api/v1/import-mapping-templates/{id}" => new { name = hack, type = "products", columns = ImportColumns },
            "POST /api/v1/orders" => new { customerId = own.Id("customer"), warehouseId = own.Id("warehouse"), lines = new[] { new { variantId = own.Id("variant"), quantity = 1m } } },
            "PATCH /api/v1/orders/{id}" => new { notes = hack },
            "POST /api/v1/orders/{id}/reject" or "POST /api/v1/orders/{id}/close-remaining" or "POST /api/v1/orders/{id}/cancel"
                or "POST /api/v1/shipments/{id}/cancel" or "POST /api/v1/stock-transfers/{id}/cancel" or "POST /api/v1/stock-counts/{id}/cancel"
                => new { reason = hack },
            "POST /api/v1/inventory/relocations" => new
            {
                warehouseId = own.Id("warehouse"),
                lines = new[] { new { variantId = own.Id("variant"), fromLocationId = own.Id("location"), toLocationId = own.Id("location"), quantity = 1m } },
            },
            "POST /api/v1/stock-transfers" => new
            {
                sourceWarehouseId = own.Id("warehouse"),
                destinationWarehouseId = own.Id("warehouse2"),
                lines = new[] { new { variantId = own.Id("variant"), quantity = 1m, sourceLocationId = own.Id("location") } },
            },
            "POST /api/v1/stock-transfers/{id}/receive" => new { lines = new[] { new { code = victim.Sku, quantity = 1m } } },
            "POST /api/v1/stock-transfers/{id}/close" => new { resolution = "lost", reason = hack },
            "POST /api/v1/stock-counts" => new { warehouseId = own.Id("warehouse2"), scope = "full" },
            "POST /api/v1/stock-counts/{id}/entries" => new { entries = new[] { new { code = victim.Sku, locationId = victim.Id("location"), quantity = 1m } } },
            "POST /api/v1/stock-counts/{id}/recount" => new { lineIds = new[] { Guid.NewGuid() } },
            "POST /api/v1/stock-counts/{id}/apply" => new { acceptMovedLines = true },
            "POST /api/v1/orders/{id}/shipments" => new
            {
                lines = new[] { new { orderLineId = victim.OrderLineId, quantity = 1m, locationId = (Guid?)victim.Id("location") } },
                carrier = hack,
                trackingNumber = hack,
                packageCount = 1,
            },
            "POST /api/v1/picking-tasks" => new { orderId = own.Id("order") },
            "POST /api/v1/picking-tasks/{id}/assign" => new { userId = own.Owner.UserId },
            "POST /api/v1/picking-tasks/{id}/scans" => new { code = victim.Sku, quantity = 1m, lineId = victim.Id("pickingTaskLine") },
            "POST /api/v1/picking-tasks/{id}/lines/{lineId}/shortage" or "POST /api/v1/shipments/{id}/lines/{lineId}/shortage" => new { quantity = 1m, reason = "not_found" },
            "POST /api/v1/shipments/{id}/pack" => new { packages = new[] { new { items = new[] { new { code = victim.Sku, quantity = 1m } } } } },
            "POST /api/v1/shipments/{id}/confirm" => new { carrier = hack, trackingNumber = hack },
            "POST /api/v1/members/invitations" => new { email = hack.ToLowerInvariant() + "@example.test", role = "viewer" },
            "PUT /api/v1/members/{userId}/role" => new { role = "viewer" },
            "POST /api/v1/members/ownership-transfer" => new { userId = victim.Id("member") },
            "POST /api/v1/invitations/accept" => new { token = hack },
            "POST /api/v1/devices" => new { token = $"{hack}-{Guid.NewGuid():N}", platform = "android" },
            "POST /api/v1/import-jobs" => "multipart",
            "POST /api/v1/ai/order-parses" => new { text = "2 adet fren balatası " + hack, customerId = own.Id("customer") },
            "PUT /api/v1/ai/settings" => new { aiParsingEnabled = true },
            "POST /api/v1/ai/order-parses/{id}/approve" => new
            {
                customerId = own.Id("customer"),
                lines = new[] { new { lineNo = 1, include = true, variantId = own.Id("variant"), quantity = 1m } },
            },
            _ when route.Method == "POST" && route.Template.StartsWith("/api/v1/reports/", StringComparison.Ordinal) && route.Template.EndsWith("/export", StringComparison.Ordinal)
                => new { format = "csv" },
            _ when route.RequestContentTypes.Count == 0 && route.Method == "POST" => new { },
            _ when route.RequestContentTypes.Count == 0 => null,
            "POST /api/v1/orders/{id}/approve" or "POST /api/v1/orders/{id}/submit" => new { },
            _ => Missing,
        };

        return !ReferenceEquals(body, Missing);
    }

    private static readonly object Missing = new();

    public static string ContentType(ApiRoute route) =>
        route.RequestContentTypes.Contains("application/merge-patch+json") ? "application/merge-patch+json" : "application/json";

    public static async Task<HttpResponseMessage> SendAsync(ApiFixture fixture, CatalogClient client, ApiRoute route, string path, object? body, string? ifMatch)
    {
        if (body is "multipart")
        {
            // Routing matches on the request content type, so the probe must be a real (tiny, synthetic) upload.
            using var http = fixture.CreateClientFor(client.UserId);
            using var request = new HttpRequestMessage(route.HttpMethod, path);
            request.Headers.Add("X-Tenant-Id", client.TenantId.ToString());
            request.Headers.Add(IdempotentRequests.Header, Guid.NewGuid().ToString());
            var part = new ByteArrayContent("Stok Kodu;Ürün Adı\nHACK-1;Sızma\n"u8.ToArray());
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
            request.Content = new MultipartFormDataContent { { part, "file", "probe.csv" }, { new StringContent("products"), "type" } };
            return await http.SendAsync(request, Ct);
        }

        return await client.SendAsync(
            route.HttpMethod,
            path,
            body,
            ifMatch,
            ContentType(route),
            idempotencyKey: route.Method is "GET" ? null : Guid.NewGuid().ToString());
    }

    /// <summary>Fails with every collected violation spelled out (xUnit truncates long collections).</summary>
    public static void AssertNone(IReadOnlyCollection<string> failures)
    {
        if (failures.Count > 0)
        {
            Assert.Fail($"{failures.Count} violation(s):\n" + string.Join('\n', failures));
        }
    }

    /// <summary>Secrets of <paramref name="victim"/> found in a response, ignoring what the caller itself sent in the path.</summary>
    public static IReadOnlyList<string> Leaks(string responseBody, string requestPath, TenantWorld victim)
    {
        var text = responseBody.Replace(requestPath, string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var segment in requestPath.Split('/', '?', '&', '='))
        {
            if (Guid.TryParse(segment, out _))
            {
                text = text.Replace(segment, string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        return [.. victim.Secrets().Where(s => text.Contains(s, StringComparison.OrdinalIgnoreCase))];
    }

    public static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Adds an existing user to a tenant with the given role and status (no invitation flow yet).</summary>
    public static async Task AddMembershipAsync(ApiFixture fixture, Guid tenantId, Guid userId, string role, string status = "active")
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO identity.memberships (id, tenant_id, user_id, role, status, created_at) VALUES ($1, $2, $3, $4, $5, now())", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        await command.ExecuteNonQueryAsync(Ct);
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.TestSupport.BulkImport;
using Npgsql;

namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// A tenant holding one resource of every kind the API exposes by id, created through the public API by its owner.
/// Every name/code carries <see cref="Token"/>, so a leak of this tenant's data into another tenant's response can be
/// detected by a plain substring search. Synthetic motorcycle-parts data only.
/// </summary>
internal sealed class TenantWorld
{
    /// <summary>Collection before an <c>{id}</c> (nested: <c>parent/child</c>, falling back to <c>child</c>) → resource kind. A new collection must be added here.</summary>
    public static readonly IReadOnlyDictionary<string, string> SegmentKinds = new Dictionary<string, string>
    {
        ["units"] = "unit",
        ["brands"] = "brand",
        ["categories"] = "category",
        ["custom-fields"] = "customField",
        ["products"] = "product",
        ["variants"] = "variant",
        ["movements"] = "movement",
        ["thresholds"] = "threshold",
        ["warehouses"] = "warehouse",
        ["locations"] = "location",
        ["price-lists"] = "priceList",
        ["items"] = "priceListItem",
        ["customers"] = "customer",
        ["addresses"] = "address",
        ["dealer-groups"] = "dealerGroup",
        ["import-jobs"] = "importJob",
        ["import-mapping-templates"] = "mappingTemplate",
        ["orders"] = "order",
        ["picking-tasks"] = "pickingTask",
        ["picking-tasks/lines"] = "pickingTaskLine",
        ["shipments"] = "shipment",
        ["shipments/lines"] = "shipmentLine",
        ["order-parses"] = "orderParse",
        ["aliases"] = "learnedAlias",
        ["members"] = "member",
        ["invitations"] = "invitation",
        ["devices"] = "device",
        ["stock-transfers"] = "stockTransfer",
        ["stock-counts"] = "stockCount",
    };

    private readonly ApiFixture _fixture;
    private readonly Dictionary<string, Guid> _ids = [];
    private readonly Dictionary<string, long> _versions = [];

    private TenantWorld(ApiFixture fixture, CatalogClient owner, string token)
    {
        _fixture = fixture;
        Owner = owner;
        Token = token;
    }

    public CatalogClient Owner { get; }

    public Guid TenantId => Owner.TenantId;

    /// <summary>Unique marker embedded in every name/code/SKU of this tenant.</summary>
    public string Token { get; }

    public string Sku => $"{Token}-SKU1";

    public string Barcode { get; private set; } = string.Empty;

    public Guid OrderLineId { get; private set; }

    public IReadOnlyDictionary<string, Guid> Ids => _ids;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Guid Id(string kind) => _ids.TryGetValue(kind, out var id) ? id : throw new KeyNotFoundException($"TenantWorld has no '{kind}'.");

    public string ETag(string kind) => CatalogClient.ETag(_versions.GetValueOrDefault(kind, 1));

    public bool HasVersion(string kind) => _versions.ContainsKey(kind);

    /// <summary>ETag for a path addressing <paramref name="kinds"/> (outer → inner): the innermost versioned resource.</summary>
    public string ETag(IReadOnlyList<string> kinds) => ETag(kinds.LastOrDefault(HasVersion) ?? kinds[0]);

    /// <summary>Every id this tenant owns plus its tenant id — none may appear in another tenant's response.</summary>
    public IEnumerable<string> Secrets() => [Token, TenantId.ToString(), .. _ids.Values.Select(id => id.ToString())];

    public static async Task<TenantWorld> CreateAsync(ApiFixture fixture, string label)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, "motorcycle_parts");
        var token = $"{label}{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var world = new TenantWorld(fixture, owner, token);
        await world.BuildAsync();
        return world;
    }

    private async Task BuildAsync()
    {
        _ids["owner"] = Owner.UserId;
        Barcode = "2" + Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture);

        await CreateAsync("unit", "/api/v1/units", new { code = "adet", name = "Adet " + Token, allowsFraction = false });
        await CreateAsync("brand", "/api/v1/brands", new { name = "Marka " + Token });
        await CreateAsync("category", "/api/v1/categories", new { name = "Fren " + Token });
        await CreateAsync("customField", "/api/v1/custom-fields", new
        {
            entityType = "product",
            key = "renk_" + Token.ToLowerInvariant(),
            label = "Renk " + Token,
            dataType = "text",
            filterable = true,
        });

        var product = await CreateAsync("product", "/api/v1/products", new
        {
            name = "Fren balatası " + Token,
            defaultUnitId = Id("unit"),
            taxRate = 20m,
            sku = Sku,
            barcode = Barcode,
            categoryId = Id("category"),
            brandId = Id("brand"),
        });
        var variant = product.GetProperty("variants")[0];
        Remember("variant", variant);
        await CreateAsync("extraVariant", $"/api/v1/products/{Id("product")}/variants", new { sku = $"{Token}-SKU2", name = "Kırmızı " + Token });

        await CreateAsync("warehouse", "/api/v1/warehouses", new { code = "W" + Token[..6], name = "Depo " + Token, usesLocations = true });
        await CreateAsync("location", $"/api/v1/warehouses/{Id("warehouse")}/locations", new { code = "R-" + Token[..6], pickSequence = 1 });
        var receipt = await CreateAsync("receipt", "/api/v1/inventory/receipts", new
        {
            warehouseId = Id("warehouse"),
            reference = "İrsaliye " + Token,
            lines = new[] { new { variantId = Id("variant"), quantity = 25m, locationId = (Guid?)Id("location") } },
        });
        _ids["movement"] = receipt.GetProperty("movements")[0].GetProperty("id").GetGuid();

        // Minimum stock (#137): a warehouse override; the probes address it by id.
        await CreateAsync("threshold", "/api/v1/inventory/thresholds", new { variantId = Id("variant"), warehouseId = Id("warehouse"), minQuantity = 5m });

        // Transfers and counts (#168): a second, location-less warehouse, a draft transfer into it and an open count.
        await CreateAsync("warehouse2", "/api/v1/warehouses", new { code = "X" + Token[..6], name = "Depo 2 " + Token });
        var transfer = await CreateAsync(null, "/api/v1/stock-transfers", new
        {
            sourceWarehouseId = Id("warehouse"),
            destinationWarehouseId = Id("warehouse2"),
            note = "Transfer " + Token,
            lines = new[] { new { variantId = Id("variant"), quantity = 1m, sourceLocationId = (Guid?)Id("location") } },
        });
        Remember("stockTransfer", transfer.GetProperty("transfer"));
        var count = await CreateAsync(null, "/api/v1/stock-counts", new { warehouseId = Id("warehouse"), note = "Sayım " + Token });
        Remember("stockCount", count.GetProperty("count"));

        await CreateAsync("priceList", "/api/v1/price-lists", new { name = "Perakende " + Token, isDefault = true });
        var items = await SendOkAsync(HttpMethod.Put, $"/api/v1/price-lists/{Id("priceList")}/items", new
        {
            items = new[] { new { variantId = Id("variant"), minQuantity = 1m, unitPrice = 150m } },
        });
        Remember("priceListItem", items.GetProperty("items")[0]);

        await CreateAsync("dealerGroup", "/api/v1/dealer-groups", new { name = "Bayi " + Token, priceListId = Id("priceList") });
        var customer = await CreateAsync(null, "/api/v1/customers", new { code = "C" + Token[..10], name = "Sentetik Motor " + Token, dealerGroupId = Id("dealerGroup") });
        Remember("customer", customer.GetProperty("customer"));
        await CreateAsync("address", $"/api/v1/customers/{Id("customer")}/addresses", new { title = "Merkez " + Token, line1 = "Sanayi Sitesi " + Token, city = "İstanbul" });

        await CreateAsync("mappingTemplate", "/api/v1/import-mapping-templates", new
        {
            name = "Şablon " + Token,
            type = "products",
            columns = new Dictionary<string, string> { ["sku"] = "Stok Kodu", ["name"] = "Ürün Adı", ["unit"] = "Birim", ["taxRate"] = "KDV" },
        });
        await UploadImportAsync();

        var order = await CreateAsync(null, "/api/v1/orders", new
        {
            customerId = Id("customer"),
            warehouseId = Id("warehouse"),
            notes = "Sipariş " + Token,
            lines = new[] { new { variantId = Id("variant"), quantity = 2m } },
        });
        Remember("order", order.GetProperty("order"));
        OrderLineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        _ids["orderLine"] = OrderLineId;
        await FulfilAsync();

        // A WhatsApp parse awaiting review (AI off: rules parser), naming the tenant's own product: opt in, submit (202),
        // run its background job.
        await SendOkAsync(HttpMethod.Put, "/api/v1/ai/settings", new { aiParsingEnabled = true });
        var parse = await ReadAsync(
            await Owner.PostAsync("/api/v1/ai/order-parses", new { text = $"2 adet fren balatası {Token}", customerId = Id("customer") }),
            HttpStatusCode.Accepted,
            "/api/v1/ai/order-parses");
        Remember("orderParse", parse);
        await Integrations.AssistantKit.ProcessAsync(_fixture.Services, Owner, Id("orderParse"), "tenant-world");

        // A learned alias (WAI-07) of the tenant's variant — normally written by an approval; here directly, so the
        // parse above stays reviewable for the approve/reject probes.
        _ids["learnedAlias"] = await Integrations.AssistantKit.ScalarAsync<Guid>(
            _fixture,
            """
            INSERT INTO integrations.learned_aliases (id, tenant_id, phrase, variant_id, confirmations, created_at, updated_at)
            VALUES (gen_random_uuid(), $1, $2, $3, 1, now(), now()) RETURNING id
            """,
            TenantId,
            "fren balatasi " + Token.ToLowerInvariant(),
            Id("variant"));

        // A second member (sales) and a pending invitation (#129): targets of the member-management routes.
        var member = Guid.NewGuid();
        await SecurityProbes.AddMembershipAsync(_fixture, TenantId, member, "sales");
        _ids["member"] = member;
        var invitation = await CreateAsync(null, "/api/v1/members/invitations", new { email = $"davet-{Token.ToLowerInvariant()}@example.test", role = "viewer" });
        Remember("invitation", invitation.GetProperty("invitation"));

        // A push registration of the owner (#44): target of DELETE /devices/{id}. The token is a synthetic FCM-like value.
        var device = await ReadAsync(
            await Owner.PostAsync("/api/v1/devices", new { token = $"world-{Guid.NewGuid():N}:{Guid.NewGuid():N}", platform = "android", appVersion = "0.1.0", locale = "tr-TR" }),
            HttpStatusCode.OK,
            "/api/v1/devices");
        Remember("device", device);

        // Versions may have moved while children were added (ETag of the current state is used by the probes).
        await RefreshAsync("product", $"/api/v1/products/{Id("product")}");
        await RefreshAsync("variant", $"/api/v1/variants/{Id("variant")}");
        await RefreshAsync("warehouse", $"/api/v1/warehouses/{Id("warehouse")}");
        await RefreshAsync("priceList", $"/api/v1/price-lists/{Id("priceList")}");
        await RefreshAsync("dealerGroup", $"/api/v1/dealer-groups/{Id("dealerGroup")}");
        await RefreshAsync("order", $"/api/v1/orders/{Id("order")}", "order");
        await RefreshAsync("mappingTemplate", $"/api/v1/import-mapping-templates/{Id("mappingTemplate")}");
        var detail = await SendOkAsync(HttpMethod.Get, $"/api/v1/customers/{Id("customer")}");
        _versions["customer"] = detail.GetProperty("customer").GetProperty("version").GetInt64();
        _versions["address"] = detail.GetProperty("addresses")[0].GetProperty("version").GetInt64();
    }

    /// <summary>
    /// Two more orders, approved (approval creates the pick task): one task stays open (probes reach its lines), the
    /// other is picked and completed, which creates the draft shipment.
    /// </summary>
    private async Task FulfilAsync()
    {
        var open = await ActiveTaskForNewOrderAsync();
        _ids["pickingTask"] = open.Task.Id;
        _ids["pickingTaskLine"] = open.Lines[0].Id;
        _versions["pickingTask"] = open.Task.Version;

        var picked = await ActiveTaskForNewOrderAsync();
        var completed = await Fulfillment.PickAndCompleteAsync(Owner, picked, new Dictionary<Guid, decimal> { [picked.Lines[0].OrderLineId] = 1m });
        _ids["shipment"] = completed.ShipmentId!.Value;
        var shipment = await SendOkAsync(HttpMethod.Get, $"/api/v1/shipments/{Id("shipment")}");
        Remember("shipment", shipment);
        _ids["shipmentLine"] = shipment.GetProperty("lines")[0].GetProperty("id").GetGuid();
    }

    private async Task<BayiOS.Modules.Warehouse.Contracts.PickingTaskResponse> ActiveTaskForNewOrderAsync()
    {
        var order = (await CreateAsync(null, "/api/v1/orders", new
        {
            customerId = Id("customer"),
            warehouseId = Id("warehouse"),
            notes = "Sevk " + Token,
            lines = new[] { new { variantId = Id("variant"), quantity = 1m } },
        })).GetProperty("order");
        var orderId = order.GetProperty("id").GetGuid();
        var version = order.GetProperty("version").GetInt64();
        foreach (var action in new[] { "submit", "approve" })
        {
            var response = await Owner.SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/{action}", new { }, CatalogClient.ETag(version));
            version = (await ReadAsync(response, HttpStatusCode.OK, action)).GetProperty("order").GetProperty("version").GetInt64();
        }

        return await Fulfillment.ActiveTaskAsync(Owner, orderId);
    }

    private async Task UploadImportAsync()
    {
        using var http = _fixture.CreateClientFor(Owner.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import-jobs");
        request.Headers.Add("X-Tenant-Id", TenantId.ToString());
        request.Headers.Add(IdempotentRequests.Header, Guid.NewGuid().ToString());
        var csv = SyntheticFiles.Csv(["Stok Kodu", "Ürün Adı", "Birim", "KDV"], [[$"{Token}-IMP", "İçe aktarılan " + Token, "adet", 20]]);
        var part = new ByteArrayContent(csv);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        using var form = new MultipartFormDataContent { { part, "file", Token + ".csv" }, { new StringContent("products"), "type" } };
        request.Content = form;
        var response = await http.SendAsync(request, Ct);
        Remember("importJob", await ReadAsync(response, HttpStatusCode.Created, "upload"));
    }

    private async Task<JsonElement> CreateAsync(string? kind, string path, object body)
    {
        var element = await ReadAsync(await Owner.PostAsync(path, body), HttpStatusCode.Created, path);
        if (kind is not null)
        {
            Remember(kind, element);
        }

        return element;
    }

    private async Task<JsonElement> SendOkAsync(HttpMethod method, string path, object? body = null) =>
        await ReadAsync(await Owner.SendAsync(method, path, body, idempotencyKey: method == HttpMethod.Put ? Guid.NewGuid().ToString() : null), HttpStatusCode.OK, path);

    private async Task RefreshAsync(string kind, string path, string? property = null)
    {
        var element = await SendOkAsync(HttpMethod.Get, path);
        Remember(kind, property is null ? element : element.GetProperty(property));
    }

    private void Remember(string kind, JsonElement element)
    {
        if (element.TryGetProperty("id", out var id))
        {
            _ids[kind] = id.GetGuid();
        }
        else if (element.TryGetProperty("receiptId", out var receiptId))
        {
            _ids[kind] = receiptId.GetGuid();
        }

        if (element.TryGetProperty("version", out var version))
        {
            _versions[kind] = version.GetInt64();
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected, string what)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{what}: {(int)response.StatusCode} {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Snapshot of every row this tenant owns in every table that has a <c>tenant_id</c> (read as superuser,
    /// catalogue-driven), keyed by table and primary key: equal before and after an attack ⇔ the attack changed nothing
    /// of this tenant. <see cref="TenantSnapshot.Diff"/> names the exact rows and columns that differ.
    /// </summary>
    public async Task<TenantSnapshot> SnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        var tables = new List<(string Table, string Key)>();
        await using (var list = new NpgsqlCommand(
            $"""
            SELECT t.name, coalesce((
                SELECT string_agg(format('t.%I::text', a.attname), $$ || '/' || $$ ORDER BY k.ord)
                FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                WHERE i.indrelid = t.name::regclass AND i.indisprimary), 't::text')
            FROM ({DatabaseCatalog.TenantTablesSql}) AS t(name)
            """,
            connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var rows = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var (table, key) in tables)
        {
            await using var command = new NpgsqlCommand($"SELECT {key}, to_jsonb(t)::text FROM {table} t WHERE t.tenant_id = $1", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var byKey = new Dictionary<string, string>();
            while (await reader.ReadAsync(Ct))
            {
                byKey[reader.GetString(0)] = reader.GetString(1);
            }

            rows[table] = byKey;
        }

        return new TenantSnapshot(rows);
    }
}

/// <summary>Rows of one tenant per table (primary key → row as JSON).</summary>
internal sealed record TenantSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Rows)
{
    /// <summary>Every inserted, deleted or changed row, with the changed columns and their before → after values.</summary>
    public IReadOnlyList<string> Diff(TenantSnapshot after)
    {
        var differences = new List<string>();
        foreach (var table in Rows.Keys.Union(after.Rows.Keys).Order())
        {
            var before = Rows.GetValueOrDefault(table) ?? new Dictionary<string, string>();
            var now = after.Rows.GetValueOrDefault(table) ?? new Dictionary<string, string>();
            foreach (var key in before.Keys.Except(now.Keys))
            {
                differences.Add($"{table} [{key}] deleted: {before[key]}");
            }

            foreach (var key in now.Keys.Except(before.Keys))
            {
                differences.Add($"{table} [{key}] inserted: {now[key]}");
            }

            foreach (var key in before.Keys.Intersect(now.Keys).Where(k => before[k] != now[k]))
            {
                using var old = System.Text.Json.JsonDocument.Parse(before[key]);
                using var @new = System.Text.Json.JsonDocument.Parse(now[key]);
                var changed = old.RootElement.EnumerateObject()
                    .Where(p => !@new.RootElement.TryGetProperty(p.Name, out var v) || v.GetRawText() != p.Value.GetRawText())
                    .Select(p => $"{p.Name}: {p.Value.GetRawText()} → {(@new.RootElement.TryGetProperty(p.Name, out var v) ? v.GetRawText() : "(missing)")}");
                differences.Add($"{table} [{key}] updated: {string.Join("; ", changed)}");
            }
        }

        return differences;
    }
}

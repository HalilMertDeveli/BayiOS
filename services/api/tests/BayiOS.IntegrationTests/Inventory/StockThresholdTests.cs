using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.IntegrationTests.Reports;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Inventory;

/// <summary>
/// #137 — minimum stock thresholds: CRUD and bulk writes (Idempotency-Key, If-Match), warehouse override precedence,
/// the critical-stock report and the stock list against SQL truth, tenant isolation, role permissions and audit.
/// Synthetic tenants built through the API.
/// </summary>
public sealed class StockThresholdTests(ApiFixture fixture)
{
    private const string Thresholds = "/api/v1/inventory/thresholds";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Thresholds_are_created_updated_read_and_removed_with_versions()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(sku: "THR-CRUD");

        // Create (201 + ETag), then the same key again updates (200, version 2); replaying the key returns the first answer.
        var key = Guid.NewGuid().ToString();
        var created = await kit.Owner.PostAsync(Thresholds, new { variantId = variant, minQuantity = 5m }, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(CatalogClient.ETag(1), created.Headers.ETag!.ToString());
        var first = (await created.Content.ReadFromJsonAsync<StockThresholdResponse>(Ct))!;
        Assert.Equal((variant, (Guid?)null, 5m, 1L), (first.VariantId, first.WarehouseId, first.MinQuantity, first.Version));
        Assert.Equal($"/api/v1/inventory/thresholds/{first.Id}", created.Headers.Location!.ToString());

        var replay = await kit.Owner.PostAsync(Thresholds, new { variantId = variant, minQuantity = 5m }, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.Id, (await replay.Content.ReadFromJsonAsync<StockThresholdResponse>(Ct))!.Id);

        var updated = await kit.Owner.OkAsync<StockThresholdResponse>(await kit.Owner.PostAsync(Thresholds, new { variantId = variant, minQuantity = 8m }));
        Assert.Equal((first.Id, 8m, 2L), (updated.Id, updated.MinQuantity, updated.Version));
        var same = await kit.Owner.OkAsync<StockThresholdResponse>(await kit.Owner.PostAsync(Thresholds, new { variantId = variant, minQuantity = 8m }));
        Assert.Equal(2L, same.Version); // unchanged: no write, no version bump

        // Override in the warehouse: its own row.
        var overrideRow = await kit.Owner.CreatedAsync<StockThresholdResponse>(Thresholds, new { variantId = variant, warehouseId = kit.WarehouseId, minQuantity = 2m });
        Assert.NotEqual(first.Id, overrideRow.Id);

        var get = await kit.Owner.GetAsync($"{Thresholds}/{first.Id}");
        Assert.Equal(CatalogClient.ETag(2), get.Headers.ETag!.ToString());
        var list = await kit.Owner.OkAsync<StockThresholdPageResponse>(await kit.Owner.GetAsync($"{Thresholds}?variantId={variant}"));
        Assert.Equal(new[] { first.Id, overrideRow.Id }.Order(), list.Items.Select(t => t.Id).Order());
        var inWarehouse = await kit.Owner.OkAsync<StockThresholdPageResponse>(await kit.Owner.GetAsync($"{Thresholds}?variantIds={variant},{Guid.NewGuid()}&warehouseId={kit.WarehouseId}"));
        Assert.Equal(overrideRow.Id, Assert.Single(inWarehouse.Items).Id);
        var paged = await kit.Owner.OkAsync<StockThresholdPageResponse>(await kit.Owner.GetAsync($"{Thresholds}?variantId={variant}&limit=1"));
        Assert.True(paged.Page.HasMore);
        var rest = await kit.Owner.OkAsync<StockThresholdPageResponse>(await kit.Owner.GetAsync($"{Thresholds}?variantId={variant}&limit=1&cursor={paged.Page.NextCursor}"));
        Assert.Equal(2, paged.Items.Concat(rest.Items).Select(t => t.Id).Distinct().Count());

        // PUT needs If-Match with the current version.
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Put, $"{Thresholds}/{first.Id}", new { minQuantity = 9m }), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Put, $"{Thresholds}/{first.Id}", new { minQuantity = 9m }, CatalogClient.ETag(1)),
            HttpStatusCode.PreconditionFailed,
            "concurrency.version_mismatch");
        var put = await kit.Owner.SendAsync(HttpMethod.Put, $"{Thresholds}/{first.Id}", new { minQuantity = 9m }, CatalogClient.ETag(2));
        Assert.Equal(CatalogClient.ETag(3), put.Headers.ETag!.ToString());
        Assert.Equal(9m, (await kit.Owner.OkAsync<StockThresholdResponse>(put)).MinQuantity);

        // DELETE with If-Match; afterwards the row is gone.
        await HttpAssert.ProblemAsync(await kit.Owner.DeleteAsync($"{Thresholds}/{first.Id}", CatalogClient.ETag(2)), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        Assert.Equal(HttpStatusCode.NoContent, (await kit.Owner.DeleteAsync($"{Thresholds}/{first.Id}", CatalogClient.ETag(3))).StatusCode);
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync($"{Thresholds}/{first.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await kit.Owner.DeleteAsync($"{Thresholds}/{first.Id}", CatalogClient.ETag(3)), HttpStatusCode.NotFound, "resource.not_found");

        // Audit: three changes of the default (5, 8, 9), the override, one removal — with from/to.
        var events = await ReportsKit.RowsAsync(
            fixture,
            "SELECT action, entity_id, changes::text FROM audit.audit_events WHERE tenant_id = $1 AND entity_type = 'stock_threshold' ORDER BY occurred_at, id",
            kit.TenantId);
        Assert.Equal(
            [AuditActions.InventoryThresholdSet, AuditActions.InventoryThresholdSet, AuditActions.InventoryThresholdSet, AuditActions.InventoryThresholdSet, AuditActions.InventoryThresholdRemoved],
            events.Select(e => (string)e[0]!));
        using var removal = JsonDocument.Parse((string)events[^1][2]!);
        Assert.Equal(9m, removal.RootElement.GetProperty("minQuantity").GetProperty("from").GetDecimal());
        Assert.Equal(JsonValueKind.Null, removal.RootElement.GetProperty("minQuantity").GetProperty("to").ValueKind);
        Assert.Equal(variant, removal.RootElement.GetProperty("variantId").GetGuid());
        using var change = JsonDocument.Parse((string)events[1][2]!);
        Assert.Equal((5m, 8m), (change.RootElement.GetProperty("minQuantity").GetProperty("from").GetDecimal(), change.RootElement.GetProperty("minQuantity").GetProperty("to").GetDecimal()));
    }

    [Fact]
    public async Task Invalid_thresholds_are_rejected_and_write_nothing()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(sku: "THR-BAD");
        var kilo = await ReportsKit.KilogramVariantAsync(kit, "THR-KG", 1m);

        foreach (var body in new object[]
        {
            new { variantId = variant, minQuantity = -1m },
            new { variantId = variant, minQuantity = 1.5m }, // adet does not allow fractions
            new { variantId = variant, minQuantity = 1.2345m },
            new { variantId = variant, minQuantity = 1_000_000_001m },
            new { variantId = variant },
            new { minQuantity = 1m },
            new { variantId = Guid.NewGuid(), minQuantity = 1m },
            new { variantId = variant, warehouseId = Guid.NewGuid(), minQuantity = 1m },
        })
        {
            await HttpAssert.ProblemAsync(await kit.Owner.PostAsync(Thresholds, body), HttpStatusCode.UnprocessableEntity, "validation.failed");
        }

        Assert.Equal(HttpStatusCode.Created, (await kit.Owner.PostAsync(Thresholds, new { variantId = kilo, minQuantity = 2.5m })).StatusCode);

        // Bulk is all or nothing: one bad row (duplicate key / remove with a value / unknown variant) rejects the batch.
        foreach (var items in new object[][]
        {
            [new { variantId = variant, minQuantity = 1m }, new { variantId = variant, minQuantity = 2m }],
            [new { variantId = variant, minQuantity = 1m }, new { variantId = kilo, minQuantity = 1m, remove = true }],
            [new { variantId = variant, minQuantity = 1m }, new { variantId = Guid.NewGuid(), minQuantity = 1m }],
            [],
            [.. Enumerable.Range(0, 501).Select(_ => new { variantId = variant, minQuantity = 1m })],
        })
        {
            await HttpAssert.ProblemAsync(await kit.Owner.PostAsync($"{Thresholds}/bulk", new { items }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        }

        var rows = await ReportsKit.RowsAsync(fixture, "SELECT variant_id, min_quantity FROM inventory.stock_thresholds WHERE tenant_id = $1", kit.TenantId);
        var row = Assert.Single(rows);
        Assert.Equal((kilo, 2.5m), ((Guid)row[0]!, (decimal)row[1]!));
    }

    [Fact]
    public async Task Bulk_sets_updates_removes_and_reports_unchanged_rows()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var a = await kit.VariantAsync(sku: "THR-BULK-A");
        var b = await kit.VariantAsync(sku: "THR-BULK-B");
        var c = await kit.VariantAsync(sku: "THR-BULK-C");

        var first = await Bulk(kit.Owner, new { variantId = a, minQuantity = 3m }, new { variantId = b, minQuantity = 4m }, new { variantId = b, warehouseId = kit.WarehouseId, minQuantity = 1m });
        Assert.Equal((3, 0, 0, 0), (first.Created, first.Updated, first.Removed, first.Unchanged));
        Assert.Equal([a, b, b], first.Items.Select(i => i.VariantId)); // request order

        var second = await Bulk(
            kit.Owner,
            new { variantId = a, minQuantity = 3m }, // unchanged
            new { variantId = b, minQuantity = 6m }, // updated
            new { variantId = b, warehouseId = kit.WarehouseId, remove = true }, // removed
            new { variantId = c, remove = true }); // nothing to remove
        Assert.Equal((0, 1, 1, 1), (second.Created, second.Updated, second.Removed, second.Unchanged));
        Assert.Equal([(a, 3m, 1L), (b, 6m, 2L)], second.Items.Select(i => (i.VariantId, i.MinQuantity, i.Version)));

        var truth = await ReportsKit.RowsAsync(
            fixture, "SELECT variant_id, warehouse_id, min_quantity FROM inventory.stock_thresholds WHERE tenant_id = $1 ORDER BY min_quantity", kit.TenantId);
        Assert.Equal([(a, (Guid?)null, 3m), (b, null, 6m)], truth.Select(r => ((Guid)r[0]!, (Guid?)r[1], (decimal)r[2]!)));
    }

    [Fact]
    public async Task Concurrent_bulk_writes_of_the_same_keys_never_duplicate_or_deadlock()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variants = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            variants.Add(await kit.VariantAsync(sku: $"THR-RACE-{i}"));
        }

        // Each writer sends the same keys in a different order with its own value.
        var writers = Enumerable.Range(1, 6).Select(n =>
        {
            var order = n % 2 == 0 ? variants : [.. Enumerable.Reverse(variants)];
            return kit.Owner.PostAsync($"{Thresholds}/bulk", new { items = order.Select(v => new { variantId = v, minQuantity = (decimal)n }).ToArray() });
        });
        var responses = await Task.WhenAll(writers);
        foreach (var response in responses)
        {
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        }

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        var rows = await ReportsKit.RowsAsync(
            fixture, "SELECT variant_id, count(*), min(min_quantity), max(min_quantity) FROM inventory.stock_thresholds WHERE tenant_id = $1 GROUP BY variant_id", kit.TenantId);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(1L, (long)r[1]!));

        // Every row carries the value of one single writer (a batch is atomic, not interleaved per row).
        Assert.Single(rows.Select(r => (decimal)r[2]!).Distinct());
    }

    [Fact]
    public async Task Warehouse_overrides_win_over_the_default_in_the_stock_list_and_the_report()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var second = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "W2", name = "İkinci Depo" });
        var a = await kit.VariantAsync(sku: "THR-PREC-A"); // default 5, override in W2 = 1
        var b = await kit.VariantAsync(10m, "THR-PREC-B"); // only overrides: MAIN 2, W2 3 (all warehouses = 5)
        var c = await kit.VariantAsync(sku: "THR-PREC-C"); // no threshold
        var d = await kit.VariantAsync(sku: "THR-PREC-D"); // default 0, never stocked
        await kit.ReceiveAsync(a, 4m);
        await kit.ReceiveAsync(a, 2m, second.Id);
        await kit.ReceiveAsync(b, 2m);
        await kit.ReceiveAsync(b, 4m, second.Id);
        await kit.ReceiveAsync(c, 1m);
        await kit.ApprovedOrderAsync(OrdersKit.Line(b, 1m)); // MAIN: available 1
        await Bulk(
            kit.Owner,
            new { variantId = a, minQuantity = 5m },
            new { variantId = a, warehouseId = second.Id, minQuantity = 1m },
            new { variantId = b, warehouseId = kit.WarehouseId, minQuantity = 2m },
            new { variantId = b, warehouseId = second.Id, minQuantity = 3m },
            new { variantId = d, minQuantity = 0m });

        // Stock list: effective minimum of the row's warehouse and the "kritik" flag over that warehouse's bins.
        var balances = await kit.Owner.OkAsync<BalancePageResponse>(await kit.Owner.GetAsync("/api/v1/inventory/balances?limit=200"));
        (decimal? Min, bool Below) Row(Guid variant, Guid warehouse)
        {
            var row = Assert.Single(balances.Items, x => x.VariantId == variant && x.WarehouseId == warehouse);
            return (row.MinQuantity, row.BelowMinimum);
        }

        Assert.Equal((5m, true), Row(a, kit.WarehouseId)); // 4 ≤ 5 (default)
        Assert.Equal((1m, false), Row(a, second.Id)); // 2 > 1 (override wins)
        Assert.Equal((2m, true), Row(b, kit.WarehouseId)); // 1 ≤ 2
        Assert.Equal((3m, false), Row(b, second.Id)); // 4 > 3
        Assert.Equal(((decimal?)null, false), Row(c, kit.WarehouseId));

        // Report, all warehouses: a = 6 vs default 5 → not critical; b = 5 vs Σ overrides 5 → critical; d never stocked, 0 ≤ 0.
        await AssertReportMatchesTruthAsync(kit, null, ["THR-PREC-B", "THR-PREC-D"]);
        await AssertReportMatchesTruthAsync(kit, kit.WarehouseId, ["THR-PREC-A", "THR-PREC-B", "THR-PREC-D"]);
        await AssertReportMatchesTruthAsync(kit, second.Id, ["THR-PREC-D"]);

        var all = await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, "/api/v1/reports/critical-stock");
        Assert.True(all.ThresholdsConfigured);
        Assert.Equal([5m, 0m], all.Items.Select(i => i.MinQuantity!.Value));
        var main = await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, $"/api/v1/reports/critical-stock?warehouseId={kit.WarehouseId}");
        Assert.Equal([5m, 2m, 0m], main.Items.Select(i => i.MinQuantity!.Value));
        Assert.Equal((2m, 1m, 1m), main.Items.Single(i => i.Id == b) is var row ? (row.OnHand, row.Reserved, row.Available) : default);

        // includeOutOfStock adds variants without a minimum whose available ≤ 0 (none here: c has 1) — and a new one.
        var e = await kit.VariantAsync(sku: "THR-PREC-E");
        var withOut = await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, "/api/v1/reports/critical-stock?includeOutOfStock=true");
        Assert.Equal(["THR-PREC-B", "THR-PREC-D", "THR-PREC-E"], withOut.Items.Select(i => i.Sku));
        Assert.Null(withOut.Items[^1].MinQuantity);
        await HttpAssert.ProblemAsync(
            await kit.Owner.GetAsync("/api/v1/reports/critical-stock?threshold=1&includeOutOfStock=true"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/critical-stock?includeOutOfStock=maybe"), HttpStatusCode.BadRequest, "request.malformed");

        // An archived variant is never listed even when under its minimum.
        await ReportsKit.ExecuteAsync(fixture, "UPDATE catalog.product_variants SET status = 'archived', archived_at = now() WHERE id = $1", d);
        Assert.Equal(["THR-PREC-B"], (await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, "/api/v1/reports/critical-stock")).Items.Select(i => i.Sku));

        // Export uses thresholds by default; the "Eşik" column carries each row's minimum.
        var csv = ReportsKit.ParseCsv(await ReportsKit.ExportOkAsync(kit.Owner, "critical-stock", new { warehouseId = kit.WarehouseId }));
        Assert.Equal(["THR-PREC-A", "THR-PREC-B"], csv.Skip(1).Select(r => r[0]));
        Assert.Equal(["5", "2"], csv.Skip(1).Select(r => r[^1]));
        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(kit.Owner, "orders", new { includeOutOfStock = true }), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task Other_tenants_cannot_see_or_change_thresholds()
    {
        var a = await OrdersKit.CreateAsync(fixture);
        var variant = await a.VariantAsync(sku: "THR-SECRET-A");
        var row = await a.Owner.CreatedAsync<StockThresholdResponse>(Thresholds, new { variantId = variant, warehouseId = a.WarehouseId, minQuantity = 4m });

        var b = await OrdersKit.CreateAsync(fixture);
        var own = await b.VariantAsync(sku: "THR-B");
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"{Thresholds}/{row.Id}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(
            await b.Owner.SendAsync(HttpMethod.Put, $"{Thresholds}/{row.Id}", new { minQuantity = 99m }, CatalogClient.ETag(1)), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.Owner.DeleteAsync($"{Thresholds}/{row.Id}", CatalogClient.ETag(1)), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await b.Owner.PostAsync(Thresholds, new { variantId = variant, minQuantity = 1m }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await b.Owner.PostAsync(Thresholds, new { variantId = own, warehouseId = a.WarehouseId, minQuantity = 1m }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(
            await b.Owner.PostAsync($"{Thresholds}/bulk", new { items = new[] { new { variantId = variant, warehouseId = (Guid?)a.WarehouseId, remove = true } } }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");
        Assert.Empty((await b.Owner.OkAsync<StockThresholdPageResponse>(await b.Owner.GetAsync($"{Thresholds}?variantId={variant}"))).Items);
        Assert.Empty((await b.Owner.OkAsync<StockThresholdPageResponse>(await b.Owner.GetAsync(Thresholds))).Items);
        Assert.False((await ReportsKit.OkAsync<CriticalStockReportResponse>(b.Owner, "/api/v1/reports/critical-stock")).ThresholdsConfigured);

        // A's row is untouched; A's owner through B's header is not a member there.
        var after = await a.Owner.OkAsync<StockThresholdResponse>(await a.Owner.GetAsync($"{Thresholds}/{row.Id}"));
        Assert.Equal((4m, 1L), (after.MinQuantity, after.Version));
        await HttpAssert.ProblemAsync(await a.Owner.Targeting(b.TenantId).GetAsync(Thresholds), HttpStatusCode.Forbidden, "tenant.access_denied");
    }

    [Theory]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Manager, true)]
    [InlineData(Roles.Sales, false)]
    [InlineData(Roles.WarehouseStaff, false)]
    [InlineData(Roles.Viewer, false)]
    public async Task Catalog_writers_manage_thresholds_and_every_stock_reader_sees_them(string role, bool canWrite)
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var variant = await kit.VariantAsync(sku: "THR-ROLE");
        var row = await kit.Owner.CreatedAsync<StockThresholdResponse>(Thresholds, new { variantId = variant, minQuantity = 2m });
        var member = await kit.Owner.AddMemberAsync(fixture, role);

        // inventory.read: every role.
        Assert.Equal(row.Id, Assert.Single((await member.OkAsync<StockThresholdPageResponse>(await member.GetAsync(Thresholds))).Items).Id);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"{Thresholds}/{row.Id}")).StatusCode);

        var writes = new[]
        {
            await member.PostAsync(Thresholds, new { variantId = variant, warehouseId = kit.WarehouseId, minQuantity = 1m }),
            await member.PostAsync($"{Thresholds}/bulk", new { items = new[] { new { variantId = variant, minQuantity = 3m } } }),
        };
        foreach (var response in writes)
        {
            if (canWrite)
            {
                Assert.True(response.IsSuccessStatusCode, $"{role}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
            }
            else
            {
                await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "auth.forbidden");
            }
        }

        var current = await kit.Owner.OkAsync<StockThresholdResponse>(await kit.Owner.GetAsync($"{Thresholds}/{row.Id}"));
        var put = await member.SendAsync(HttpMethod.Put, $"{Thresholds}/{row.Id}", new { minQuantity = 4m }, CatalogClient.ETag(current.Version));
        var delete = await member.DeleteAsync($"{Thresholds}/{row.Id}", CatalogClient.ETag(canWrite ? current.Version + 1 : current.Version));
        if (canWrite)
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        else
        {
            await HttpAssert.ProblemAsync(put, HttpStatusCode.Forbidden, "auth.forbidden");
            await HttpAssert.ProblemAsync(delete, HttpStatusCode.Forbidden, "auth.forbidden");
            Assert.Equal(2m, (await kit.Owner.OkAsync<StockThresholdResponse>(await kit.Owner.GetAsync($"{Thresholds}/{row.Id}"))).MinQuantity);
        }
    }

    /// <summary>
    /// The report in thresholds mode against SQL truth computed independently from the tables (superuser): effective
    /// minimum per the precedence rule, available = Σ on_hand − Σ reserved (no row = 0), active variants of active products.
    /// </summary>
    private async Task AssertReportMatchesTruthAsync(OrdersKit kit, Guid? warehouseId, string[] expected)
    {
        var truth = await ReportsKit.RowsAsync(
            fixture,
            """
            WITH m AS (
                SELECT t.variant_id,
                       CASE WHEN $2::uuid IS NULL
                            THEN coalesce((array_agg(t.min_quantity) FILTER (WHERE t.warehouse_id IS NULL))[1],
                                          sum(t.min_quantity) FILTER (WHERE t.warehouse_id IS NOT NULL))
                            ELSE coalesce((array_agg(t.min_quantity) FILTER (WHERE t.warehouse_id = $2))[1],
                                          (array_agg(t.min_quantity) FILTER (WHERE t.warehouse_id IS NULL))[1])
                       END AS min_quantity
                FROM inventory.stock_thresholds t WHERE t.tenant_id = $1 GROUP BY t.variant_id)
            SELECT v.sku
            FROM catalog.product_variants v
            JOIN catalog.products p ON p.tenant_id = v.tenant_id AND p.id = v.product_id
            JOIN m ON m.variant_id = v.id
            WHERE v.tenant_id = $1 AND v.status = 'active' AND p.status = 'active' AND m.min_quantity IS NOT NULL
              AND coalesce((SELECT sum(b.on_hand) - sum(b.reserved) FROM inventory.inventory_balances b
                            WHERE b.tenant_id = v.tenant_id AND b.variant_id = v.id AND ($2::uuid IS NULL OR b.warehouse_id = $2)), 0) <= m.min_quantity
            ORDER BY v.sku
            """,
            kit.TenantId,
            (object?)warehouseId ?? DBNull.Value);
        var report = await ReportsKit.OkAsync<CriticalStockReportResponse>(
            kit.Owner, "/api/v1/reports/critical-stock" + (warehouseId is { } id ? $"?warehouseId={id}" : string.Empty));
        Assert.Equal(CriticalStockModes.Thresholds, report.Mode);
        Assert.Equal(expected, truth.Select(r => (string)r[0]!));
        Assert.Equal(expected, report.Items.Select(i => i.Sku));
    }

    private static async Task<BulkStockThresholdResponse> Bulk(CatalogClient client, params object[] items)
    {
        var response = await client.PostAsync($"{Thresholds}/bulk", new { items });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"bulk: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<BulkStockThresholdResponse>(Ct))!;
    }
}

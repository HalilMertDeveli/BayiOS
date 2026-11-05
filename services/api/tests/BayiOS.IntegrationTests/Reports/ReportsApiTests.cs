using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.IntegrationTests.Warehouse;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Reports;

/// <summary>
/// #53 — every report's numbers against the database (superuser truth queries grouped in C# by Europe/Istanbul day),
/// role visibility (footnote 4 for sales, warehouse staff none, AI usage owner/admin only), cross-tenant isolation and
/// filter validation. Synthetic tenants built through the API.
/// </summary>
public sealed class ReportsApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Critical_stock_lists_exactly_the_active_variants_at_or_below_the_threshold()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var plenty = await kit.VariantAsync(10m, "CRT-PLENTY");
        var low = await kit.VariantAsync(10m, "CRT-LOW");
        var never = await kit.VariantAsync(10m, "CRT-NEVER");
        var reserved = await kit.VariantAsync(10m, "CRT-RESERVED");
        var archived = await kit.VariantAsync(10m, "CRT-ARCHIVED");
        await kit.ReceiveAsync(plenty, 10m);
        await kit.ReceiveAsync(low, 2m);
        await kit.ReceiveAsync(reserved, 5m);
        await kit.ApprovedOrderAsync(OrdersKit.Line(reserved, 4m)); // available 1
        await ReportsKit.ExecuteAsync(fixture, "UPDATE catalog.product_variants SET status = 'archived', archived_at = now() WHERE id = $1", archived);

        var report = await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, "/api/v1/reports/critical-stock?threshold=2");
        Assert.Equal(2m, report.Threshold);
        Assert.Equal(["CRT-LOW", "CRT-NEVER", "CRT-RESERVED"], report.Items.Select(i => i.Sku));
        var reservedRow = report.Items.Single(i => i.Id == reserved);
        Assert.Equal((5m, 4m, 1m), (reservedRow.OnHand, reservedRow.Reserved, reservedRow.Available));
        Assert.Equal((0m, 0m, 0m), report.Items.Single(i => i.Id == never) is var n ? (n.OnHand, n.Reserved, n.Available) : default);

        // SQL truth: active variants of active products whose Σ on_hand − Σ reserved ≤ threshold (no row = 0).
        var truth = await ReportsKit.RowsAsync(
            fixture,
            """
            SELECT v.sku FROM catalog.product_variants v JOIN catalog.products p ON p.tenant_id = v.tenant_id AND p.id = v.product_id
            WHERE v.tenant_id = $1 AND v.status = 'active' AND p.status = 'active'
              AND coalesce((SELECT sum(b.on_hand) - sum(b.reserved) FROM inventory.inventory_balances b
                            WHERE b.tenant_id = v.tenant_id AND b.variant_id = v.id), 0) <= 2
            ORDER BY v.sku
            """,
            kit.TenantId);
        Assert.Equal(truth.Select(r => (string)r[0]!), report.Items.Select(i => i.Sku));

        // Threshold 0 keeps only the never-stocked one; a higher threshold includes the rest.
        Assert.Equal(["CRT-NEVER"], (await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, "/api/v1/reports/critical-stock")).Items.Select(i => i.Sku));
        Assert.Equal(4, (await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, "/api/v1/reports/critical-stock?threshold=10")).Items.Count);

        // Keyset paging returns the same rows once each.
        var paged = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ReportsKit.OkAsync<CriticalStockReportResponse>(
                kit.Owner, "/api/v1/reports/critical-stock?threshold=2&limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            paged.AddRange(page.Items.Select(i => i.Sku));
            cursor = page.Page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(report.Items.Select(i => i.Sku), paged);

        // Per warehouse: a second, empty warehouse makes every active variant critical there.
        var second = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "W2", name = "İkinci Depo" });
        var inSecond = await ReportsKit.OkAsync<CriticalStockReportResponse>(kit.Owner, $"/api/v1/reports/critical-stock?warehouseId={second.Id}");
        Assert.Equal(4, inSecond.Items.Count);
        Assert.All(inSecond.Items, i => Assert.Equal(0m, i.OnHand));

        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync($"/api/v1/reports/critical-stock?warehouseId={Guid.NewGuid()}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/critical-stock?threshold=-1"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/critical-stock?threshold=1.23456"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/critical-stock?limit=501"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/critical-stock?cursor=bm9wZQ"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/critical-stock?sort=x"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Orders_report_counts_and_totals_per_istanbul_day_and_status_match_the_orders()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var a = await kit.VariantAsync(100m);
        var b = await kit.VariantAsync(37.5m);
        await kit.ReceiveAsync(a, 50m);
        await kit.ReceiveAsync(b, 50m);
        await kit.CreateOrderAsync(OrdersKit.Line(a, 1m));
        await kit.SubmittedOrderAsync(OrdersKit.Line(b, 2m));
        await kit.ApprovedOrderAsync(OrdersKit.Line(a, 2m), OrdersKit.Line(b, 3m));
        var late = await kit.ApprovedOrderAsync(OrdersKit.Line(a, 1m));

        // 22:30 UTC is already the next day in Istanbul (UTC+3): the order must land on that day.
        await ReportsKit.ExecuteAsync(fixture, "UPDATE orders.orders SET created_at = $2 WHERE id = $1", late.Order.Id, new DateTimeOffset(2026, 10, 9, 22, 30, 0, TimeSpan.Zero));

        var report = await ReportsKit.OkAsync<OrdersReportResponse>(kit.Owner, "/api/v1/reports/orders?from=2026-10-01&to=" + ReportsKit.Day(ReportsKit.Today.AddDays(1)));
        Assert.True(report.AmountsVisible);

        var truth = (await ReportsKit.RowsAsync(fixture, "SELECT created_at, status, currency, grand_total FROM orders.orders WHERE tenant_id = $1", kit.TenantId))
            .GroupBy(r => (Day: ReportsKit.DayOf(r[0]!), Status: (string)r[1]!, Currency: (string)r[2]!))
            .Select(g => (g.Key.Day, g.Key.Status, g.Key.Currency, Count: (long)g.Count(), Total: g.Sum(r => (decimal)r[3]!)))
            .OrderBy(t => t.Day).ThenBy(t => t.Status, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            truth,
            report.Days.Select(d => (d.Day, d.Status, d.Currency, d.OrderCount, d.GrandTotal!.Value)).OrderBy(t => t.Day).ThenBy(t => t.Status, StringComparer.Ordinal).ToList());
        Assert.Contains(report.Days, d => d.Day == new DateOnly(2026, 10, 10) && d.Status == "approved" && d.OrderCount == 1);
        Assert.Equal(truth.Sum(t => t.Total), report.ByStatus.Sum(s => s.GrandTotal!.Value));
        Assert.Equal(2, report.ByStatus.Single(s => s.Status == "approved").OrderCount);

        // Status filter, and the default range (last 30 days) leaves the October order out.
        var approvedOnly = await ReportsKit.OkAsync<OrdersReportResponse>(kit.Owner, "/api/v1/reports/orders?status=approved&from=2026-10-01&to=" + ReportsKit.Day(ReportsKit.Today.AddDays(1)));
        Assert.All(approvedOnly.Days, d => Assert.Equal("approved", d.Status));
        Assert.Equal(2, approvedOnly.Days.Sum(d => d.OrderCount));
        var recent = await ReportsKit.OkAsync<OrdersReportResponse>(kit.Owner, "/api/v1/reports/orders");
        Assert.Equal(ReportsKit.Today, recent.To);
        Assert.Equal(ReportsKit.Today.AddDays(-29), recent.From);

        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/orders?status=shipped,lost"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/orders?from=2026-02-01&to=2026-01-01"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/orders?from=2025-01-01&to=2026-01-02"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/orders?from=01.02.2026"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Top_products_rank_committed_order_lines_by_quantity_with_revenue()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var first = await kit.VariantAsync(10m, "TOP-A");
        var second = await kit.VariantAsync(25m, "TOP-B");
        var third = await kit.VariantAsync(5m, "TOP-C");
        foreach (var v in new[] { first, second, third })
        {
            await kit.ReceiveAsync(v, 100m);
        }

        await kit.ApprovedOrderAsync(OrdersKit.Line(first, 3m), OrdersKit.Line(second, 4m));
        await kit.ApprovedOrderAsync(OrdersKit.Line(first, 2m));
        await kit.CreateOrderAsync(OrdersKit.Line(third, 100m)); // draft: not ordered yet
        var cancelled = await kit.ApprovedOrderAsync(OrdersKit.Line(third, 50m));
        await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, await kit.GetAsync(cancelled.Order.Id), "cancel", new { reason = "Test iptali" }));

        var report = await ReportsKit.OkAsync<TopProductsReportResponse>(kit.Owner, "/api/v1/reports/top-products?" + ReportsKit.Range);
        Assert.Equal(["TOP-A", "TOP-B"], report.Items.Select(i => i.Sku));
        Assert.Equal([1, 2], report.Items.Select(i => i.Rank));

        var truth = (await ReportsKit.RowsAsync(
                fixture,
                """
                SELECT l.variant_id, l.quantity, l.line_total, o.id FROM orders.order_lines l JOIN orders.orders o ON o.id = l.order_id
                WHERE o.tenant_id = $1 AND o.status IN ('approved', 'picking', 'packed', 'shipped', 'completed')
                """,
                kit.TenantId))
            .GroupBy(r => (Guid)r[0]!)
            .ToDictionary(g => g.Key, g => (Quantity: g.Sum(r => (decimal)r[1]!), Orders: g.Select(r => r[3]).Distinct().LongCount(), Revenue: g.Sum(r => (decimal)r[2]!)));
        Assert.Equal(truth.Count, report.Items.Count);
        foreach (var item in report.Items)
        {
            Assert.Equal(truth[item.Id], (item.Quantity, item.OrderCount, item.Revenue!.Value));
        }

        Assert.Equal((5m, 2L, 50m), (report.Items[0].Quantity, report.Items[0].OrderCount, report.Items[0].Revenue!.Value));
        Assert.Single((await ReportsKit.OkAsync<TopProductsReportResponse>(kit.Owner, "/api/v1/reports/top-products?limit=1&" + ReportsKit.Range)).Items);
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/reports/top-products?limit=0"), HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Stock_movement_summary_matches_the_ledger_per_day_and_type()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var v = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(v, 20m);
        var kg = await ReportsKit.KilogramVariantAsync(kit, "KG-1", 10m);
        await kit.ReceiveAsync(kg, 5.5m);
        var adjust = await kit.Owner.PostAsync("/api/v1/inventory/adjustments", new { variantId = v, warehouseId = kit.WarehouseId, type = "adjustment_out", quantity = 3m, reason = "Sayım farkı" });
        Assert.Equal(HttpStatusCode.Created, adjust.StatusCode);
        var order = await kit.ApprovedOrderAsync(OrdersKit.Line(v, 4m));
        await kit.ShipAsync(order, (order.Lines[0].Id, 4m));

        var report = await ReportsKit.OkAsync<StockMovementsReportResponse>(kit.Owner, "/api/v1/reports/stock-movements?" + ReportsKit.Range);
        var truth = (await ReportsKit.RowsAsync(fixture, "SELECT occurred_at, type, quantity FROM inventory.inventory_movements WHERE tenant_id = $1", kit.TenantId))
            .GroupBy(r => (Day: ReportsKit.DayOf(r[0]!), Type: (string)r[1]!))
            .Select(g => (g.Key.Day, g.Key.Type, Count: (long)g.Count(), In: g.Where(r => (decimal)r[2]! > 0).Sum(r => (decimal)r[2]!), Out: -g.Where(r => (decimal)r[2]! < 0).Sum(r => (decimal)r[2]!)))
            .OrderBy(t => t.Day).ThenBy(t => t.Type, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(truth, report.Days.Select(d => (d.Day, d.Type, d.MovementCount, d.QuantityIn, d.QuantityOut)).ToList());
        Assert.Equal((2L, 25.5m, 0m), report.Totals.Single(t => t.Type == "receipt") is var r ? (r.MovementCount, r.QuantityIn, r.QuantityOut) : default);
        Assert.Equal(4m, report.Totals.Single(t => t.Type == "shipment_out").QuantityOut);

        var other = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "W2", name = "İkinci Depo" });
        Assert.Empty((await ReportsKit.OkAsync<StockMovementsReportResponse>(kit.Owner, $"/api/v1/reports/stock-movements?warehouseId={other.Id}&{ReportsKit.Range}")).Days);
    }

    [Fact]
    public async Task Picking_task_report_counts_created_completed_and_cancelled_tasks_per_day()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var v = await kit.VariantAsync(10m);
        await kit.ReceiveAsync(v, 30m);
        var shipped = await kit.ApprovedOrderAsync(OrdersKit.Line(v, 2m));
        var shorted = await kit.ApprovedOrderAsync(OrdersKit.Line(v, 3m));
        var cancelled = await kit.ApprovedOrderAsync(OrdersKit.Line(v, 1m));
        await kit.ApprovedOrderAsync(OrdersKit.Line(v, 1m)); // stays open
        await kit.ShipAsync(shipped, (shipped.Lines[0].Id, 2m));
        var task = await Fulfillment.ActiveTaskAsync(kit.Owner, shorted.Order.Id);
        await Fulfillment.PickAndCompleteAsync(kit.Owner, task, new Dictionary<Guid, decimal> { [shorted.Lines[0].Id] = 1m });
        await OrdersKit.ReadAsync(await OrdersKit.ActAsync(kit.Owner, await kit.GetAsync(cancelled.Order.Id), "cancel", new { reason = "Müşteri vazgeçti" }));

        var report = await ReportsKit.OkAsync<PickingTasksReportResponse>(kit.Owner, "/api/v1/reports/picking-tasks?" + ReportsKit.Range);
        var tasks = await ReportsKit.RowsAsync(
            fixture,
            "SELECT created_at, completed_at, cancelled_at, status, started_at FROM warehouse.picking_tasks WHERE tenant_id = $1 AND type = 'pick'",
            kit.TenantId);
        Assert.Equal(tasks.Count, report.Totals.Created);
        Assert.Equal(4, report.Totals.Created);
        Assert.Equal(2, report.Totals.Completed);
        Assert.Equal(1, report.Totals.CompletedWithShortage);
        Assert.Equal(1, report.Totals.Cancelled);
        Assert.Equal(tasks.Count(t => (string)t[3]! is "completed" or "completed_with_shortage"), report.Totals.Completed);
        Assert.NotNull(report.Totals.AveragePickMinutes);
        Assert.True(report.Totals.AveragePickMinutes >= 0);
        foreach (var day in report.Days)
        {
            Assert.Equal(tasks.Count(t => ReportsKit.DayOf(t[0]!) == day.Day), day.Created);
            Assert.Equal(tasks.Count(t => t[1] is { } at && ReportsKit.DayOf(at) == day.Day), day.Completed);
        }
    }

    [Fact]
    public async Task Ai_usage_report_sums_hourly_counters_per_istanbul_day_for_owner_and_admin_only()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        // 20:00 and 21:00 UTC on the 2nd are the 2nd and 3rd in Istanbul.
        var hours = new[]
        {
            (Hour: new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero), Calls: 3, Input: 1000L),
            (Hour: new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero), Calls: 2, Input: 400L),
            (Hour: new DateTimeOffset(2026, 10, 2, 21, 0, 0, TimeSpan.Zero), Calls: 5, Input: 900L),
        };
        foreach (var (hour, calls, input) in hours)
        {
            await ReportsKit.ExecuteAsync(
                fixture,
                """
                INSERT INTO integrations.ai_usage (tenant_id, usage_hour, parse_requests, ai_calls, ai_failures, fallbacks, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
                VALUES ($1, $2, $3, $3, 1, 1, $4, $4 / 2, 10, 5)
                """,
                owner.TenantId,
                hour,
                calls,
                input);
        }

        var report = await ReportsKit.OkAsync<AiUsageReportResponse>(owner, "/api/v1/reports/ai-usage?from=2026-10-01&to=2026-10-05");
        Assert.Equal(
            [(new DateOnly(2026, 10, 2), 5L, 1400L, 2L), (new DateOnly(2026, 10, 3), 5L, 900L, 1L)],
            report.Days.Select(d => (d.Day, d.AiCalls, d.InputTokens, d.Fallbacks)).ToList());
        Assert.Equal((10L, 2300L, 1150L, 30L, 15L), (report.Totals.AiCalls, report.Totals.InputTokens, report.Totals.OutputTokens, report.Totals.CacheReadTokens, report.Totals.CacheWriteTokens));

        var admin = await owner.AddMemberAsync(fixture, Roles.Admin);
        Assert.Equal(report.Totals, (await ReportsKit.OkAsync<AiUsageReportResponse>(admin, "/api/v1/reports/ai-usage?from=2026-10-01&to=2026-10-05")).Totals);
        foreach (var role in new[] { Roles.Manager, Roles.Viewer, Roles.Sales, Roles.WarehouseStaff })
        {
            var member = await owner.AddMemberAsync(fixture, role);
            await HttpAssert.ProblemAsync(await member.GetAsync("/api/v1/reports/ai-usage"), HttpStatusCode.Forbidden, "auth.forbidden");
            await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(member, "ai-usage"), HttpStatusCode.Forbidden, "auth.forbidden");
        }
    }

    [Fact]
    public async Task Sales_sees_only_order_reports_warehouse_staff_none_and_viewer_all_but_ai_usage()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var v = await kit.VariantAsync(12m);
        await kit.ReceiveAsync(v, 10m);
        await kit.ApprovedOrderAsync(OrdersKit.Line(v, 2m));

        var sales = await kit.Owner.AddMemberAsync(fixture, Roles.Sales);
        var orders = await ReportsKit.OkAsync<OrdersReportResponse>(sales, "/api/v1/reports/orders");
        Assert.True(orders.AmountsVisible); // sales holds prices.read.sell
        Assert.Equal(1, orders.ByStatus.Sum(s => s.OrderCount));
        Assert.Single((await ReportsKit.OkAsync<TopProductsReportResponse>(sales, "/api/v1/reports/top-products")).Items);
        Assert.NotEmpty(await ReportsKit.ExportOkAsync(sales, "orders"));
        Assert.NotEmpty(await ReportsKit.ExportOkAsync(sales, "top-products"));
        foreach (var report in new[] { "critical-stock", "stock-movements", "picking-tasks" })
        {
            await HttpAssert.ProblemAsync(await sales.GetAsync($"/api/v1/reports/{report}"), HttpStatusCode.Forbidden, "reports.orders_and_customers_only");
            await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(sales, report), HttpStatusCode.Forbidden, "reports.orders_and_customers_only");
        }

        var staff = await kit.Owner.AddMemberAsync(fixture, Roles.WarehouseStaff);
        foreach (var report in new[] { "critical-stock", "orders", "top-products", "stock-movements", "picking-tasks" })
        {
            await HttpAssert.ProblemAsync(await staff.GetAsync($"/api/v1/reports/{report}"), HttpStatusCode.Forbidden, "auth.forbidden");
            await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(staff, report), HttpStatusCode.Forbidden, "auth.forbidden");
        }

        var viewer = await kit.Owner.AddMemberAsync(fixture, Roles.Viewer);
        foreach (var report in new[] { "critical-stock", "orders", "top-products", "stock-movements", "picking-tasks" })
        {
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/reports/{report}")).StatusCode);
        }

        // Rejected exports (403) take no slot and write no audit event.
        Assert.Equal(2L, await kit.CountAsync("SELECT count(*) FROM reports.export_requests WHERE tenant_id = $1"));
        Assert.Equal(2L, await kit.CountAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'export.downloaded'"));
    }

    [Fact]
    public async Task Reports_and_exports_of_one_tenant_never_contain_another_tenants_data()
    {
        var a = await OrdersKit.CreateAsync(fixture);
        var sku = "SECRET-A-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var secret = await a.VariantAsync(99m, sku);
        await a.ReceiveAsync(secret, 1m);
        await a.ApprovedOrderAsync(OrdersKit.Line(secret, 1m));

        var b = await OrdersKit.CreateAsync(fixture);
        foreach (var report in new[] { "critical-stock?threshold=1000", "orders", "top-products", "stock-movements", "picking-tasks", "ai-usage" })
        {
            var response = await b.Owner.GetAsync("/api/v1/reports/" + report);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(sku, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty((await ReportsKit.OkAsync<OrdersReportResponse>(b.Owner, "/api/v1/reports/orders")).Days);
        Assert.Empty((await ReportsKit.OkAsync<TopProductsReportResponse>(b.Owner, "/api/v1/reports/top-products")).Items);

        // A's warehouse id through B's tenant: unknown there.
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/reports/critical-stock?warehouseId={a.WarehouseId}"), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(b.Owner, "stock-movements", new { warehouseId = a.WarehouseId }), HttpStatusCode.NotFound, "resource.not_found");

        foreach (var report in new[] { "critical-stock", "orders", "top-products" })
        {
            var csv = System.Text.Encoding.UTF8.GetString(await ReportsKit.ExportOkAsync(b.Owner, report));
            Assert.DoesNotContain(sku, csv, StringComparison.OrdinalIgnoreCase);
        }

        // B's owner sending A's tenant header is not a member there.
        await HttpAssert.ProblemAsync(await b.Owner.Targeting(a.TenantId).GetAsync("/api/v1/reports/orders"), HttpStatusCode.Forbidden, "tenant.access_denied");
    }
}

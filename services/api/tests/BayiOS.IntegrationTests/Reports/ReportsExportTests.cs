using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.IntegrationTests.Reports;

/// <summary>
/// #53 — CSV/XLSX export: formula-injection neutralisation of every text cell in both formats (threat-model §6.1),
/// the file parses back (XLSX through ExcelDataReader), numbers stay numbers, the hourly limit per tenant (§6.5, 429 +
/// Retry-After, shared by roles, not consumed by rejected or replayed requests), one data-free audit event per export,
/// and exports work for every report-capable role without any entitlement (roles-permissions §5).
/// </summary>
public sealed class ReportsExportTests(ApiFixture fixture)
{
    /// <summary>OWASP CSV-injection payloads; TAB/CR ones are written directly (the catalog API trims whitespace).</summary>
    private static readonly string[] ApiPayloads =
    [
        "=HYPERLINK(\"http://evil.example/x\",\"Tıkla\")",
        "+cmd|' /C calc'!A0",
        "-2+3+cmd|' /C calc'!A0",
        "@SUM(1+1)*cmd|' /C calc'!A0",
    ];

    private static readonly string[] DirectPayloads = ["\t=1+1 TAB", "\r=1+1 CR"];

    private static readonly string[] UnknownStatus = ["lost"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_text_cell_starting_with_a_formula_character_is_neutralised_in_csv_and_xlsx()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var unit = await kit.Owner.CreateUnitAsync("kutu", "Kutu");
        var variants = new List<Guid>();
        for (var i = 0; i < ApiPayloads.Length + DirectPayloads.Length; i++)
        {
            var name = i < ApiPayloads.Length ? ApiPayloads[i] : "Sentetik ürün " + i;
            var product = await kit.Owner.CreateSimpleProductAsync(unit.Id, name, $"INJ-{i:D2}");
            var variant = Assert.Single(product.Variants).Id;
            variants.Add(variant);
            await kit.PriceAsync(kit.PriceListId, variant, 10m);
            await kit.ReceiveAsync(variant, 5m);
            if (i >= ApiPayloads.Length)
            {
                await ReportsKit.ExecuteAsync(fixture, "UPDATE catalog.products SET name = $2 WHERE id = $1", product.Id, DirectPayloads[i - ApiPayloads.Length]);
            }
        }

        // Ordered lines snapshot the (direct) names too, so the order-based export carries them as well.
        await kit.ApprovedOrderAsync([.. variants.Select(v => OrdersKit.Line(v, 1m))]);
        var payloads = ApiPayloads.Concat(DirectPayloads).ToList();

        var csv = ReportsKit.ParseCsv(await ReportsKit.ExportOkAsync(kit.Owner, "critical-stock", new { threshold = 100m }));
        Assert.Equal(["Stok Kodu", "Ürün", "Birim", "Eldeki", "Rezerve", "Kullanılabilir", "Eşik"], csv[0]);
        var names = csv.Skip(1).Select(r => r[1]).ToList();
        Assert.Equal(payloads.Count, names.Count);
        foreach (var payload in payloads)
        {
            Assert.Contains("'" + payload, names);
        }

        Assert.All(csv.Skip(1), r => Assert.Equal(("5", "1", "4"), (r[3], r[4], r[5]))); // numbers are not text: never prefixed
        var xlsx = ReportsKit.ParseXlsx(await ReportsKit.ExportOkAsync(kit.Owner, "critical-stock", new { format = "xlsx", threshold = 100m }));
        Assert.Equal("Ürün", xlsx[0][1]);
        var xlsxNames = xlsx.Skip(1).Select(r => (string)r[1]!).ToList();
        foreach (var payload in payloads)
        {
            // XML normalises a lone CR to LF on read; what matters is the leading apostrophe before it.
            Assert.Contains(xlsxNames, n => n.StartsWith('\'') && n[1..].Replace('\n', '\r') == payload);
        }

        Assert.All(xlsxNames, n => Assert.StartsWith("'", n, StringComparison.Ordinal));
        Assert.All(xlsx.Skip(1), r => Assert.IsType<double>(r[3]));

        var top = ReportsKit.ParseXlsx(await ReportsKit.ExportOkAsync(kit.Owner, "top-products", new { format = "xlsx" }));
        Assert.All(top.Skip(1), r => Assert.StartsWith("'", (string)r[2]!, StringComparison.Ordinal));
        var topCsv = ReportsKit.ParseCsv(await ReportsKit.ExportOkAsync(kit.Owner, "top-products"));
        Assert.All(topCsv.Skip(1), r => Assert.StartsWith("'", r[2], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exports_parse_back_with_typed_numbers_turkish_csv_conventions_and_all_reports()
    {
        var kit = await OrdersKit.CreateAsync(fixture);
        var v = await ReportsKit.KilogramVariantAsync(kit, "EXP-1", 12.5m);
        await kit.ReceiveAsync(v, 7.25m);
        await kit.ApprovedOrderAsync(OrdersKit.Line(v, 2m));

        var csv = ReportsKit.ParseCsv(await ReportsKit.ExportOkAsync(kit.Owner, "critical-stock", new { threshold = 10m }));
        Assert.Equal(["EXP-1", "7,25", "2", "5,25", "10"], new[] { csv[1][0], csv[1][3], csv[1][4], csv[1][5], csv[1][6] });

        var orders = ReportsKit.ParseXlsx(await ReportsKit.ExportOkAsync(kit.Owner, "orders", new { format = "xlsx" }));
        Assert.Equal(["Gün", "Durum", "Para Birimi", "Sipariş Sayısı", "Genel Toplam"], orders[0].Select(c => (string)c!));
        var row = Assert.Single(orders.Skip(1));
        Assert.Equal(ReportsKit.Today.ToDateTime(TimeOnly.MinValue), Assert.IsType<DateTime>(row[0]));
        Assert.Equal("approved", row[1]);
        Assert.Equal(1d, row[3]);
        Assert.Equal(30d, row[4]); // 2 × 12.5 + 20 % VAT

        foreach (var report in new[] { "stock-movements", "picking-tasks", "ai-usage" })
        {
            foreach (var format in new[] { "csv", "xlsx" })
            {
                var response = await ReportsKit.ExportAsync(kit.Owner, report, new { format });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(
                    format == "csv" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    response.Content.Headers.ContentType?.MediaType);
                Assert.EndsWith("." + format, response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
                var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
                Assert.NotEmpty(format == "csv" ? ReportsKit.ParseCsv(bytes) : ReportsKit.ParseXlsx(bytes));
            }
        }

        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(kit.Owner, "orders", new { format = "pdf" }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(kit.Owner, "orders", new { threshold = 3 }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(kit.Owner, "critical-stock", new { from = "2026-01-01" }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(kit.Owner, "orders", new { statuses = UnknownStatus }), HttpStatusCode.UnprocessableEntity, "validation.failed");
        var noKey = await kit.Owner.Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/reports/orders/export")
            {
                Headers = { { "X-Tenant-Id", kit.TenantId.ToString() } },
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            },
            Ct);
        await HttpAssert.ProblemAsync(noKey, HttpStatusCode.PreconditionRequired, "precondition.required");
    }

    [Fact]
    public async Task Ten_exports_per_hour_per_tenant_then_429_with_retry_after_and_one_data_free_audit_event_each()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var viewer = await owner.AddMemberAsync(fixture, Roles.Viewer);
        var replayKey = Guid.NewGuid().ToString();
        var first = await ReportsKit.ExportAsync(owner, "orders", new { format = "xlsx" }, replayKey);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBytes = await first.Content.ReadAsByteArrayAsync(Ct);

        // Rejected requests take no slot.
        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(owner, "orders", new { format = "doc" }), HttpStatusCode.UnprocessableEntity, "validation.failed");

        for (var i = 1; i < 10; i++)
        {
            // The limit is per tenant: the owner's and the viewer's exports share it.
            Assert.NotEmpty(await ReportsKit.ExportOkAsync(i % 2 == 0 ? owner : viewer, i % 3 == 0 ? "critical-stock" : "orders"));
        }

        foreach (var client in new[] { owner, viewer })
        {
            var refused = await ReportsKit.ExportAsync(client, "top-products");
            await HttpAssert.ProblemAsync(refused, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
            var retryAfter = int.Parse(refused.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture);
            Assert.InRange(retryAfter, 1, 3600);
        }

        // An idempotent replay returns the stored file and consumes nothing.
        var replay = await ReportsKit.ExportAsync(owner, "orders", new { format = "xlsx" }, replayKey);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(firstBytes, await replay.Content.ReadAsByteArrayAsync(Ct));

        // Another tenant is not affected.
        var other = await CatalogClient.NewTenantOwnerAsync(fixture);
        Assert.NotEmpty(await ReportsKit.ExportOkAsync(other, "orders"));

        var exports = await ReportsKit.RowsAsync(fixture, "SELECT report, format, row_count FROM reports.export_requests WHERE tenant_id = $1", owner.TenantId);
        Assert.Equal(10, exports.Count);
        var audit = await ReportsKit.RowsAsync(
            fixture,
            "SELECT action, entity_type, changes::text, actor_user_id FROM audit.audit_events WHERE tenant_id = $1 AND action LIKE 'export.%'",
            owner.TenantId);
        Assert.Equal(10, audit.Count);
        Assert.All(audit, a =>
        {
            Assert.Equal("export.downloaded", a[0]);
            Assert.Equal("report_export", a[1]);
            using var changes = JsonDocument.Parse((string)a[2]!);
            Assert.Equal(["format", "report", "rows"], changes.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        });
        Assert.Equal(5, audit.Count(a => (Guid)a[3]! == viewer.UserId));
    }

    [Fact]
    public async Task Exports_are_available_to_every_report_role_without_an_entitlement()
    {
        // A fresh tenant has no paid module: exports must still work (roles-permissions §1.5 / §5 "asla kilitlenmez").
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        foreach (var role in new[] { Roles.Owner, Roles.Admin, Roles.Manager, Roles.Viewer })
        {
            var member = role == Roles.Owner ? owner : await owner.AddMemberAsync(fixture, role);
            Assert.NotEmpty(await ReportsKit.ExportOkAsync(member, "critical-stock", new { format = "xlsx" }));
        }

        Assert.NotEmpty(await ReportsKit.ExportOkAsync(owner, "ai-usage"));
    }

    [Fact]
    public async Task Exports_over_the_row_limit_are_refused_and_not_counted()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var unit = await owner.CreateUnitAsync();
        var product = await owner.CreatedAsync<ProductResponse>("/api/v1/products", new { name = "Sentetik ürün", defaultUnitId = unit.Id, taxRate = 20m, sku = "ROW-00000" });
        // 20,001 never-stocked active variants, inserted directly (the API would take minutes).
        await ReportsKit.ExecuteAsync(
            fixture,
            """
            INSERT INTO catalog.product_variants (id, tenant_id, product_id, sku, unit_id, status, option_values, attributes, row_version, created_at)
            SELECT gen_random_uuid(), $1, $2, 'ROW-' || lpad(g::text, 5, '0'), $3, 'active', '{}', '{}', 1, now()
            FROM generate_series(1, 20000) g
            """,
            owner.TenantId,
            product.Id,
            unit.Id);

        await HttpAssert.ProblemAsync(await ReportsKit.ExportAsync(owner, "critical-stock"), HttpStatusCode.UnprocessableEntity, "reports.export_too_large");
        Assert.Empty(await ReportsKit.RowsAsync(fixture, "SELECT id FROM reports.export_requests WHERE tenant_id = $1", owner.TenantId));

        // One variant fewer fits exactly at the limit (header + 20,000 rows).
        await ReportsKit.ExecuteAsync(fixture, "UPDATE catalog.product_variants SET status = 'archived', archived_at = now() WHERE tenant_id = $1 AND sku = 'ROW-20000'", owner.TenantId);
        Assert.Equal(20_001, ReportsKit.ParseCsv(await ReportsKit.ExportOkAsync(owner, "critical-stock")).Count);
    }
}

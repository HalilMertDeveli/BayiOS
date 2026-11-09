using System.Diagnostics;
using System.Net;
using System.Text;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.TestSupport.BulkImport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BayiOS.IntegrationTests.BulkImport;

/// <summary>
/// End-to-end imports driven through the real job processor (#21–#25): preview, error report, execution, re-run,
/// 10k rows, cancel/resume, price-only import, actor re-check. The tests of this class run sequentially, so no other
/// import chunk is in flight while one of them counts chunks.
/// </summary>
[Collection(ImportJobsTestGroup.Name)]
public class ImportsApiTests(ApiFixture fixture)
{
    private static readonly Dictionary<string, string> ProductColumns = new()
    {
        ["sku"] = "Stok Kodu",
        ["name"] = "Ürün Adı",
        ["unit"] = "Birim",
        ["taxRate"] = "KDV Oranı",
        ["barcode"] = "Barkod",
    };

    private static readonly int[] ErrorRows = [5, 6, 7, 8, 9, 10, 11, 12];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preview_reports_actions_duplicates_and_row_errors_then_executes_with_audit()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var brand = await kit.BrandAsync("Mondial Sentetik");
        var category = await kit.CategoryAsync("Debriyaj");
        await kit.ProductAsync("MTR-EXIST", "Eski Ad");
        await kit.ProductAsync("MTR-SAME", "Aynı Ürün");
        var otherBarcode = SyntheticFiles.Ean13("869100000001");
        await kit.ProductAsync("MTR-OTHER", "Başka Ürün", otherBarcode);
        var newBarcode = SyntheticFiles.Ean13("869100000002");

        string[] headers = ["Stok Kodu", "Ürün Adı", "Birim", "KDV Oranı", "Barkod", "Marka", "Kategori"];
        var file = SyntheticFiles.Xlsx(headers,
        [
            ["MTR-NEW-1", "Debriyaj Balatası", "adet", 20, newBarcode, "MONDIAL sentetik", "debriyaj"], // 2 create
            ["MTR-EXIST", "Yeni Ad", "Adet", "20", null, null, null], //                                     3 update
            ["MTR-SAME", "Aynı Ürün", "adet", 20, null, null, null], //                                      4 unchanged
            ["mtr-new-1", "Tekrar", "adet", 20, null, null, null], //                                        5 duplicate in file
            ["MTR-BAD", "Kutu Birimli", "kutu", 20, null, null, null], //                                    6 unknown unit
            ["MTR-NEW-2", "=HYPERLINK(\"http://evil.test\",\"tıkla\")", "adet", 20, null, null, null], //    7 formula
            ["MTR-NEW-3", "Fren Diski", "adet", 150, null, null, null], //                                   8 VAT out of range
            ["MTR-NEW-4", "Zincir Dişli", "adet", 20, otherBarcode, null, null], //                          9 barcode taken
            [null, "SKU'suz Satır", "adet", 20, null, null, null], //                                         10 missing key
            ["MTR-NEW-5", "LED Modül P10", "adet", "18", "8690000000013", null, null], //                    11 bad check digit
            ["MTR-NEW-6", "Gidon", "adet", 20, null, "Bilinmeyen Marka", null], //                            12 unknown brand
        ]);

        var job = await kit.UploadAsync(file, "urunler.xlsx");
        Assert.Equal(("uploaded", 11, "xlsx"), (job.Status, job.TotalRows, job.FileFormat));
        Assert.Equal(headers, job.Headers);
        Assert.Equal(5, job.SampleRows.Count);

        var suggestions = await kit.Owner.OkAsync<MappingSuggestionsResponse>(await kit.Owner.GetAsync($"/api/v1/import-jobs/{job.Id}/mapping-suggestions"));
        var suggested = suggestions.Suggestions.ToDictionary(s => s.Field, s => s.Header);
        Assert.Equal(ProductColumns.Concat(new Dictionary<string, string> { ["brand"] = "Marka", ["category"] = "Kategori" }).OrderBy(p => p.Key), suggested.OrderBy(p => p.Key));
        Assert.Equal("uploaded", (await kit.GetAsync(job.Id)).Status); // suggestions are never applied

        await kit.MapAsync(job.Id, suggested);
        var validated = await kit.ValidateAsync(job.Id);
        Assert.Equal("validated", validated.Status);
        Assert.Equal(new ImportPreviewCounts(1, 1, 1, 8), validated.Preview);
        Assert.Equal(2, validated.Progress.PlannedRows);

        var errors = await kit.RowsAsync(job.Id, "?status=error");
        Assert.Equal(ErrorRows, errors.Items.Select(r => r.RowNumber));
        string Code(int row) => Assert.Single(errors.Items.Single(r => r.RowNumber == row).Errors).Code;
        Assert.Equal(
            ["duplicate.in_file", "unit.not_found", "value.formula_not_allowed", "tax_rate.invalid", "barcode.taken", "match_key.missing", "barcode.invalid", "brand.not_found"],
            ErrorRows.Select(Code));
        Assert.Equal("Stok Kodu", errors.Items[0].Errors[0].Column);

        var first100 = await kit.RowsAsync(job.Id, "?limit=2");
        Assert.True(first100.Page.HasMore);
        var next = await kit.RowsAsync(job.Id, $"?limit=100&cursor={first100.Page.NextCursor}");
        Assert.Equal(9, next.Items.Count);
        Assert.Equal("create", first100.Items[0].Action);
        Assert.Equal(brand.Id.ToString(), first100.Items[0].Values["brandId"]);

        // Error report: CSV, BOM, every value formula-neutralised.
        var report = await kit.Owner.GetAsync($"/api/v1/import-jobs/{job.Id}/error-report");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("text/csv", report.Content.Headers.ContentType?.MediaType);
        var bytes = await report.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var csv = Encoding.UTF8.GetString(bytes[3..]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Satır;Stok Kodu;Ürün Adı;Birim;KDV Oranı;Barkod;Marka;Kategori;Durum;Hatalar", lines[0]);
        Assert.Equal(9, lines.Length);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"tıkla\"\")\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(";=", csv, StringComparison.Ordinal);

        var done = await kit.StartAndRunAsync(job.Id);
        Assert.Equal("completed", done.Status);
        Assert.Equal(new ImportProgress(2, 2, 100, 1, 1, 0, 0), done.Progress);
        Assert.Equal(kit.Owner.UserId, done.StartedBy);

        var created = await kit.Owner.OkAsync<PagedResponse<VariantResponse>>(await kit.Owner.GetAsync("/api/v1/variants?sku=MTR-NEW-1"));
        var variant = Assert.Single(created.Items);
        Assert.Equal(newBarcode, variant.Barcode);
        var product = await kit.Owner.OkAsync<ProductResponse>(await kit.Owner.GetAsync($"/api/v1/products/{variant.ProductId}"));
        Assert.Equal(("Debriyaj Balatası", 20m, brand.Id, category.Id), (product.Name, product.TaxRate, product.BrandId!.Value, product.CategoryId!.Value));
        Assert.Equal(kit.Owner.UserId, product.CreatedBy);

        var updated = await kit.Owner.OkAsync<PagedResponse<VariantResponse>>(await kit.Owner.GetAsync("/api/v1/variants?sku=MTR-EXIST"));
        var existing = await kit.Owner.OkAsync<ProductResponse>(await kit.Owner.GetAsync($"/api/v1/products/{Assert.Single(updated.Items).ProductId}"));
        Assert.Equal("Yeni Ad", existing.Name);
        var updatedRow = (await kit.RowsAsync(job.Id, "?action=update")).Items.Single();
        Assert.Equal(("imported", "updated"), (updatedRow.Status, updatedRow.Outcome));

        Assert.Equal(4L, await kit.VariantCountAsync()); // 3 seeded + MTR-NEW-1, nothing else
        Assert.Equal(1L, await kit.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.completed' AND entity_id = $2 AND actor_user_id = $3",
            kit.TenantId, job.Id, kit.Owner.UserId));
        Assert.Equal(1L, await kit.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.started'", kit.TenantId));
        Assert.Equal(1L, await kit.ScalarAsync<long>(
            "SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'imports.import_completed.v1'", kit.TenantId));
        Assert.Contains("Eski Ad", await kit.ScalarAsync<string>(
            "SELECT previous_values::text FROM imports.import_rows WHERE import_job_id = $1 AND row_number = 3", job.Id), StringComparison.Ordinal);

        // A completed import cannot be re-mapped, started or cancelled again.
        await HttpAssert.ProblemAsync(await kit.Owner.PostAsync($"/api/v1/import-jobs/{job.Id}/start", new { }), HttpStatusCode.Conflict, "imports.invalid_state");
        await HttpAssert.ProblemAsync(await kit.MapResponseAsync(job.Id, ProductColumns), HttpStatusCode.Conflict, "imports.invalid_state");
        await HttpAssert.ProblemAsync(await kit.Owner.PostAsync($"/api/v1/import-jobs/{job.Id}/cancel", new { }), HttpStatusCode.Conflict, "imports.invalid_state");
    }

    [Fact]
    public async Task Importing_the_same_file_twice_never_duplicates_products()
    {
        var kit = await ImportsKit.CreateAsync(fixture, "led_electronics");
        var file = SyntheticFiles.Csv(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(60, "LED"), ';', SyntheticFiles.Windows1254);

        var first = await kit.ImportAsync(file, "led.csv", ProductColumns);
        Assert.Equal(("completed", 60, 0), (first.Status, first.Progress.Created, first.Progress.Failed));
        Assert.Equal(60L, await kit.VariantCountAsync());
        Assert.Equal("windows-1254", first.FileEncoding);
        var decoded = (await kit.RowsAsync(first.Id, "?limit=2")).Items;
        Assert.Contains("Şanzıman", decoded[0].Values["name"], StringComparison.Ordinal);
        Assert.Contains("Mekân", decoded[1].Values["name"], StringComparison.Ordinal);

        var again = await kit.UploadAsync(file, "led-tekrar.csv");
        Assert.Equal(first.Id, again.DuplicateOfImportId); // warning only (PRD IMP-09)
        await kit.MapAsync(again.Id, ProductColumns);
        var preview = await kit.ValidateAsync(again.Id);
        Assert.Equal(new ImportPreviewCounts(0, 0, 60, 0), preview.Preview);
        var second = await kit.StartAndRunAsync(again.Id);
        Assert.Equal(("completed", 0, 0), (second.Status, second.Progress.Created, second.Progress.Updated));
        Assert.Equal(60L, await kit.VariantCountAsync());

        // Same SKUs with changed names: updates, still no new products — also in create_only mode nothing is created.
        var renamed = SyntheticFiles.Csv(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(60, "LED").Select(r => (IReadOnlyList<object?>)[r[0], "Yeni " + r[1], r[2], r[3], r[4], r[5]]));
        var third = await kit.ImportAsync(renamed, "led-yeni.csv", ProductColumns);
        Assert.Equal((60, 0), (third.Progress.Updated, third.Progress.Created));
        Assert.Equal(60L, await kit.VariantCountAsync());

        var createOnly = await kit.UploadAsync(SyntheticFiles.Csv(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(61, "LED")), "led-61.csv");
        await kit.MapAsync(createOnly.Id, ProductColumns, mode: "create_only");
        Assert.Equal(new ImportPreviewCounts(1, 0, 60, 0), (await kit.ValidateAsync(createOnly.Id)).Preview);
        Assert.Equal(1, (await kit.StartAndRunAsync(createOnly.Id)).Progress.Created);
        Assert.Equal(61L, await kit.VariantCountAsync());
    }

    [Fact]
    public async Task Ten_thousand_rows_with_prices_import_correctly_within_two_minutes()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var list = await kit.Owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Perakende" });
        var file = SyntheticFiles.Xlsx(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(10_000));
        var columns = new Dictionary<string, string>(ProductColumns) { ["price"] = "Fiyat" };

        var stopwatch = Stopwatch.StartNew();
        var job = await kit.UploadAsync(file, "katalog-10k.xlsx");
        var uploaded = stopwatch.Elapsed;
        await kit.MapAsync(job.Id, columns, priceListId: list.Id);
        var validated = await kit.ValidateAsync(job.Id);
        var validatedAt = stopwatch.Elapsed;
        Assert.Equal(new ImportPreviewCounts(10_000, 0, 0, 0), validated.Preview);
        var done = await kit.StartAndRunAsync(job.Id);
        stopwatch.Stop();

        TestContext.Current.SendDiagnosticMessage(
            $"10k import: file {file.Length / 1024} KB, upload {uploaded.TotalSeconds:F1}s, validate {(validatedAt - uploaded).TotalSeconds:F1}s, "
            + $"execute {(stopwatch.Elapsed - validatedAt).TotalSeconds:F1}s, total {stopwatch.Elapsed.TotalSeconds:F1}s");

        Assert.Equal(("completed", 10_000, 0, 100), (done.Status, done.Progress.Created, done.Progress.Failed, done.Progress.Percent));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(2), $"10k-row import took {stopwatch.Elapsed}");
        Assert.Equal(10_000L, await kit.VariantCountAsync());
        Assert.Equal(10_000L, await kit.ScalarAsync<long>("SELECT count(DISTINCT sku) FROM catalog.product_variants WHERE tenant_id = $1", kit.TenantId));
        Assert.Equal(10_000L, await kit.ScalarAsync<long>(
            "SELECT count(*) FROM pricing.price_list_items WHERE tenant_id = $1 AND price_list_id = $2", kit.TenantId, list.Id));
        Assert.Equal(100.5m + (1234 % 97), await kit.ScalarAsync<decimal>(
            """
            SELECT i.unit_price FROM pricing.price_list_items i JOIN catalog.product_variants v ON v.id = i.variant_id
            WHERE i.tenant_id = $1 AND v.sku = 'MTR-01234'
            """,
            kit.TenantId));
        Assert.Equal(0L, await kit.ScalarAsync<long>("SELECT count(*) FROM inventory.inventory_movements WHERE tenant_id = $1", kit.TenantId));
    }

    [Fact]
    public async Task Cancel_mid_run_keeps_committed_chunks_and_resume_finishes_without_duplicates()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var job = await kit.UploadAsync(SyntheticFiles.Csv(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(1_200, "CNC")), "parca.csv");
        await kit.MapAsync(job.Id, ProductColumns);
        Assert.Equal(1_200, (await kit.ValidateAsync(job.Id)).Preview.Create);
        await kit.StartAsync(job.Id);

        // Exactly one chunk (500 rows) with a one-job-at-a-time processor.
        var processor = new JobProcessor(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Services.GetRequiredService<JobStore>(),
            fixture.Services.GetRequiredService<JobHandlerRegistry>(),
            new JobsOptions { BatchSize = 1, WorkerId = "imports-cancel-test" },
            NullLogger<JobProcessor>.Instance);
        await WaitForAsync(processor, async () => (await kit.GetAsync(job.Id)).Progress.ProcessedRows > 0);

        var cancelled = await kit.Owner.OkAsync<ImportJobResponse>(await kit.Owner.PostAsync($"/api/v1/import-jobs/{job.Id}/cancel", new { }));
        Assert.Equal(("cancelled", 500, 500), (cancelled.Status, cancelled.Progress.ProcessedRows, cancelled.Progress.Created));
        Assert.Equal(500L, await kit.VariantCountAsync());

        // The already queued next chunk belongs to the cancelled run: it runs as a no-op.
        for (var i = 0; i < 5; i++)
        {
            await processor.RunOnceAsync(Ct);
        }

        Assert.Equal((500, 500L), ((await kit.GetAsync(job.Id)).Progress.ProcessedRows, await kit.VariantCountAsync()));
        Assert.Equal(1L, await kit.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.cancelled'", kit.TenantId));

        // Resume: continues after the checkpoint, never re-creates committed rows.
        var resumed = await kit.StartAndRunAsync(job.Id);
        Assert.Equal(("completed", 1_200, 1_200, 0), (resumed.Status, resumed.Progress.ProcessedRows, resumed.Progress.Created, resumed.Progress.Failed));
        Assert.Equal(1_200L, await kit.VariantCountAsync());
        Assert.Equal(1_200L, await kit.ScalarAsync<long>("SELECT count(*) FROM imports.import_rows WHERE import_job_id = $1 AND status = 'imported'", job.Id));
    }

    [Fact]
    public async Task Price_only_import_updates_prices_and_never_touches_stock()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var brake = (await kit.ProductAsync("FRN-001", "Fren Balatası Ön")).Variants[0].Id;
        var chain = (await kit.ProductAsync("ZNC-428", "Zincir 428H")).Variants[0].Id;
        var warehouse = await kit.Owner.CreatedAsync<WarehouseResponse>("/api/v1/warehouses", new { code = "ANA", name = "Ana Depo" });
        var receipt = await kit.Owner.PostAsync("/api/v1/inventory/receipts", new
        {
            warehouseId = warehouse.Id,
            reference = "İrsaliye SENT-1",
            lines = new[] { new { variantId = brake, quantity = 40m }, new { variantId = chain, quantity = 15m } },
        });
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        var list = await kit.Owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Bayi A" });
        await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/price-lists/{list.Id}/items", new { items = new[] { new { variantId = brake, unitPrice = 100m } } }, idempotencyKey: Guid.NewGuid().ToString());

        const string StockSql = "SELECT count(*)::text || '/' || coalesce(sum(quantity), 0)::text FROM inventory.inventory_movements WHERE tenant_id = $1";
        const string BalanceSql = "SELECT coalesce(string_agg(variant_id::text || '=' || on_hand::text, ',' ORDER BY variant_id), '') FROM inventory.inventory_balances WHERE tenant_id = $1";
        var stockBefore = (await kit.ScalarAsync<string>(StockSql, kit.TenantId), await kit.ScalarAsync<string>(BalanceSql, kit.TenantId));
        var variantsBefore = await kit.VariantCountAsync();

        var file = SyntheticFiles.Csv(
            ["Stok Kodu", "Liste Fiyatı", "Min Miktar", "Stok"],
            [
                ["FRN-001", "120,50", null, 999], //  update (the stock column is ignored: not a price-import field)
                ["ZNC-428", "80", "1", 999], //       create
                ["ZNC-428", "70,25", "10", 999], //   create tier
                ["YOK-404", "5", null, 1], //          unknown product: price import never creates products
                ["FRN-001", "121", "1", 1], //         duplicate (variant + tier)
            ]);
        var job = await kit.UploadAsync(file, "fiyatlar.csv", "prices");
        var suggestions = await kit.Owner.OkAsync<MappingSuggestionsResponse>(await kit.Owner.GetAsync($"/api/v1/import-jobs/{job.Id}/mapping-suggestions"));
        Assert.DoesNotContain(suggestions.Fields, f => f.Key is "stock" or "quantity");
        var columns = suggestions.Suggestions.ToDictionary(s => s.Field, s => s.Header);
        Assert.Equal(new Dictionary<string, string> { ["sku"] = "Stok Kodu", ["price"] = "Liste Fiyatı", ["minQuantity"] = "Min Miktar" }, columns);

        await HttpAssert.ProblemAsync(await kit.MapResponseAsync(job.Id, columns), HttpStatusCode.UnprocessableEntity, "validation.failed"); // list required
        await kit.MapAsync(job.Id, columns, priceListId: list.Id);
        var preview = await kit.ValidateAsync(job.Id);
        Assert.Equal(new ImportPreviewCounts(2, 1, 0, 2), preview.Preview);
        var done = await kit.StartAndRunAsync(job.Id);
        Assert.Equal(("completed", 2, 1, 0), (done.Status, done.Progress.Created, done.Progress.Updated, done.Progress.Failed));

        var items = await kit.Owner.OkAsync<PriceListItemPageResponse>(await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items"));
        Assert.Equal(
            new[] { (brake, 1m, 120.5m), (chain, 1m, 80m), (chain, 10m, 70.25m) }.OrderBy(i => i.Item1).ThenBy(i => i.Item2),
            items.Items.Select(i => (i.VariantId, i.MinQuantity, i.UnitPrice)).OrderBy(i => i.VariantId).ThenBy(i => i.MinQuantity));
        var updatedRow = Assert.Single((await kit.RowsAsync(job.Id, "?action=update")).Items);
        Assert.Equal((2, "updated"), (updatedRow.RowNumber, updatedRow.Outcome));
        Assert.Matches("\"unitPrice\": ?100", await kit.ScalarAsync<string>(
            "SELECT previous_values::text FROM imports.import_rows WHERE import_job_id = $1 AND row_number = 2", job.Id));

        Assert.Equal(stockBefore, (await kit.ScalarAsync<string>(StockSql, kit.TenantId), await kit.ScalarAsync<string>(BalanceSql, kit.TenantId)));
        Assert.Equal(variantsBefore, await kit.VariantCountAsync());
    }

    [Fact]
    public async Task Execution_rechecks_the_starters_permissions_on_every_chunk()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var admin = await kit.Owner.AddMemberAsync(fixture, Roles.Admin);
        var job = await kit.UploadAsync(SyntheticFiles.Csv(SyntheticFiles.ProductHeaders, SyntheticFiles.ProductRows(3, "ADM")), "admin.csv");
        await kit.MapAsync(job.Id, ProductColumns);
        await kit.ValidateAsync(job.Id);
        await kit.StartAsync(job.Id, admin);

        await kit.ScalarAsync<int>(
            "WITH u AS (UPDATE identity.memberships SET role = 'viewer' WHERE tenant_id = $1 AND user_id = $2 RETURNING 1) SELECT count(*)::int FROM u",
            kit.TenantId,
            admin.UserId);
        var failed = await kit.WaitAsync(job.Id, j => j.Status is "completed" or "failed");
        Assert.Equal(("failed", "imports.actor_not_authorized", 0L), (failed.Status, failed.Failure?.Code, (long)failed.Progress.Created));
        Assert.Equal(0L, await kit.VariantCountAsync());
    }

    private static async Task WaitForAsync(JobProcessor processor, Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            if (await processor.RunOnceAsync(Ct) == 0)
            {
                await Task.Delay(20, Ct);
            }
        }

        Assert.Fail("Condition not reached.");
    }
}

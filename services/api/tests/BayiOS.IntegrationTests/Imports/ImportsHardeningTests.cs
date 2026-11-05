using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.TestSupport.BulkImport;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.IntegrationTests.BulkImport;

/// <summary>
/// Import hardening (#94): rollback (PRD IMP-12, closes #25), per-tenant upload rate limit across API instances
/// (threat-model §6.5), raw file retention (data-policy §4), the stalled-import sweep, and custom field
/// (<c>attr.&lt;key&gt;</c>) columns. Real job processor, real PostgreSQL; synthetic motorcycle-parts data only.
/// </summary>
public class ImportsHardeningTests(ApiFixture fixture)
{
    private static readonly Dictionary<string, string> PricedProductColumns = new()
    {
        ["sku"] = "Stok Kodu",
        ["name"] = "Ürün Adı",
        ["unit"] = "Birim",
        ["taxRate"] = "KDV Oranı",
        ["price"] = "Fiyat",
    };

    private static readonly string[] PricedProductHeaders = ["Stok Kodu", "Ürün Adı", "Birim", "KDV Oranı", "Fiyat"];

    private static readonly string[] PlainHeaders = ["Stok Kodu", "Ürün Adı", "Birim", "KDV Oranı"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- rollback

    [Fact]
    public async Task Rollback_archives_created_products_restores_updates_and_never_overwrites_later_edits()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var list = await kit.Owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Perakende" });
        var kept = await kit.ProductAsync("RB-UPD-1", "Eski Ad 1");
        var edited = await kit.ProductAsync("RB-UPD-2", "Eski Ad 2");
        await PutPricesAsync(kit, list.Id, (kept.Variants[0].Id, 1m, 100m));
        await ReceiveStockAsync(kit, kept.Variants[0].Id, 25m);
        var stockBefore = await StockFingerprintAsync(kit);

        var file = SyntheticFiles.Csv(PricedProductHeaders,
        [
            ["RB-NEW-1", "Yeni Fren Balatası", "adet", "20", "50"], // 2 create (+ price item created)
            ["RB-UPD-1", "Yeni Ad 1", "adet", "20", "120"], //         3 update name + price
            ["RB-UPD-2", "Yeni Ad 2", "adet", "20", "80"], //          4 update name, price item created; edited later
        ]);
        var job = await ImportCompletedAsync(kit, file, "geri-al.csv", PricedProductColumns, list.Id);
        Assert.Equal((1, 2), (job.Progress.Created, job.Progress.Updated));
        Assert.Equal(job.FinishedAt!.Value.AddDays(7), job.RollbackDeadline);
        Assert.Null(job.Rollback);

        // A later edit by a user: the rollback must not overwrite it.
        var editedNow = await kit.Owner.OkAsync<ProductResponse>(await kit.Owner.GetAsync($"/api/v1/products/{edited.Id}"));
        Assert.Equal("Yeni Ad 2", editedNow.Name);
        await kit.Owner.OkAsync<ProductResponse>(
            await kit.Owner.PatchAsync($"/api/v1/products/{edited.Id}", new { name = "Elle Düzeltildi" }, CatalogClient.ETag(editedNow.Version)));

        var key = Guid.NewGuid().ToString();
        var accepted = await RollbackAsync(kit.Owner, job.Id, job.Version, key);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var requested = (await accepted.Content.ReadFromJsonAsync<ImportJobResponse>(Ct))!;
        Assert.Equal("rolling_back", requested.Status);
        Assert.Equal((3, kit.Owner.UserId), (requested.Rollback!.Planned, requested.Rollback.RequestedBy!.Value));

        // Idempotent replay (same key) returns the stored response; a new request is refused — one rollback per import.
        var replay = await RollbackAsync(kit.Owner, job.Id, job.Version, key);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        await HttpAssert.ProblemAsync(await RollbackAsync(kit.Owner, job.Id, requested.Version), HttpStatusCode.Conflict, "imports.already_rolled_back");

        var rolled = await kit.WaitAsync(job.Id, j => j.Status != "rolling_back");
        Assert.Equal("partially_rolled_back", rolled.Status);
        Assert.Equal((3, 2, 1, 0, 0), (rolled.Rollback!.Planned, rolled.Rollback.RolledBack, rolled.Rollback.Conflicts, rolled.Rollback.Failed, rolled.Rollback.Skipped));
        Assert.NotNull(rolled.Rollback.FinishedAt);
        Assert.Null(rolled.RollbackDeadline);

        var rows = await kit.RowsAsync(job.Id);
        Assert.Equal(["rolled_back", "rolled_back", "conflict"], rows.Items.Select(r => r.RollbackStatus));
        var conflict = Assert.Single((await kit.RowsAsync(job.Id, "?rollbackStatus=conflict")).Items);
        Assert.Equal(4, conflict.RowNumber);
        Assert.Contains(conflict.Errors, e => e.Code == "rollback.conflict");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync($"/api/v1/import-jobs/{job.Id}/rows?rollbackStatus=x"), HttpStatusCode.UnprocessableEntity, "validation.failed");

        // Created product archived (never deleted), its price item removed; the update restored; the edited row untouched.
        Assert.Equal("archived", await kit.ScalarAsync<string>(
            "SELECT p.status FROM catalog.products p JOIN catalog.product_variants v ON v.product_id = p.id WHERE v.tenant_id = $1 AND v.sku = 'RB-NEW-1'", kit.TenantId));
        Assert.Equal("Eski Ad 1", (await kit.Owner.OkAsync<ProductResponse>(await kit.Owner.GetAsync($"/api/v1/products/{kept.Id}"))).Name);
        Assert.Equal("Elle Düzeltildi", (await kit.Owner.OkAsync<ProductResponse>(await kit.Owner.GetAsync($"/api/v1/products/{edited.Id}"))).Name);
        var items = await kit.Owner.OkAsync<PriceListItemPageResponse>(await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items"));
        Assert.Equal(
            new[] { (kept.Variants[0].Id, 100m), (edited.Variants[0].Id, 80m) }.OrderBy(i => i.Item1),
            items.Items.Select(i => (i.VariantId, i.UnitPrice)).OrderBy(i => i.VariantId));

        // Stock untouched; audit + outbox once each (the replay added nothing).
        Assert.Equal(stockBefore, await StockFingerprintAsync(kit));
        Assert.Equal(1L, await CountAsync(kit, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.rollback_requested'"));
        Assert.Equal(1L, await CountAsync(kit, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.rolled_back'"));
        Assert.Equal(1L, await CountAsync(kit, "SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = $1 AND type = 'imports.import_rolled_back.v1'"));

        await HttpAssert.ProblemAsync(await RollbackAsync(kit.Owner, job.Id, rolled.Version), HttpStatusCode.Conflict, "imports.already_rolled_back");
    }

    [Fact]
    public async Task Price_import_rollback_restores_previous_prices_removes_created_items_and_skips_later_changes()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var a = (await kit.ProductAsync("PR-A", "Zincir Seti")).Variants[0].Id;
        var b = (await kit.ProductAsync("PR-B", "Debriyaj Teli")).Variants[0].Id;
        var list = await kit.Owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name = "Bayi" });
        await PutPricesAsync(kit, list.Id, (a, 1m, 100m));

        var file = SyntheticFiles.Csv(["Stok Kodu", "Liste Fiyatı", "Min Miktar"],
        [
            ["PR-A", "150", null], // update 100 → 150
            ["PR-B", "60", null], //  create; changed later by a user
            ["PR-A", "90", "10"], //  create (tier)
        ]);
        var job = await kit.UploadAsync(file, "fiyat.csv", "prices");
        await kit.MapAsync(job.Id, new Dictionary<string, string> { ["sku"] = "Stok Kodu", ["price"] = "Liste Fiyatı", ["minQuantity"] = "Min Miktar" }, priceListId: list.Id);
        Assert.Equal("validated", (await kit.ValidateAsync(job.Id)).Status);
        var done = await kit.StartAndRunAsync(job.Id);
        Assert.Equal(("completed", 2, 1), (done.Status, done.Progress.Created, done.Progress.Updated));

        await PutPricesAsync(kit, list.Id, (b, 1m, 65m));

        Assert.Equal(HttpStatusCode.Accepted, (await RollbackAsync(kit.Owner, job.Id, done.Version)).StatusCode);
        var rolled = await kit.WaitAsync(job.Id, j => j.Status != "rolling_back");
        Assert.Equal(("partially_rolled_back", 2, 1), (rolled.Status, rolled.Rollback!.RolledBack, rolled.Rollback.Conflicts));

        var items = await kit.Owner.OkAsync<PriceListItemPageResponse>(await kit.Owner.GetAsync($"/api/v1/price-lists/{list.Id}/items"));
        Assert.Equal(
            new[] { (a, 1m, 100m), (b, 1m, 65m) }.OrderBy(i => i.Item1),
            items.Items.Select(i => (i.VariantId, i.MinQuantity, i.UnitPrice)).OrderBy(i => i.VariantId));
    }

    [Fact]
    public async Task Rollback_requires_a_completed_import_in_its_window_the_permission_and_the_current_version()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var uploaded = await kit.UploadAsync(SyntheticFiles.Csv(PlainHeaders, [["PC-0", "Ürün", "adet", "20"]]), "u.csv");
        await HttpAssert.ProblemAsync(await RollbackAsync(kit.Owner, uploaded.Id, uploaded.Version), HttpStatusCode.Conflict, "imports.invalid_state");

        var job = await ImportCompletedAsync(kit, SyntheticFiles.Csv(PlainHeaders, [["PC-1", "Ürün 1", "adet", "20"], ["PC-2", "Ürün 2", "adet", "20"]]), "p.csv", Plain());
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Post, $"/api/v1/import-jobs/{job.Id}/rollback", new { }), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(await RollbackAsync(kit.Owner, job.Id, job.Version + 5), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");

        var manager = await kit.Owner.AddMemberAsync(fixture, Roles.Manager);
        await HttpAssert.ProblemAsync(await RollbackAsync(manager, job.Id, job.Version), HttpStatusCode.Forbidden, "auth.forbidden");

        // Another tenant cannot even see it.
        var other = await ImportsKit.CreateAsync(fixture);
        await HttpAssert.ProblemAsync(await RollbackAsync(other.Owner, job.Id, job.Version), HttpStatusCode.NotFound, "resource.not_found");
        await HttpAssert.ProblemAsync(await RollbackAsync(kit.Owner.Targeting(other.TenantId), job.Id, job.Version), HttpStatusCode.Forbidden, "auth.forbidden");

        // The requester loses the permission mid-way: rollback_failed, then another user resumes it.
        var admin = await kit.Owner.AddMemberAsync(fixture, Roles.Admin);
        Assert.Equal(HttpStatusCode.Accepted, (await RollbackAsync(admin, job.Id, job.Version)).StatusCode);
        await kit.ScalarAsync<int>("WITH u AS (UPDATE identity.memberships SET role = 'viewer' WHERE tenant_id = $1 AND user_id = $2 RETURNING 1) SELECT count(*)::int FROM u", kit.TenantId, admin.UserId);
        var failed = await kit.WaitAsync(job.Id, j => j.Status != "rolling_back");
        Assert.Equal(("rollback_failed", "imports.actor_not_authorized", 0), (failed.Status, failed.Failure!.Code, failed.Rollback!.RolledBack));
        Assert.Equal(HttpStatusCode.Accepted, (await RollbackAsync(kit.Owner, job.Id, failed.Version)).StatusCode);
        var resumed = await kit.WaitAsync(job.Id, j => j.Status != "rolling_back");
        Assert.Equal(("rolled_back", 2), (resumed.Status, resumed.Rollback!.RolledBack));
        Assert.Equal(kit.Owner.UserId, resumed.Rollback.RequestedBy);
        Assert.Equal(1L, await CountAsync(kit, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.rollback_failed'"));

        // Window (PRD IMP-12: 7 days after completion).
        var late = await ImportCompletedAsync(kit, SyntheticFiles.Csv(PlainHeaders, [["PC-3", "Ürün 3", "adet", "20"]]), "late.csv", Plain());
        await kit.ScalarAsync<int>("WITH u AS (UPDATE imports.import_jobs SET finished_at = now() - interval '8 days' WHERE id = $1 RETURNING 1) SELECT count(*)::int FROM u", late.Id);
        await HttpAssert.ProblemAsync(await RollbackAsync(kit.Owner, late.Id, late.Version), HttpStatusCode.Conflict, "imports.rollback_window_expired");
        Assert.Null((await kit.GetAsync(uploaded.Id)).RollbackDeadline);
    }

    // ---------------------------------------------------------------- upload rate limit

    [Fact]
    public async Task Upload_rate_limit_is_per_tenant_and_shared_by_every_api_instance()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        await using var second = fixture.WithWebHostBuilder(_ => { });
        using var hostA = fixture.CreateClientFor(kit.Owner.UserId);
        using var hostB = second.CreateClient();
        hostB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(kit.Owner.UserId));

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await UploadViaAsync(i % 2 == 0 ? hostA : hostB, kit, i)).StatusCode);
        }

        // Two slots left; four concurrent uploads on two instances: exactly two get through.
        var racing = await Task.WhenAll(Enumerable.Range(8, 4).Select(i => UploadViaAsync(i % 2 == 0 ? hostA : hostB, kit, i)));
        Assert.Equal(2, racing.Count(r => r.StatusCode == HttpStatusCode.Created));
        var limited = racing.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(2, limited.Count);
        foreach (var response in limited)
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
            var retryAfter = int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(retryAfter, 1, 3600);
        }

        Assert.Equal(10L, await CountAsync(kit, "SELECT count(*) FROM imports.import_jobs WHERE tenant_id = $1"));

        // Other tenants are unaffected.
        var other = await ImportsKit.CreateAsync(fixture);
        Assert.Equal(HttpStatusCode.Created, (await other.UploadResponseAsync(SyntheticFiles.Csv(PlainHeaders, [["X-1", "Ürün", "adet", "20"]]), "x.csv")).StatusCode);

        // Sliding window: once the oldest upload is older than an hour, a slot frees up.
        await kit.ScalarAsync<int>(
            "WITH u AS (UPDATE imports.import_jobs SET created_at = created_at - interval '2 hours' WHERE id = (SELECT id FROM imports.import_jobs WHERE tenant_id = $1 ORDER BY created_at LIMIT 1) RETURNING 1) SELECT count(*)::int FROM u",
            kit.TenantId);
        Assert.Equal(HttpStatusCode.Created, (await UploadViaAsync(hostB, kit, 99)).StatusCode);
        await HttpAssert.ProblemAsync(await UploadViaAsync(hostA, kit, 100), HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
    }

    // ---------------------------------------------------------------- raw file retention

    [Fact]
    public async Task Raw_files_are_purged_after_retention_keeping_results_and_rollback_never_needs_the_file()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var old = await ImportCompletedAsync(kit, SyntheticFiles.Csv(PlainHeaders, [["RF-1", "Eski Dosya", "adet", "20"]]), "eski.csv", Plain());
        var fresh = await ImportCompletedAsync(kit, SyntheticFiles.Csv(PlainHeaders, [["RF-2", "Yeni Dosya", "adet", "20"]]), "yeni.csv", Plain());
        var abandoned = await kit.UploadAsync(SyntheticFiles.Csv(PlainHeaders, [["RF-3", "Terk", "adet", "20"]]), "terk.csv");
        await kit.MapAsync(abandoned.Id, Plain());
        foreach (var id in new[] { old.Id, abandoned.Id })
        {
            await kit.ScalarAsync<int>(
                """
                WITH j AS (UPDATE imports.import_jobs SET created_at = created_at - interval '31 days', finished_at = finished_at - interval '31 days' WHERE id = $1 RETURNING 1),
                     f AS (UPDATE imports.import_files SET created_at = created_at - interval '31 days' WHERE import_job_id = $1 RETURNING 1)
                SELECT (SELECT count(*) FROM j)::int + (SELECT count(*) FROM f)::int
                """,
                id);
        }

        await RunMaintenanceAsync(kit, old.Id, j => j.FilePurgedAt is not null);
        await kit.WaitAsync(abandoned.Id, j => j.FilePurgedAt is not null);

        Assert.Equal(0L, await CountAsync(kit, $"SELECT count(*) FROM imports.import_files WHERE tenant_id = $1 AND import_job_id IN ('{old.Id}', '{abandoned.Id}')"));
        Assert.Equal(1L, await CountAsync(kit, $"SELECT count(*) FROM imports.import_files WHERE tenant_id = $1 AND import_job_id = '{fresh.Id}'"));
        Assert.Null((await kit.GetAsync(fresh.Id)).FilePurgedAt);
        var purged = await kit.GetAsync(old.Id);
        Assert.Equal(("completed", 1, "eski.csv"), (purged.Status, purged.Progress.Created, purged.FileName)); // metadata and results stay
        Assert.Single((await kit.RowsAsync(old.Id)).Items);
        await HttpAssert.ProblemAsync(await kit.Owner.PostAsync($"/api/v1/import-jobs/{abandoned.Id}/validate", new { }), HttpStatusCode.Conflict, "imports.file_purged");

        // Rollback works from the row results alone.
        await kit.ScalarAsync<int>("WITH d AS (DELETE FROM imports.import_files WHERE import_job_id = $1 RETURNING 1) SELECT count(*)::int FROM d", fresh.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await RollbackAsync(kit.Owner, fresh.Id, fresh.Version)).StatusCode);
        Assert.Equal("rolled_back", (await kit.WaitAsync(fresh.Id, j => j.Status != "rolling_back")).Status);
    }

    // ---------------------------------------------------------------- stalled import sweep

    [Fact]
    public async Task Import_whose_job_died_is_failed_after_the_stall_timeout_and_resumes_from_its_checkpoint()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var job = await kit.UploadAsync(SyntheticFiles.Csv(PlainHeaders, [["ST-1", "Bir", "adet", "20"], ["ST-2", "İki", "adet", "20"], ["ST-3", "Üç", "adet", "20"]]), "st.csv");
        await kit.MapAsync(job.Id, Plain());
        Assert.Equal("validated", (await kit.ValidateAsync(job.Id)).Status);

        // Simulate a run whose execute job exhausted its retries: import 'running', its only job 'dead'. (Set up in SQL
        // so no worker of a parallel test can pick a live job up in between.)
        var runId = Guid.CreateVersion7();
        await kit.ScalarAsync<int>(
            "WITH u AS (UPDATE imports.import_jobs SET status = 'running', run_id = $2, started_at = now(), started_by = $3, updated_at = now() WHERE id = $1 RETURNING 1) SELECT count(*)::int FROM u",
            job.Id, runId, kit.Owner.UserId);
        await kit.ScalarAsync<int>(
            """
            WITH i AS (
                INSERT INTO platform.jobs (id, tenant_id, type, payload, status, priority, run_after, attempts, max_attempts, last_error, created_at, finished_at)
                VALUES (gen_random_uuid(), $1, 'imports.execute', jsonb_build_object('importId', $2::text, 'runId', $3::text), 'dead', 0, now(), 5, 5, 'boom', now(), now())
                RETURNING 1)
            SELECT count(*)::int FROM i
            """,
            kit.TenantId, job.Id, runId);

        // Recent progress: the sweep leaves it alone.
        await RunMaintenanceAsync(kit, job.Id, _ => true);
        await DrainAsync();
        Assert.Equal("running", (await kit.GetAsync(job.Id)).Status);

        // No progress for longer than the stall timeout (30 min): failed, resumable, audited.
        await kit.ScalarAsync<int>("WITH u AS (UPDATE imports.import_jobs SET updated_at = now() - interval '31 minutes' WHERE id = $1 RETURNING 1) SELECT count(*)::int FROM u", job.Id);
        var failed = await RunMaintenanceAsync(kit, job.Id, j => j.Status == "failed");
        Assert.Equal("imports.stalled", failed.Failure!.Code);
        Assert.Equal(1L, await CountAsync(kit, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'import.failed' AND changes::text LIKE '%imports.stalled%'"));

        var resumed = await kit.StartAndRunAsync(job.Id);
        Assert.Equal(("completed", 3), (resumed.Status, resumed.Progress.Created));
    }

    // ---------------------------------------------------------------- custom field columns

    [Fact]
    public async Task Custom_field_columns_are_checked_by_the_catalog_validator_imported_and_rolled_back()
    {
        var kit = await ImportsKit.CreateAsync(fixture); // motorcycle_parts: taraf, model_yili, uyumlu_markalar, parca_tipi (variant)
        var existing = await kit.ProductAsync("AT-UPD", "Mevcut Ürün");
        await kit.Owner.OkAsync<ProductResponse>(
            await kit.Owner.PatchAsync($"/api/v1/products/{existing.Id}", new { attributes = new { taraf = "Ön" } }, CatalogClient.ETag(existing.Version)));

        string[] headers = ["Stok Kodu", "Ürün Adı", "Birim", "KDV Oranı", "Taraf", "Model yılı aralığı", "Uyumlu motosiklet markaları", "Parça tipi"];
        var file = SyntheticFiles.Xlsx(headers,
        [
            ["AT-NEW-1", "Fren Balatası", "adet", 20, "Arka", "2015-2020", "Honda; Yamaha", "Muadil"], // 2 create
            ["AT-NEW-2", "Zincir", "adet", 20, "Üst", null, null, null], //                             3 enum value not allowed
            ["AT-NEW-3", "Dişli", "adet", 20, null, "2020-2015", null, null], //                        4 range min > max
            ["AT-NEW-4", "Gidon", "adet", 20, null, null, "Honda; Ducati", null], //                    5 multi-select value not allowed
            ["AT-UPD", "Mevcut Ürün", "adet", 20, "Sol", null, null, null], //                          6 update attribute only
        ]);
        var job = await kit.UploadAsync(file, "ozellik.xlsx");

        var suggestions = await kit.Owner.OkAsync<MappingSuggestionsResponse>(await kit.Owner.GetAsync($"/api/v1/import-jobs/{job.Id}/mapping-suggestions"));
        Assert.Contains(suggestions.Fields, f => f.Key == "attr.parca_tipi");
        var suggested = suggestions.Suggestions.ToDictionary(s => s.Field, s => s.Header);
        Assert.Equal("Taraf", suggested["attr.taraf"]);
        Assert.Equal("Uyumlu motosiklet markaları", suggested["attr.uyumlu_markalar"]);

        var unknown = new Dictionary<string, string>(Plain()) { ["attr.yok_boyle_alan"] = "Taraf" };
        await HttpAssert.ProblemAsync(await kit.MapResponseAsync(job.Id, unknown), HttpStatusCode.UnprocessableEntity, "validation.failed");

        var columns = new Dictionary<string, string>(Plain())
        {
            ["attr.taraf"] = "Taraf",
            ["attr.model_yili"] = "Model yılı aralığı",
            ["attr.uyumlu_markalar"] = "Uyumlu motosiklet markaları",
            ["attr.parca_tipi"] = "Parça tipi",
        };
        await kit.MapAsync(job.Id, columns);
        var validated = await kit.ValidateAsync(job.Id);
        Assert.Equal(new ImportPreviewCounts(1, 1, 0, 3), validated.Preview);
        var errors = (await kit.RowsAsync(job.Id, "?status=error")).Items;
        Assert.Equal(
            [(3, "attr.taraf"), (4, "attr.model_yili"), (5, "attr.uyumlu_markalar")],
            errors.Select(r => (r.RowNumber, Assert.Single(r.Errors).Field!)));
        Assert.All(errors, r => Assert.Equal("attribute.invalid", r.Errors[0].Code));

        var done = await kit.StartAndRunAsync(job.Id);
        Assert.Equal(("completed", 1, 1), (done.Status, done.Progress.Created, done.Progress.Updated));
        using (var created = JsonDocument.Parse(await kit.ScalarAsync<string>(
                   "SELECT json_build_object('p', p.attributes, 'v', v.attributes)::text FROM catalog.products p JOIN catalog.product_variants v ON v.product_id = p.id WHERE v.tenant_id = $1 AND v.sku = 'AT-NEW-1'",
                   kit.TenantId)))
        {
            var product = created.RootElement.GetProperty("p");
            Assert.Equal("Arka", product.GetProperty("taraf").GetString());
            Assert.Equal((2015m, 2020m), (product.GetProperty("model_yili").GetProperty("min").GetDecimal(), product.GetProperty("model_yili").GetProperty("max").GetDecimal()));
            Assert.Equal(["Honda", "Yamaha"], product.GetProperty("uyumlu_markalar").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("Muadil", created.RootElement.GetProperty("v").GetProperty("parca_tipi").GetString());
        }

        Assert.Equal("Sol", await TarafAsync(kit, existing.Id));

        Assert.Equal(HttpStatusCode.Accepted, (await RollbackAsync(kit.Owner, job.Id, done.Version)).StatusCode);
        Assert.Equal("rolled_back", (await kit.WaitAsync(job.Id, j => j.Status != "rolling_back")).Status);
        Assert.Equal("Ön", await TarafAsync(kit, existing.Id));
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, string> Plain() => new() { ["sku"] = "Stok Kodu", ["name"] = "Ürün Adı", ["unit"] = "Birim", ["taxRate"] = "KDV Oranı" };

    private static Task<HttpResponseMessage> RollbackAsync(CatalogClient caller, Guid id, long version, string? idempotencyKey = null) =>
        caller.SendAsync(HttpMethod.Post, $"/api/v1/import-jobs/{id}/rollback", new { }, CatalogClient.ETag(version), idempotencyKey: idempotencyKey);

    private static async Task<ImportJobResponse> ImportCompletedAsync(
        ImportsKit kit, byte[] file, string fileName, IReadOnlyDictionary<string, string> columns, Guid? priceListId = null)
    {
        var job = await kit.UploadAsync(file, fileName);
        await kit.MapAsync(job.Id, columns, priceListId: priceListId);
        Assert.Equal("validated", (await kit.ValidateAsync(job.Id)).Status);
        var done = await kit.StartAndRunAsync(job.Id);
        Assert.Equal("completed", done.Status);
        return done;
    }

    private static async Task PutPricesAsync(ImportsKit kit, Guid listId, params (Guid VariantId, decimal MinQuantity, decimal UnitPrice)[] items)
    {
        var response = await kit.Owner.SendAsync(
            HttpMethod.Put,
            $"/api/v1/price-lists/{listId}/items",
            new { items = items.Select(i => new { variantId = i.VariantId, minQuantity = i.MinQuantity, unitPrice = i.UnitPrice }) },
            idempotencyKey: Guid.NewGuid().ToString());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task ReceiveStockAsync(ImportsKit kit, Guid variantId, decimal quantity)
    {
        var token = Guid.NewGuid().ToString("N")[..6];
        var warehouse = await kit.Owner.CreatedAsync<JsonElement>("/api/v1/warehouses", new { code = "W" + token, name = "Depo " + token, usesLocations = true });
        var warehouseId = warehouse.GetProperty("id").GetGuid();
        var location = await kit.Owner.CreatedAsync<JsonElement>($"/api/v1/warehouses/{warehouseId}/locations", new { code = "R-" + token, pickSequence = 1 });
        await kit.Owner.CreatedAsync<JsonElement>("/api/v1/inventory/receipts", new
        {
            warehouseId,
            reference = "İrsaliye " + token,
            lines = new[] { new { variantId, quantity, locationId = (Guid?)location.GetProperty("id").GetGuid() } },
        });
    }

    /// <summary>Every ledger and balance row of the tenant, as text: any stock change shows up.</summary>
    private static Task<string> StockFingerprintAsync(ImportsKit kit) => kit.ScalarAsync<string>(
        """
        SELECT coalesce((SELECT string_agg(m::text, '|' ORDER BY m.id) FROM inventory.inventory_movements m WHERE m.tenant_id = $1), '')
            || '#' || coalesce((SELECT string_agg(b::text, '|' ORDER BY b.id) FROM inventory.inventory_balances b WHERE b.tenant_id = $1), '')
        """,
        kit.TenantId);

    private static Task<long> CountAsync(ImportsKit kit, string sql) => kit.ScalarAsync<long>(sql, kit.TenantId);

    private static async Task<string?> TarafAsync(ImportsKit kit, Guid productId) =>
        (await kit.Owner.OkAsync<ProductResponse>(await kit.Owner.GetAsync($"/api/v1/products/{productId}"))).Attributes.GetValueOrDefault("taraf").GetString();

    private async Task<HttpResponseMessage> UploadViaAsync(HttpClient http, ImportsKit kit, int n)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import-jobs");
        request.Headers.Add("X-Tenant-Id", kit.TenantId.ToString());
        request.Headers.Add(IdempotentRequests.Header, Guid.NewGuid().ToString());
        var part = new ByteArrayContent(SyntheticFiles.Csv(PlainHeaders, [[$"RL-{n}", "Ürün", "adet", "20"]]));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        request.Content = new MultipartFormDataContent { { part, "file", $"rl-{n}.csv" }, { new StringContent("products"), "type" } };
        var response = await http.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        _ = fixture;
        return response;
    }

    /// <summary>Enqueues the tenant-less maintenance job (what the scheduler does) and runs jobs until the condition holds.</summary>
    private async Task<ImportJobResponse> RunMaintenanceAsync(ImportsKit kit, Guid importId, Func<ImportJobResponse, bool> condition)
    {
        await fixture.Services.GetRequiredService<JobStore>().InsertAsync(new NewJob(null, "imports.maintenance", "{}", null, 0, 3, null, null), Ct);
        return await kit.WaitAsync(importId, j => condition(j) && MaintenanceDone().GetAwaiter().GetResult());
    }

    private async Task<bool> MaintenanceDone() =>
        await new ImportsKitProbe(fixture).NoQueuedAsync(["imports.maintenance", "imports.sweep_stalled"]);

    private async Task DrainAsync()
    {
        var processor = fixture.Services.GetRequiredService<JobProcessor>();
        while (await processor.RunOnceAsync(Ct) > 0 || !await MaintenanceDone())
        {
            await Task.Delay(20, Ct);
        }
    }

    private sealed class ImportsKitProbe(ApiFixture fixture)
    {
        public async Task<bool> NoQueuedAsync(string[] types)
        {
            await using var connection = new Npgsql.NpgsqlConnection(fixture.SuperuserConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM platform.jobs WHERE type = ANY($1) AND status IN ('queued', 'running'))", connection);
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = types });
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }
    }
}

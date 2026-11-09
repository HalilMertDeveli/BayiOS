using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.TestSupport.BulkImport;
using Npgsql;

namespace BayiOS.IntegrationTests.BulkImport;

/// <summary>
/// Upload hardening over HTTP, permission gates, tenant isolation (API + RLS), mapping validation and mapping
/// templates (#21, #22). No job is executed here.
/// </summary>
[Collection(ImportJobsTestGroup.Name)]
public class ImportsSecurityTests(ApiFixture fixture)
{
    private static readonly string[] Headers = ["Stok Kodu", "Ürün Adı", "Birim", "KDV"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hostile_and_malformed_uploads_are_rejected_with_stable_codes_and_nothing_is_stored()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var valid = SyntheticFiles.Xlsx(Headers, [["A-1", "Ürün", "adet", 20]]);

        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(valid, "urunler.xlsm"), HttpStatusCode.UnsupportedMediaType, "imports.file_macro_not_allowed");
        await HttpAssert.ProblemAsync(
            await kit.UploadResponseAsync(SyntheticFiles.Xlsx(Headers, [["A"]], new Dictionary<string, byte[]> { ["xl/vbaProject.bin"] = [1] }), "gizli.xlsx"),
            HttpStatusCode.UnsupportedMediaType,
            "imports.file_macro_not_allowed");
        await HttpAssert.ProblemAsync(
            await kit.UploadResponseAsync(Encoding.UTF8.GetBytes("Stok Kodu;Ad\r\nA;B"), "sahte.xlsx"), HttpStatusCode.UnsupportedMediaType, "imports.file_content_mismatch");
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(valid, "sahte.csv"), HttpStatusCode.UnsupportedMediaType, "imports.file_content_mismatch");
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(valid, "program.exe", contentType: "application/octet-stream"), HttpStatusCode.UnsupportedMediaType, "imports.file_type_not_allowed");

        var bomb = SyntheticFiles.Xlsx(Headers, [["A"]], new Dictionary<string, byte[]> { ["xl/media/dolgu.xml"] = new byte[60 * 1024 * 1024] }, level: CompressionLevel.SmallestSize);
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(bomb, "bomba.xlsx"), HttpStatusCode.UnprocessableEntity, "imports.file_archive_limits");

        // OD-04: the limit is 20 MB (A_file_between_10_and_20_MB_is_accepted_and_stored_whole).
        var oversized = new byte[(20 * 1024 * 1024) + 1];
        Encoding.ASCII.GetBytes("SKU\r\n").CopyTo(oversized, 0);
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(oversized, "buyuk.csv"), HttpStatusCode.RequestEntityTooLarge, "imports.file_too_large");

        var rows = SyntheticFiles.Csv(["SKU"], Enumerable.Range(0, 10_001).Select(i => (IReadOnlyList<object?>)[$"S{i}"]));
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(rows, "satir.csv"), HttpStatusCode.UnprocessableEntity, "imports.file_too_many_rows");

        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(valid, "urunler.xlsx", type: "stock"), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(valid, "urunler.xlsx", idempotencyKey: null), HttpStatusCode.PreconditionRequired, "precondition.required");
        // Not multipart: refused by the route's content-type constraint before the handler runs.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await kit.Owner.PostAsync("/api/v1/import-jobs", new { type = "products" })).StatusCode);

        Assert.Equal(0L, await kit.ScalarAsync<long>("SELECT count(*) FROM imports.import_jobs WHERE tenant_id = $1", kit.TenantId));
        Assert.Equal(0L, await kit.ScalarAsync<long>("SELECT count(*) FROM imports.import_files WHERE tenant_id = $1", kit.TenantId));

        // A valid upload stores the bytes once; the same Idempotency-Key replays the first response.
        var key = Guid.NewGuid().ToString();
        var first = await kit.UploadResponseAsync(valid, "urunler.xlsx", idempotencyKey: key);
        var replay = await kit.UploadResponseAsync(valid, "urunler.xlsx", idempotencyKey: key);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, replay.StatusCode));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((await first.Content.ReadFromJsonAsync<ImportJobResponse>(Ct))!.Id, (await replay.Content.ReadFromJsonAsync<ImportJobResponse>(Ct))!.Id);
        Assert.Equal((long)valid.Length, await kit.ScalarAsync<long>("SELECT sum(length(content))::bigint FROM imports.import_files WHERE tenant_id = $1", kit.TenantId));
    }

    [Fact]
    public async Task A_file_between_10_and_20_MB_is_accepted_and_stored_whole()
    {
        // OD-04 (2026-11-09): 20 MB. ~18 MB of CSV in 9,000 rows (each name cell 2,000 characters, under the cell limit).
        var kit = await ImportsKit.CreateAsync(fixture);
        var name = new string('x', 2_000);
        var file = SyntheticFiles.Csv(Headers, Enumerable.Range(0, 9_000).Select(i => (IReadOnlyList<object?>)[$"S{i}", name, "adet", "20"]));
        Assert.InRange(file.LongLength, (10L * 1024 * 1024) + 1, 20L * 1024 * 1024);

        var response = await kit.UploadResponseAsync(file, "buyuk.csv");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(file.LongLength, await kit.ScalarAsync<long>("SELECT sum(length(content))::bigint FROM imports.import_files WHERE tenant_id = $1", kit.TenantId));
    }

    [Fact]
    public async Task Upload_idempotency_key_replays_the_same_file_and_rejects_a_different_one()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var file = SyntheticFiles.Csv(Headers, [["A-1", "Debriyaj Balatası", "adet", "20"]]);
        var other = SyntheticFiles.Csv(Headers, [["A-2", "Fren Diski", "adet", "20"]]);
        var key = Guid.NewGuid().ToString();

        var first = await kit.UploadResponseAsync(file, "urunler.csv", idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = (await first.Content.ReadFromJsonAsync<ImportJobResponse>(Ct))!.Id;

        // Same key + same file (bytes, name, type, form fields) → the stored response is replayed.
        var replay = await kit.UploadResponseAsync(file, "urunler.csv", idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<ImportJobResponse>(Ct))!.Id);

        // Same key + anything different about the upload → 422, never a silent replay of the first import.
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(other, "urunler.csv", idempotencyKey: key), HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        await HttpAssert.ProblemAsync(await kit.UploadResponseAsync(file, "baska-ad.csv", idempotencyKey: key), HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        await HttpAssert.ProblemAsync(
            await kit.UploadResponseAsync(file, "urunler.csv", contentType: "text/plain", idempotencyKey: key), HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");
        await HttpAssert.ProblemAsync(
            await kit.UploadResponseAsync(file, "urunler.csv", type: "prices", idempotencyKey: key), HttpStatusCode.UnprocessableEntity, "idempotency.key_reused");

        Assert.Equal(1L, await kit.ScalarAsync<long>("SELECT count(*) FROM imports.import_jobs WHERE tenant_id = $1", kit.TenantId));

        // A fresh key with the other file is a new import.
        Assert.Equal(HttpStatusCode.Created, (await kit.UploadResponseAsync(other, "urunler.csv")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.Viewer)]
    public async Task Only_owner_and_admin_can_use_imports(string role)
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var job = await kit.UploadAsync(SyntheticFiles.Csv(Headers, [["A-1", "Ürün", "adet", "20"]]), "u.csv");
        var member = await kit.Owner.AddMemberAsync(fixture, role);

        await HttpAssert.ProblemAsync(
            await kit.UploadResponseAsync(SyntheticFiles.Csv(Headers, [["A-2", "Ürün", "adet", "20"]]), "u.csv", caller: member), HttpStatusCode.Forbidden, "auth.forbidden");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/import-jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v1/import-jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/v1/import-jobs/{job.Id}/start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v1/import-jobs/{job.Id}/error-report")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/import-mapping-templates")).StatusCode);

        var admin = await kit.Owner.AddMemberAsync(fixture, Roles.Admin);
        Assert.Equal(HttpStatusCode.Created, (await kit.UploadResponseAsync(SyntheticFiles.Csv(Headers, [["A-3", "Ürün", "adet", "20"]]), "u.csv", caller: admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await kit.MapResponseAsync(job.Id, Map(), caller: admin)).StatusCode);
    }

    [Fact]
    public async Task Imports_rows_and_templates_are_isolated_per_tenant()
    {
        var a = await ImportsKit.CreateAsync(fixture);
        var b = await ImportsKit.CreateAsync(fixture);
        var job = await a.UploadAsync(SyntheticFiles.Csv(Headers, [["GIZLI-1", "A'nın ürünü", "adet", "20"]]), "a.csv");
        var template = await a.Owner.CreatedAsync<MappingTemplateResponse>(
            "/api/v1/import-mapping-templates", new { name = "A şablonu", type = "products", columns = Map() });

        foreach (var path in new[]
        {
            $"/api/v1/import-jobs/{job.Id}",
            $"/api/v1/import-jobs/{job.Id}/rows",
            $"/api/v1/import-jobs/{job.Id}/mapping-suggestions",
            $"/api/v1/import-jobs/{job.Id}/error-report",
            $"/api/v1/import-mapping-templates/{template.Id}",
        })
        {
            await HttpAssert.ProblemAsync(await b.Owner.GetAsync(path), HttpStatusCode.NotFound, "resource.not_found");
        }

        await HttpAssert.ProblemAsync(await b.MapResponseAsync(job.Id, Map()), HttpStatusCode.NotFound, "resource.not_found");
        foreach (var action in new[] { "validate", "start", "cancel" })
        {
            await HttpAssert.ProblemAsync(await b.Owner.PostAsync($"/api/v1/import-jobs/{job.Id}/{action}", new { }), HttpStatusCode.NotFound, "resource.not_found");
        }

        await HttpAssert.ProblemAsync(await b.Owner.DeleteAsync($"/api/v1/import-mapping-templates/{template.Id}", null), HttpStatusCode.NotFound, "resource.not_found");
        Assert.Empty((await b.Owner.OkAsync<ImportJobPageResponse>(await b.Owner.GetAsync("/api/v1/import-jobs"))).Items);
        Assert.Empty((await b.Owner.OkAsync<MappingTemplateListResponse>(await b.Owner.GetAsync("/api/v1/import-mapping-templates"))).Items);

        // The X-Tenant-Id header is a selection, not a grant.
        await HttpAssert.ProblemAsync(await a.Owner.Targeting(b.TenantId).GetAsync("/api/v1/import-jobs"), HttpStatusCode.Forbidden, "tenant.access_denied");

        // RLS as the application role: tenant B's context sees none of A's rows, and cannot write into A.
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter { Value = b.TenantId.ToString() });
            await context.ExecuteNonQueryAsync(Ct);
        }

        foreach (var table in new[] { "import_jobs", "import_files", "mapping_templates" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM imports.{table} WHERE tenant_id = $1", connection, transaction);
            count.Parameters.Add(new NpgsqlParameter { Value = a.TenantId });
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
        }

        await using var forge = new NpgsqlCommand(
            "UPDATE imports.import_jobs SET status = 'cancelled' WHERE id = $1", connection, transaction);
        forge.Parameters.Add(new NpgsqlParameter { Value = job.Id });
        Assert.Equal(0, await forge.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Mapping_is_validated_saved_and_reusable_as_a_template()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var headers = new[] { "Malzeme Kodu", "Malzeme Adı", "Ölçü Birimi", "KDV %", "Liste Fiyatı" };
        var file = SyntheticFiles.Csv(headers, [["A-1", "Ürün", "adet", "20", "10,5"]]);
        var job = await kit.UploadAsync(file, "tedarikci-a.csv");

        async Task Invalid(object columns, string field, Guid? priceListId = null, string? mode = null)
        {
            var response = await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/import-jobs/{job.Id}/mapping", new { columns, priceListId, mode });
            await HttpAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation.failed");
            Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        await Invalid(new { sku = "Malzeme Kodu" }, "columns.name"); // create needs name, unit, taxRate
        await Invalid(new { sku = "Olmayan Kolon", name = "Malzeme Adı", unit = "Ölçü Birimi", taxRate = "KDV %" }, "columns.sku");
        await Invalid(new { sku = "Malzeme Kodu", name = "Malzeme Kodu", unit = "Ölçü Birimi", taxRate = "KDV %" }, "columns.name");
        await Invalid(new { sku = "Malzeme Kodu", stok = "Malzeme Adı" }, "columns.stok", mode: "update_only");
        await Invalid(new { sku = "Malzeme Kodu", price = "Liste Fiyatı" }, "priceListId", mode: "update_only");
        await Invalid(new { sku = "Malzeme Kodu" }, "priceListId", priceListId: Guid.NewGuid(), mode: "update_only");
        await Invalid(new { sku = "Malzeme Kodu", price = "Liste Fiyatı" }, "priceListId", priceListId: Guid.NewGuid(), mode: "update_only");

        var columns = new Dictionary<string, string> { ["sku"] = "Malzeme Kodu", ["name"] = "Malzeme Adı", ["unit"] = "Ölçü Birimi", ["taxRate"] = "KDV %" };
        var mapped = await kit.MapAsync(job.Id, columns, saveAsTemplate: "Tedarikçi A");
        Assert.Equal(("mapped", "sku", "upsert"), (mapped.Status, mapped.MatchKey, mapped.Mode));
        Assert.Equal(columns.OrderBy(c => c.Key), mapped.Mapping!.OrderBy(c => c.Key));

        // Next upload with the same headers: the template is offered; suggestions still come from synonyms.
        var next = await kit.UploadAsync(file, "tedarikci-a-2.csv");
        var suggestions = await kit.Owner.OkAsync<MappingSuggestionsResponse>(await kit.Owner.GetAsync($"/api/v1/import-jobs/{next.Id}/mapping-suggestions"));
        var template = Assert.Single(suggestions.Templates);
        Assert.Equal(("Tedarikçi A", "products"), (template.Name, template.Type));
        Assert.Equal(columns.OrderBy(c => c.Key), template.Columns.OrderBy(c => c.Key));
        Assert.Equal("Malzeme Kodu", suggestions.Suggestions.Single(s => s.Field == "sku").Header);
        Assert.Equal("Liste Fiyatı", suggestions.Suggestions.Single(s => s.Field == "price").Header);
        Assert.Contains(suggestions.Fields, f => f is { Key: "sku", RequiredForCreate: true });

        // Saving again under the same name replaces the template (no duplicate).
        await kit.MapAsync(next.Id, new Dictionary<string, string>(columns) { ["description"] = "Liste Fiyatı" }, saveAsTemplate: "Tedarikçi A");
        var templates = await kit.Owner.OkAsync<MappingTemplateListResponse>(await kit.Owner.GetAsync("/api/v1/import-mapping-templates?type=products"));
        Assert.Equal("Liste Fiyatı", Assert.Single(templates.Items).Columns["description"]);
    }

    [Fact]
    public async Task Mapping_templates_crud_with_etags_and_unique_names()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var created = await kit.Owner.CreatedAsync<MappingTemplateResponse>(
            "/api/v1/import-mapping-templates", new { name = "Fiyat dosyası", type = "prices", columns = new { sku = "Kod", price = "Fiyat" } });
        Assert.Equal(("prices", "sku", "upsert", 1L), (created.Type, created.MatchKey, created.Mode, created.Version));

        await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync("/api/v1/import-mapping-templates", new { name = "Fiyat dosyası", type = "prices", columns = new { sku = "Kod", price = "Fiyat" } }),
            HttpStatusCode.Conflict,
            "imports.template_name_taken");
        await HttpAssert.ProblemAsync(
            await kit.Owner.PostAsync("/api/v1/import-mapping-templates", new { name = "Eksik", type = "prices", columns = new { sku = "Kod" } }),
            HttpStatusCode.UnprocessableEntity,
            "validation.failed");

        var get = await kit.Owner.GetAsync($"/api/v1/import-mapping-templates/{created.Id}");
        Assert.Equal("W/\"1\"", get.Headers.ETag?.ToString());
        var body = new { name = "Fiyat dosyası (barkod)", type = "prices", columns = new { barcode = "EAN", price = "Fiyat" }, matchKey = "barcode" };
        await HttpAssert.ProblemAsync(await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/import-mapping-templates/{created.Id}", body), HttpStatusCode.PreconditionRequired, "precondition.required");
        await HttpAssert.ProblemAsync(
            await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/import-mapping-templates/{created.Id}", body, "W/\"9\""), HttpStatusCode.PreconditionFailed, "concurrency.version_mismatch");
        var updated = await kit.Owner.OkAsync<MappingTemplateResponse>(
            await kit.Owner.SendAsync(HttpMethod.Put, $"/api/v1/import-mapping-templates/{created.Id}", body, "W/\"1\""));
        Assert.Equal(("barcode", 2L, "EAN"), (updated.MatchKey, updated.Version, updated.Columns["barcode"]));

        Assert.Equal(HttpStatusCode.NoContent, (await kit.Owner.DeleteAsync($"/api/v1/import-mapping-templates/{created.Id}", null)).StatusCode);
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync($"/api/v1/import-mapping-templates/{created.Id}"), HttpStatusCode.NotFound, "resource.not_found");
    }

    [Fact]
    public async Task Import_history_is_paged_newest_first()
    {
        var kit = await ImportsKit.CreateAsync(fixture);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await kit.UploadAsync(SyntheticFiles.Csv(Headers, [[$"A-{i}", "Ürün", "adet", "20"]]), $"u{i}.csv")).Id);
        }

        var page1 = await kit.Owner.OkAsync<ImportJobPageResponse>(await kit.Owner.GetAsync("/api/v1/import-jobs?limit=2"));
        var page2 = await kit.Owner.OkAsync<ImportJobPageResponse>(await kit.Owner.GetAsync($"/api/v1/import-jobs?limit=2&cursor={page1.Page.NextCursor}"));
        Assert.Equal(Enumerable.Reverse(ids), page1.Items.Concat(page2.Items).Select(j => j.Id));
        Assert.False(page2.Page.HasMore);
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/import-jobs?limit=0"), HttpStatusCode.BadRequest, "request.malformed");
        await HttpAssert.ProblemAsync(await kit.Owner.GetAsync("/api/v1/import-jobs?foo=1"), HttpStatusCode.BadRequest, "request.malformed");
    }

    private static Dictionary<string, string> Map() => new() { ["sku"] = "Stok Kodu", ["name"] = "Ürün Adı", ["unit"] = "Birim", ["taxRate"] = "KDV" };
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Imports.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BayiOS.IntegrationTests.BulkImport;

/// <summary>
/// A fresh tenant with an owner and an "adet" unit, plus helpers for the import API and for driving the background
/// jobs (the shared test host runs with <c>Jobs:Enabled=false</c>). Synthetic motorcycle-parts / LED data only.
/// </summary>
internal sealed class ImportsKit
{
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ApiFixture _fixture;

    private ImportsKit(ApiFixture fixture, CatalogClient owner, Guid unitId)
    {
        _fixture = fixture;
        Owner = owner;
        UnitId = unitId;
    }

    public CatalogClient Owner { get; }

    public Guid UnitId { get; }

    public Guid TenantId => Owner.TenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ImportsKit> CreateAsync(ApiFixture fixture, string sector = "motorcycle_parts")
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, sector);
        var unit = await owner.CreateUnitAsync("adet", "Adet");
        return new ImportsKit(fixture, owner, unit.Id);
    }

    public async Task<HttpResponseMessage> UploadResponseAsync(
        byte[] file, string fileName, string type = "products", string? contentType = null, CatalogClient? caller = null, string? idempotencyKey = "auto")
    {
        caller ??= Owner;
        using var http = _fixture.CreateClientFor(caller.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import-jobs");
        request.Headers.Add("X-Tenant-Id", caller.TenantId.ToString());
        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotentRequests.Header, idempotencyKey == "auto" ? Guid.NewGuid().ToString() : idempotencyKey);
        }

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : XlsxType));
        form.Add(part, "file", fileName);
        form.Add(new StringContent(type), "type");
        request.Content = form;
        return await http.SendAsync(request, Ct);
    }

    public async Task<ImportJobResponse> UploadAsync(byte[] file, string fileName, string type = "products")
    {
        var response = await UploadResponseAsync(file, fileName, type);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<ImportJobResponse>(Ct))!;
    }

    public Task<HttpResponseMessage> MapResponseAsync(
        Guid id, IReadOnlyDictionary<string, string> columns, string? matchKey = null, string? mode = null, Guid? priceListId = null, string? saveAsTemplate = null, CatalogClient? caller = null) =>
        (caller ?? Owner).SendAsync(HttpMethod.Put, $"/api/v1/import-jobs/{id}/mapping", new { columns, matchKey, mode, priceListId, saveAsTemplate });

    public async Task<ImportJobResponse> MapAsync(
        Guid id, IReadOnlyDictionary<string, string> columns, string? matchKey = null, string? mode = null, Guid? priceListId = null, string? saveAsTemplate = null) =>
        await Owner.OkAsync<ImportJobResponse>(await MapResponseAsync(id, columns, matchKey, mode, priceListId, saveAsTemplate));

    public async Task<ImportJobResponse> GetAsync(Guid id) => await Owner.OkAsync<ImportJobResponse>(await Owner.GetAsync($"/api/v1/import-jobs/{id}"));

    /// <summary>Queues the dry run and runs jobs until the import is validated (or failed).</summary>
    public async Task<ImportJobResponse> ValidateAsync(Guid id)
    {
        await AcceptedAsync(await Owner.PostAsync($"/api/v1/import-jobs/{id}/validate", new { }));
        return await WaitAsync(id, j => j.Status is "validated" or "failed");
    }

    /// <summary>Starts (or resumes) and runs jobs until the import completes (or fails).</summary>
    public async Task<ImportJobResponse> StartAndRunAsync(Guid id, CatalogClient? caller = null)
    {
        await StartAsync(id, caller);
        return await WaitAsync(id, j => j.Status is "completed" or "failed");
    }

    public async Task StartAsync(Guid id, CatalogClient? caller = null) =>
        await AcceptedAsync(await (caller ?? Owner).PostAsync($"/api/v1/import-jobs/{id}/start", new { }));

    public async Task<ImportJobResponse> ImportAsync(byte[] file, string fileName, IReadOnlyDictionary<string, string> columns, Guid? priceListId = null, string type = "products")
    {
        var job = await UploadAsync(file, fileName, type);
        await MapAsync(job.Id, columns, priceListId: priceListId);
        var validated = await ValidateAsync(job.Id);
        Assert.Equal("validated", validated.Status);
        return await StartAndRunAsync(job.Id);
    }

    /// <summary>Runs the real job processor (all registered types) until the condition holds.</summary>
    public async Task<ImportJobResponse> WaitAsync(Guid id, Func<ImportJobResponse, bool> condition, TimeSpan? timeout = null)
    {
        var processor = _fixture.Services.GetRequiredService<JobProcessor>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        while (true)
        {
            var job = await GetAsync(id);
            if (condition(job))
            {
                return job;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Import {id} stuck in '{job.Status}' ({job.Progress.ProcessedRows}/{job.Progress.PlannedRows}).");
            if (await processor.RunOnceAsync(Ct) == 0)
            {
                await Task.Delay(20, Ct);
            }
        }
    }

    public async Task<ImportRowPageResponse> RowsAsync(Guid id, string query = "") =>
        await Owner.OkAsync<ImportRowPageResponse>(await Owner.GetAsync($"/api/v1/import-jobs/{id}/rows{query}"));

    public Task<BrandResponse> BrandAsync(string name) => Owner.CreatedAsync<BrandResponse>("/api/v1/brands", new { name });

    public Task<CategoryResponse> CategoryAsync(string name) => Owner.CreatedAsync<CategoryResponse>("/api/v1/categories", new { name });

    public Task<ProductResponse> ProductAsync(string sku, string name, string? barcode = null) => Owner.CreateSimpleProductAsync(UnitId, name, sku, barcode);

    public async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public Task<long> VariantCountAsync() => ScalarAsync<long>("SELECT count(*) FROM catalog.product_variants WHERE tenant_id = $1", TenantId);

    private static async Task AcceptedAsync(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
}

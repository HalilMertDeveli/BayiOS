using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Catalog.Contracts;
using BayiOS.Modules.Identity.Contracts;
using Npgsql;

namespace BayiOS.IntegrationTests.Catalog;

/// <summary>A user acting inside one tenant, with small helpers for catalog calls. Synthetic data only.</summary>
internal sealed class CatalogClient(HttpClient http, Guid tenantId, Guid userId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid TenantId { get; } = tenantId;

    public Guid UserId { get; } = userId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>New user who creates a new tenant and is its owner.</summary>
    public static async Task<CatalogClient> NewTenantOwnerAsync(ApiFixture fixture, string sector = "general")
    {
        var userId = Guid.NewGuid();
        var http = fixture.CreateClientFor(userId);
        var response = await http.PostAsJsonAsync(
            "/api/v1/tenants",
            new { name = "Katalog " + Guid.NewGuid().ToString("N")[..6], slug = "c-" + Guid.NewGuid().ToString("N")[..12], sector },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = (await response.Content.ReadFromJsonAsync<TenantResponse>(Ct))!;
        return new CatalogClient(http, tenant.Id, userId);
    }

    /// <summary>Another user with the given role in the same tenant (seeded directly; no invitation flow yet).</summary>
    public async Task<CatalogClient> AddMemberAsync(ApiFixture fixture, string role)
    {
        var userId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO identity.memberships (id, tenant_id, user_id, role, status, created_at) VALUES ($1, $2, $3, $4, 'active', now())",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = TenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        await command.ExecuteNonQueryAsync(Ct);
        return new CatalogClient(fixture.CreateClientFor(userId), TenantId, userId);
    }

    /// <summary>Same user/HTTP client, but sending another tenant id (to probe cross-tenant access).</summary>
    public CatalogClient Targeting(Guid otherTenantId) => new(http, otherTenantId, UserId);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? ifMatch = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Tenant-Id", TenantId.ToString());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            var json = body as string ?? JsonSerializer.Serialize(body, Json);
            request.Content = new StringContent(json, Encoding.UTF8, contentType);
        }

        return await http.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<HttpResponseMessage> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);

    public Task<HttpResponseMessage> PatchAsync(string path, object body, string? ifMatch) =>
        SendAsync(HttpMethod.Patch, path, body, ifMatch, "application/merge-patch+json");

    public Task<HttpResponseMessage> DeleteAsync(string path, string? ifMatch) => SendAsync(HttpMethod.Delete, path, ifMatch: ifMatch);

    public async Task<T> CreatedAsync<T>(string path, object body)
    {
        var response = await PostAsync(path, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Reads as owner.OkAsync at call sites.")]
    public async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    public Task<UnitResponse> CreateUnitAsync(string code = "adet", string name = "Adet") =>
        CreatedAsync<UnitResponse>("/api/v1/units", new { code, name, allowsFraction = false });

    public Task<ProductResponse> CreateSimpleProductAsync(Guid unitId, string name, string sku, string? barcode = null, Guid? categoryId = null, Guid? brandId = null) =>
        CreatedAsync<ProductResponse>("/api/v1/products", new { name, defaultUnitId = unitId, taxRate = 20m, sku, barcode, categoryId, brandId });

    public static string ETag(long version) => $"W/\"{version}\"";
}

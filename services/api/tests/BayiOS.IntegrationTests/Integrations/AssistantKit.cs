using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Integrations.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace BayiOS.IntegrationTests.Integrations;

/// <summary>
/// A tenant with a small synthetic motorcycle-parts catalog (two look-alike clutch pads, H4/H7 bulbs), a default price
/// list with prices, a warehouse and a customer — plus helpers for the AI assistant endpoints.
/// </summary>
internal sealed class AssistantKit
{
    private AssistantKit(OrdersKit orders, Dictionary<string, Guid> variants)
    {
        Orders = orders;
        Variants = variants;
    }

    public OrdersKit Orders { get; }

    public CatalogClient Owner => Orders.Owner;

    /// <summary>SKU → variant id.</summary>
    public IReadOnlyDictionary<string, Guid> Variants { get; }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly (string Sku, string Name, decimal Price)[] Products =
    [
        ("MTR-1001", "Debriyaj Balatası CG 125", 150m),
        ("MTR-1002", "Debriyaj Balatası KR 150", 170m),
        ("MTR-1040", "Far Ampulü H4 60/55W 12V", 45m),
        ("MTR-1041", "Far Ampulü H7 55W 12V", 55m),
        ("MTR-1071", "Gaz Teli CG 125", 30m),
    ];

    public static async Task<AssistantKit> CreateAsync(ApiFixture fixture)
    {
        var orders = await OrdersKit.CreateAsync(fixture);
        var unit = await orders.Owner.CreateUnitAsync("takim", "Takım");
        var variants = new Dictionary<string, Guid>();
        foreach (var (sku, name, price) in Products)
        {
            var product = await orders.Owner.CreateSimpleProductAsync(unit.Id, name, sku);
            var variant = Assert.Single(product.Variants).Id;
            await orders.PriceAsync(orders.PriceListId, variant, price);
            variants[sku] = variant;
        }

        return new AssistantKit(orders, variants);
    }

    public static Task<HttpResponseMessage> SubmitAsync(CatalogClient client, string text, Guid? customerId = null, string? key = null) =>
        client.PostAsync("/api/v1/ai/order-parses", new { text, customerId }, key);

    public static async Task<OrderParseResponse> SubmitOkAsync(CatalogClient client, string text, Guid? customerId = null)
    {
        var response = await SubmitAsync(client, text, customerId);
        return await ReadAsync<OrderParseResponse>(response, HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> ApproveAsync(CatalogClient client, Guid id, object body, string? key = null) =>
        client.PostAsync($"/api/v1/ai/order-parses/{id}/approve", body, key);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<T>(text, Web)!;
    }

    public static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>A client of another host (custom settings) acting as the same user in the same tenant.</summary>
    public static CatalogClient On(WebApplicationFactory<Program> factory, CatalogClient member)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(member.UserId));
        return new CatalogClient(http, member.TenantId, member.UserId);
    }

    public static async Task<T> ScalarAsync<T>(ApiFixture fixture, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public static async Task<int> ExecuteAsync(ApiFixture fixture, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteNonQueryAsync(Ct);
    }
}

/// <summary>
/// Offline Claude Messages API for the host (named HttpClient "anthropic"): canned responses in the wire shape of
/// <c>POST /v1/messages</c>, request bodies recorded. Never reaches the network; the key is a placeholder.
/// </summary>
internal sealed class FakeClaude : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<string> Requests { get; } = [];

    public FakeClaude Reply(object output, int input = 220, int outputTokens = 400, int cacheRead = 1650)
    {
        var body = new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-opus-5-5",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "thinking", ["thinking"] = string.Empty, ["signature"] = "c2ln" },
                new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(output) },
            },
            ["stop_reason"] = "end_turn",
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["output_tokens"] = outputTokens,
                ["cache_creation_input_tokens"] = 0,
                ["cache_read_input_tokens"] = cacheRead,
            },
        };
        _responses.Enqueue((HttpStatusCode.OK, body.ToJsonString()));
        return this;
    }

    public FakeClaude Fail(HttpStatusCode status = HttpStatusCode.InternalServerError)
    {
        _responses.Enqueue((status, """{"type":"error","error":{"type":"api_error","message":"synthetic failure"}}"""));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.Content is null ? string.Empty : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
        }

        await Task.Yield();
        var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public static object Output(params object[] lines) => new { lines, unparsedFragments = Array.Empty<string>(), warnings = Array.Empty<string>() };

    public static object Line(string rawText, string productText, decimal? quantity, string? unit = null) =>
        new { rawText, productCode = (string?)null, productText, quantity, unit, brandModel = (string?)null, confidence = 0.9m, notes = (string?)null };
}

internal static class JsonExtensions
{
    public static Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}

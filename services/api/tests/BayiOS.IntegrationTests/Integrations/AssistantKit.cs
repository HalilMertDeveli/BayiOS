using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Api.Platform.Jobs;
using BayiOS.IntegrationTests.Billing;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.IntegrationTests.Orders;
using BayiOS.Modules.Integrations;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <param name="optIn">Switch the tenant's AI opt-in on (data-policy §7; off by default).</param>
    public static async Task<AssistantKit> CreateAsync(ApiFixture fixture, bool optIn = true)
    {
        var orders = await OrdersKit.CreateAsync(fixture);

        // Explicit entitlements (#51): Professional plan + the AI assistant add-on (free activation), not the trial.
        await BillingKit.SetPlanAsync(fixture, orders.Owner.TenantId, BillingKit.Professional);
        await BillingKit.ActivateAsync(orders.Owner);
        var unit = await orders.Owner.CreateUnitAsync("takim", "Takım");
        var variants = new Dictionary<string, Guid>();
        foreach (var (sku, name, price) in Products)
        {
            var product = await orders.Owner.CreateSimpleProductAsync(unit.Id, name, sku);
            var variant = Assert.Single(product.Variants).Id;
            await orders.PriceAsync(orders.PriceListId, variant, price);
            variants[sku] = variant;
        }

        if (optIn)
        {
            await OptInAsync(orders.Owner);
        }

        return new AssistantKit(orders, variants);
    }

    /// <summary>Switches the tenant's AI opt-in (owner/admin).</summary>
    public static async Task<AiSettingsResponse> OptInAsync(CatalogClient owner, bool enabled = true) =>
        await ReadAsync<AiSettingsResponse>(await owner.SendAsync(HttpMethod.Put, "/api/v1/ai/settings", new { aiParsingEnabled = enabled }), HttpStatusCode.OK);

    public static Task<HttpResponseMessage> SubmitAsync(CatalogClient client, string text, Guid? customerId = null, string? key = null, Guid? warehouseId = null) =>
        client.PostAsync("/api/v1/ai/order-parses", new { text, customerId, warehouseId }, key);

    /// <summary>Submits (202), runs the parse job on <paramref name="services"/>' host and returns the finished parse.</summary>
    public static async Task<OrderParseResponse> SubmitOkAsync(IServiceProvider services, CatalogClient client, string text, Guid? customerId = null, Guid? warehouseId = null)
    {
        var accepted = await ReadAsync<OrderParseAcceptedResponse>(await SubmitAsync(client, text, customerId, warehouseId: warehouseId), HttpStatusCode.Accepted);
        Assert.Equal("pending", accepted.Status);
        await ProcessAsync(services, client, accepted.Id);
        return await ReadAsync<OrderParseResponse>(await client.GetAsync(accepted.ResultUrl), HttpStatusCode.OK);
    }

    /// <summary>
    /// Runs the AI parse jobs of the host until the parse has left <c>pending</c>. The processor only claims
    /// <c>integrations.ai_order_parse</c> jobs (never another test's imports); it may finish other tests' parses too,
    /// which is harmless — every host of the parallel tests runs with AI off.
    /// </summary>
    public static async Task<OrderParseStatusResponse> ProcessAsync(IServiceProvider services, CatalogClient client, Guid parseId, string workerId = "assistant-tests")
    {
        var processor = Processor(services, workerId);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            var status = await ReadAsync<OrderParseStatusResponse>(await client.GetAsync($"/api/v1/ai/order-parses/{parseId}/status"), HttpStatusCode.OK);
            if (status.State != "pending")
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Parse {parseId} is still pending.");
            if (await processor.RunOnceAsync(Ct) == 0)
            {
                await Task.Delay(20, Ct);
            }
        }
    }

    /// <summary>Runs AI parse jobs until the tenant has none queued or running.</summary>
    public static async Task DrainAsync(ApiFixture fixture, IServiceProvider services, Guid tenantId, string workerId = "assistant-drain")
    {
        var processor = Processor(services, workerId);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (await ScalarAsync<long>(
                   fixture,
                   "SELECT count(*) FROM platform.jobs WHERE tenant_id = $1 AND type = 'integrations.ai_order_parse' AND status IN ('queued', 'running')",
                   tenantId) > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, $"AI parse jobs of tenant {tenantId} are still queued.");
            if (await processor.RunOnceAsync(Ct) == 0)
            {
                await Task.Delay(20, Ct);
            }
        }
    }

    /// <summary>
    /// Runs every due AI parse job of every tenant on the shared (AI off) host — for the tests that run alone and must
    /// start from an empty queue (parses other tests submitted but never processed).
    /// </summary>
    public static async Task DrainAllAsync(ApiFixture fixture)
    {
        var processor = Processor(fixture.Services, "assistant-drain-all", batchSize: 50);
        while (await ScalarAsync<long>(
                   fixture, "SELECT count(*) FROM platform.jobs WHERE type = 'integrations.ai_order_parse' AND status = 'queued' AND run_after <= now()") > 0)
        {
            await processor.RunOnceAsync(Ct);
        }
    }

    /// <summary>A job processor of the host that claims only AI parse jobs.</summary>
    public static JobProcessor Processor(IServiceProvider services, string workerId, int batchSize = 10)
    {
        var registry = JobHandlerRegistry.FromRegistrations(
            services.GetServices<JobHandlerRegistration>().Where(r => r.Type == IntegrationsModule.OrderParseJobType), []);
        return new JobProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<JobStore>(),
            registry,
            new JobsOptions { WorkerId = workerId, BatchSize = batchSize },
            NullLogger<JobProcessor>.Instance);
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
/// <param name="marker">
/// Only requests whose message contains this text are this test's: they are recorded and answered from the queue.
/// Any other request (another test's parse picked up by this host's worker) gets a 500 and is not recorded, so it
/// can neither consume nor count against this test's canned replies.
/// </param>
internal sealed class FakeClaude(string marker) : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<string> Requests { get; } = [];

    private TaskCompletionSource? _gate;

    /// <summary>When set, the next one of this test's calls waits for it (a slow provider); later calls do not.</summary>
    public TaskCompletionSource? Gate
    {
        get => _gate;
        set => _gate = value;
    }

    /// <summary>Completed when one of this test's calls has started (and is waiting on <see cref="Gate"/>).</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        if (!content.Contains(marker, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }

        (HttpStatusCode Status, string Body) reply;
        lock (Requests)
        {
            Requests.Add(content);
            reply = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
        }

        Entered.TrySetResult();
        if (Interlocked.Exchange(ref _gate, null) is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        await Task.Yield();
        return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
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

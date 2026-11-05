using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;

namespace BayiOS.UnitTests.Integrations;

/// <summary>
/// Offline stand-in for the Claude Messages API: answers with responses in the exact wire shape of
/// <c>POST /v1/messages</c> (thinking block + text block, stop_reason, usage with cache tokens) and records every
/// request body. No network, no API key — CI never calls the provider.
/// </summary>
internal sealed class RecordedClaude : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<JsonObject> Requests { get; } = [];

    public List<HttpRequestMessage> RawRequests { get; } = [];

    public List<string[]> HeaderNames { get; } = [];

    public RecordedClaude Reply(string text, string stopReason = "end_turn", int input = 214, int output = 380, int cacheRead = 1650, int cacheWrite = 0)
    {
        _responses.Enqueue(() => Json(HttpStatusCode.OK, Message(text, stopReason, input, output, cacheRead, cacheWrite)));
        return this;
    }

    public RecordedClaude Refusal()
    {
        _responses.Enqueue(() =>
        {
            var body = Message(string.Empty, "refusal", 214, 0, 1650, 0);
            body["content"] = new JsonArray();
            body["stop_details"] = new JsonObject { ["type"] = "refusal", ["category"] = "cyber", ["explanation"] = "declined" };
            return Json(HttpStatusCode.OK, body);
        });
        return this;
    }

    public RecordedClaude Error(HttpStatusCode status, string type = "api_error")
    {
        _responses.Enqueue(() => Json(status, new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = "synthetic failure" },
            ["request_id"] = "req_test",
        }));
        return this;
    }

    public RecordedClaude Hang()
    {
        _responses.Enqueue(() => throw new TaskCanceledException("synthetic timeout", new TimeoutException()));
        return this;
    }

    public AnthropicClient Client(int maxRetries = 0) => new()
    {
        ApiKey = "test-key-not-a-real-secret",
        HttpClient = new HttpClient(this),
        BaseUrl = "https://claude.test",
        MaxRetries = maxRetries,
        Timeout = TimeSpan.FromSeconds(5),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RawRequests.Add(request);
        HeaderNames.Add([.. request.Headers.Select(h => h.Key.ToLowerInvariant())]);
        if (request.Content is not null)
        {
            Requests.Add(JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject());
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("RecordedClaude: no response queued.");
        }

        return _responses.Dequeue()();
    }

    public static JsonObject Message(string text, string stopReason, int input, int output, int cacheRead, int cacheWrite) => new()
    {
        ["id"] = "msg_test_" + Guid.NewGuid().ToString("N")[..12],
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = "claude-opus-5-5",
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "thinking", ["thinking"] = string.Empty, ["signature"] = "c2lnbmF0dXJl" },
            new JsonObject { ["type"] = "text", ["text"] = text },
        },
        ["stop_reason"] = stopReason,
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["cache_creation_input_tokens"] = cacheWrite,
            ["cache_read_input_tokens"] = cacheRead,
        },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Headers = { { "request-id", "req_test" } },
        };

    /// <summary>A model output as the schema requires (one object per line, every field present).</summary>
    public static string Output(params object[] lines) => Output(lines, [], []);

    public static string Output(object[] lines, string[] fragments, string[] warnings) =>
        JsonSerializer.Serialize(new { lines, unparsedFragments = fragments, warnings });

    public static object Line(
        string rawText, string productText, decimal? quantity, string? unit = null, string? productCode = null, string? brandModel = null,
        decimal confidence = 0.9m, string? notes = null) =>
        new { rawText, productCode, productText, quantity, unit, brandModel, confidence, notes };
}

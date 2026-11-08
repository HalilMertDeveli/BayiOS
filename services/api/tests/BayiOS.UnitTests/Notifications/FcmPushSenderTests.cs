using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BayiOS.Modules.Notifications.Application;
using BayiOS.Modules.Notifications.Push;
using Microsoft.Extensions.Logging;

namespace BayiOS.UnitTests.Notifications;

/// <summary>
/// The real FCM HTTP v1 sender and its Google service-account auth (#44), driven through a fake HTTP handler: no
/// request ever leaves the process. Covers the request shape, error classification, token caching and that neither
/// the device token nor the private key is logged.
/// </summary>
public sealed class FcmPushSenderTests
{
    private const string DeviceToken = "fake-device:APA91bH0123456789abcdefghijklmnopqrstuvwxyz";
    // Split so secret scanners do not mistake the corrupt test PEM for a key.
    private const string PemLabel = "PRIVATE " + "KEY";
    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly Guid TaskId = Guid.Parse("01933b5e-0000-7000-8000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("01933b5e-0000-7000-8000-0000000000aa");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string KeyJson(string? tokenUri = "https://oauth2.googleapis.com/token", string type = "service_account", string? privateKey = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["type"] = type,
            ["project_id"] = "bayios-test",
            ["private_key_id"] = "kid-1",
            ["private_key"] = privateKey ?? Rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "push@bayios-test.iam.gserviceaccount.com",
            ["token_uri"] = tokenUri,
        });

    private static PushMessage Message() => PushMessages.For(new PushSendPayload(PushKinds.PickingTaskAssigned, Guid.NewGuid(), TaskId), TenantId);

    private sealed class Harness
    {
        public Harness(Func<HttpRequestMessage, HttpResponseMessage> fcm)
        {
            Handler = new FakeHandler(request => request.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json(HttpStatusCode.OK, $$"""{"access_token":"ya29.access-{{Interlocked.Increment(ref _tokens)}}","expires_in":3599,"token_type":"Bearer"}""")
                : fcm(request));
            var http = new HttpClient(Handler);
            Tokens = new GoogleAccessTokenSource(ServiceAccountKey.Parse(KeyJson()), () => http, Clock);
            Sender = new FcmPushSender(() => http, Tokens, "bayios-test", Logs);
        }

        private int _tokens;

        public FakeHandler Handler { get; }

        public ManualClock Clock { get; } = new(DateTimeOffset.Parse("2026-11-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        public GoogleAccessTokenSource Tokens { get; }

        public FcmPushSender Sender { get; }

        public ListLogger<FcmPushSender> Logs { get; } = new();

        public IReadOnlyList<HttpRequestMessage> FcmRequests => [.. Handler.Requests.Where(r => r.RequestUri!.Host == "fcm.googleapis.com")];

        public IReadOnlyList<HttpRequestMessage> TokenRequests => [.. Handler.Requests.Where(r => r.RequestUri!.Host == "oauth2.googleapis.com")];
    }

    [Fact]
    public async Task Sends_the_v1_message_with_the_oauth_token_and_no_personal_data()
    {
        var harness = new Harness(_ => Json(HttpStatusCode.OK, """{"name":"projects/bayios-test/messages/0:1"}"""));

        Assert.Equal(PushOutcome.Delivered, await harness.Sender.SendAsync(DeviceToken, Message(), Ct));

        var request = Assert.Single(harness.FcmRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://fcm.googleapis.com/v1/projects/bayios-test/messages:send", request.RequestUri!.ToString());
        Assert.Equal("Bearer ya29.access-1", request.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(harness.Handler.Bodies[request]);
        var message = body.RootElement.GetProperty("message");
        Assert.Equal(DeviceToken, message.GetProperty("token").GetString());
        Assert.Equal("Yeni toplama görevi", message.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("Size bir toplama görevi atandı. Açmak için dokunun.", message.GetProperty("notification").GetProperty("body").GetString());
        Assert.Equal(
            [("taskId", TaskId.ToString()), ("tenantId", TenantId.ToString()), ("type", "picking_task_assigned")],
            message.GetProperty("data").EnumerateObject().Select(p => (p.Name, p.Value.GetString())));
        var collapse = $"task-{TaskId:N}";
        Assert.Equal(collapse, message.GetProperty("android").GetProperty("collapse_key").GetString());
        Assert.Equal(collapse, message.GetProperty("android").GetProperty("notification").GetProperty("tag").GetString());
        Assert.Equal("HIGH", message.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal(collapse, message.GetProperty("apns").GetProperty("headers").GetProperty("apns-collapse-id").GetString());
        Assert.Equal(["android", "apns", "data", "notification", "token"], message.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    public static TheoryData<HttpStatusCode, string, string> Responses => new()
    {
        { HttpStatusCode.NotFound, Error(404, "NOT_FOUND", "Requested entity was not found.", "UNREGISTERED"), nameof(PushOutcome.Unregistered) },
        { HttpStatusCode.Forbidden, Error(403, "PERMISSION_DENIED", "SenderId mismatch", "SENDER_ID_MISMATCH"), nameof(PushOutcome.Unregistered) },
        { HttpStatusCode.BadRequest, Error(400, "INVALID_ARGUMENT", "The registration token is not a valid FCM registration token", "INVALID_ARGUMENT"), nameof(PushOutcome.Unregistered) },
        { HttpStatusCode.BadRequest, Error(400, "INVALID_ARGUMENT", "Invalid value at 'message.data[0].value'", "INVALID_ARGUMENT"), nameof(PushOutcome.Failed) },
        { HttpStatusCode.NotFound, Error(404, "NOT_FOUND", "Project not found.", null), nameof(PushOutcome.Failed) }, // wrong project id: keep every token
        { HttpStatusCode.Unauthorized, Error(401, "UNAUTHENTICATED", "APNs auth error", "THIRD_PARTY_AUTH_ERROR"), nameof(PushOutcome.Failed) },
        { HttpStatusCode.TooManyRequests, Error(429, "RESOURCE_EXHAUSTED", "Quota exceeded", "QUOTA_EXCEEDED"), nameof(PushOutcome.Transient) },
        { HttpStatusCode.ServiceUnavailable, Error(503, "UNAVAILABLE", "The server is overloaded.", "UNAVAILABLE"), nameof(PushOutcome.Transient) },
        { HttpStatusCode.InternalServerError, "not json", nameof(PushOutcome.Transient) },
        { HttpStatusCode.Forbidden, Error(403, "PERMISSION_DENIED", "Permission denied", null), nameof(PushOutcome.Failed) },
    };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Classifies_fcm_errors(HttpStatusCode status, string body, string expected)
    {
        var harness = new Harness(_ => Json(status, body));
        Assert.Equal(Enum.Parse<PushOutcome>(expected), await harness.Sender.SendAsync(DeviceToken, Message(), Ct));
        AssertNoSecretsLogged(harness);
    }

    [Fact]
    public async Task An_expired_access_token_is_dropped_and_fetched_again()
    {
        var calls = 0;
        var harness = new Harness(_ => ++calls == 1
            ? Json(HttpStatusCode.Unauthorized, Error(401, "UNAUTHENTICATED", "Request had invalid authentication credentials.", null))
            : Json(HttpStatusCode.OK, "{}"));

        Assert.Equal(PushOutcome.Transient, await harness.Sender.SendAsync(DeviceToken, Message(), Ct));
        Assert.Equal(PushOutcome.Delivered, await harness.Sender.SendAsync(DeviceToken, Message(), Ct));
        Assert.Equal(2, harness.TokenRequests.Count);
        Assert.Equal("Bearer ya29.access-2", harness.FcmRequests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Access_tokens_are_cached_until_shortly_before_expiry()
    {
        var harness = new Harness(_ => Json(HttpStatusCode.OK, "{}"));
        await harness.Sender.SendAsync(DeviceToken, Message(), Ct);
        await harness.Sender.SendAsync(DeviceToken, Message(), Ct);
        Assert.Single(harness.TokenRequests);

        harness.Clock.Advance(TimeSpan.FromMinutes(56)); // 3599 s token, refreshed 5 min early
        await harness.Sender.SendAsync(DeviceToken, Message(), Ct);
        Assert.Equal(2, harness.TokenRequests.Count);
    }

    [Fact]
    public async Task Network_errors_and_timeouts_are_temporary()
    {
        var harness = new Harness(_ => throw new HttpRequestException("connection reset"));
        Assert.Equal(PushOutcome.Transient, await harness.Sender.SendAsync(DeviceToken, Message(), Ct));

        var timeout = new Harness(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(PushOutcome.Transient, await timeout.Sender.SendAsync(DeviceToken, Message(), Ct));
    }

    [Fact]
    public async Task The_oauth_assertion_is_an_rs256_jwt_for_the_messaging_scope()
    {
        var harness = new Harness(_ => Json(HttpStatusCode.OK, "{}"));
        await harness.Sender.SendAsync(DeviceToken, Message(), Ct);

        var form = harness.Handler.Bodies[Assert.Single(harness.TokenRequests)];
        var fields = form.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", fields["grant_type"]);
        var parts = fields["assertion"].Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(Rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal(("RS256", "kid-1"), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("kid").GetString()));
        using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
        var c = claims.RootElement;
        Assert.Equal("push@bayios-test.iam.gserviceaccount.com", c.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", c.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", c.GetProperty("aud").GetString());
        Assert.Equal(3600, c.GetProperty("exp").GetInt64() - c.GetProperty("iat").GetInt64());
        Assert.Equal(harness.Clock.GetUtcNow().ToUnixTimeSeconds(), c.GetProperty("iat").GetInt64());
    }

    [Fact]
    public void Service_account_keys_are_validated_and_never_printed()
    {
        var key = ServiceAccountKey.Parse(KeyJson());
        Assert.Equal("bayios-test", key.ProjectId);
        Assert.DoesNotContain("PRIVATE KEY", key.ToString(), StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => ServiceAccountKey.Parse(KeyJson(type: "authorized_user")));
        Assert.Throws<InvalidOperationException>(() => ServiceAccountKey.Parse(KeyJson(tokenUri: "https://attacker.example/token")));
        Assert.Throws<InvalidOperationException>(() => ServiceAccountKey.Parse(KeyJson(privateKey: $"-----BEGIN {PemLabel}-----\nAAAA\n-----END {PemLabel}-----\n")));
        Assert.Throws<InvalidOperationException>(() => ServiceAccountKey.Parse(KeyJson(privateKey: "not a pem")));
        Assert.Throws<InvalidOperationException>(() => ServiceAccountKey.Parse(KeyJson(privateKey: Rsa.ExportSubjectPublicKeyInfoPem())));
    }

    [Fact]
    public async Task Disabled_sender_sends_nothing()
    {
        var sender = new DisabledPushSender(new ListLogger<DisabledPushSender>());
        Assert.False(sender.IsEnabled);
        Assert.Equal(PushOutcome.Skipped, await sender.SendAsync(DeviceToken, Message(), Ct));
    }

    [Fact]
    public void The_subscribed_event_type_is_the_one_warehouse_publishes()
    {
        Assert.Equal(BayiOS.Modules.Warehouse.Application.FulfillmentEvents.PickingTaskAssigned, PickingTaskAssignedHandler.MessageType);
    }

    private static void AssertNoSecretsLogged(Harness harness)
    {
        foreach (var line in harness.Logs.Lines)
        {
            Assert.DoesNotContain(DeviceToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain("ya29.", line, StringComparison.Ordinal);
        }
    }

    private static string Error(int code, string status, string message, string? errorCode) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code,
                message,
                status,
                details = errorCode is null
                    ? Array.Empty<object>()
                    : [new Dictionary<string, string> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = errorCode }],
            },
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public ConcurrentDictionary<HttpRequestMessage, string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Bodies[request] = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + " " + exception);
    }
}

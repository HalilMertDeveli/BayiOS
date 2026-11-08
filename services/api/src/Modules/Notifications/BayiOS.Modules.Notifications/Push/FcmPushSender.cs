using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BayiOS.Modules.Notifications.Push;

/// <summary>
/// FCM HTTP v1 (<c>POST https://fcm.googleapis.com/v1/projects/{project}/messages:send</c>), one request per token (v1
/// has no multicast). Only used with <c>Push:Enabled=true</c>; CI exercises it with a fake HTTP handler only.
/// </summary>
internal sealed partial class FcmPushSender(
    Func<HttpClient> httpClient, GoogleAccessTokenSource tokens, string projectId, ILogger<FcmPushSender> logger) : IPushSender
{
    public bool IsEnabled => true;

    public async Task<PushOutcome> SendAsync(string token, PushMessage message, CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await tokens.GetAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogAuthFailed(logger, ex);
            return PushOutcome.Transient;
        }

        using var request = FcmRequest.Build(projectId, token, message);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response;
        try
        {
            response = await httpClient().SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogSendError(logger, ex.GetType().Name);
            return PushOutcome.Transient;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Delivered;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (outcome, errorCode) = FcmRequest.Classify(response.StatusCode, body);
            if (response.StatusCode == HttpStatusCode.Unauthorized && outcome == PushOutcome.Transient)
            {
                tokens.Invalidate(); // expired/revoked access token: fetch a new one on the retry
            }

            if (outcome == PushOutcome.Failed)
            {
                LogRejected(logger, (int)response.StatusCode, errorCode);
            }
            else
            {
                LogNotDelivered(logger, (int)response.StatusCode, errorCode, outcome);
            }

            return outcome;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FCM access token could not be obtained")]
    private static partial void LogAuthFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FCM send failed before a response ({ErrorType})")]
    private static partial void LogSendError(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "FCM answered {Status} ({ErrorCode}): {Outcome}")]
    private static partial void LogNotDelivered(ILogger logger, int status, string errorCode, PushOutcome outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "FCM rejected the message permanently: {Status} ({ErrorCode})")]
    private static partial void LogRejected(ILogger logger, int status, string errorCode);
}

/// <summary>Builds FCM v1 requests and classifies their errors (pure; unit-tested with a fake HTTP handler).</summary>
internal static class FcmRequest
{
    public static readonly Uri BaseUri = new("https://fcm.googleapis.com/");

    public static HttpRequestMessage Build(string projectId, string token, PushMessage message)
    {
        var data = new JsonObject();
        foreach (var (name, value) in message.Data.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            data[name] = value;
        }

        var body = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["token"] = token,
                ["notification"] = new JsonObject { ["title"] = message.Title, ["body"] = message.Body },
                ["data"] = data,
                ["android"] = new JsonObject
                {
                    ["priority"] = "HIGH",
                    ["ttl"] = "86400s",
                    ["collapse_key"] = message.CollapseKey,
                    // Same tag = a resent notification replaces the shown one instead of stacking.
                    ["notification"] = new JsonObject { ["tag"] = message.CollapseKey },
                },
                ["apns"] = new JsonObject
                {
                    ["headers"] = new JsonObject
                    {
                        ["apns-priority"] = "10",
                        ["apns-collapse-id"] = message.CollapseKey,
                    },
                    ["payload"] = new JsonObject { ["aps"] = new JsonObject { ["sound"] = "default" } },
                },
            },
        };

        return new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, $"v1/projects/{Uri.EscapeDataString(projectId)}/messages:send"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// FCM v1 error → what to do (firebase.google.com/docs/cloud-messaging/manage-tokens): UNREGISTERED (404),
    /// SENDER_ID_MISMATCH (403) and an INVALID_ARGUMENT naming the registration token → delete the token; quota, 5xx
    /// and our own expired OAuth token → retry; anything else (bad payload, APNs auth, wrong project) → permanent,
    /// logged, tokens kept.
    /// </summary>
    public static (PushOutcome Outcome, string ErrorCode) Classify(HttpStatusCode status, string? body)
    {
        var (errorCode, message) = ParseError(body);
        // Only FCM's explicit verdict deletes a token: a bare 404 may as well be a wrong project id (all tokens!).
        if (errorCode is "UNREGISTERED" or "SENDER_ID_MISMATCH")
        {
            return (PushOutcome.Unregistered, errorCode);
        }

        if (errorCode == "INVALID_ARGUMENT" || (status == HttpStatusCode.BadRequest && errorCode.Length == 0))
        {
            var tokenProblem = message.Contains("registration token", StringComparison.OrdinalIgnoreCase);
            return (tokenProblem ? PushOutcome.Unregistered : PushOutcome.Failed, errorCode.Length == 0 ? "INVALID_ARGUMENT" : errorCode);
        }

        if (errorCode == "THIRD_PARTY_AUTH_ERROR")
        {
            return (PushOutcome.Failed, errorCode); // APNs key/certificate problem: fix the Firebase project, keep tokens
        }

        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized || (int)status >= 500
            || errorCode is "QUOTA_EXCEEDED" or "UNAVAILABLE" or "INTERNAL")
        {
            return (PushOutcome.Transient, errorCode.Length == 0 ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) : errorCode);
        }

        return (PushOutcome.Failed, errorCode.Length == 0 ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) : errorCode);
    }

    private static (string ErrorCode, string Message) ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, string.Empty);
            }

            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                    {
                        return (code.GetString() ?? string.Empty, message);
                    }
                }
            }

            var status = error.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
            return (status, message);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }
}

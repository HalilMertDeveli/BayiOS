using Microsoft.Extensions.Logging;

namespace BayiOS.Modules.Notifications.Push;

/// <summary>
/// One push notification. No personal data and no prices (data-policy §9): a short Turkish title/body and string data
/// with ids only (<c>type</c>, <c>taskId</c>, <c>tenantId</c>) that the app uses to deep-link.
/// </summary>
/// <param name="CollapseKey">Same key = the device replaces the earlier notification (a retried send shows once).</param>
internal sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> Data, string CollapseKey);

internal enum PushOutcome
{
    /// <summary>FCM accepted the message.</summary>
    Delivered,

    /// <summary>Push is disabled (<c>Push:Enabled=false</c>): nothing was sent.</summary>
    Skipped,

    /// <summary>The token is unknown, expired or belongs to another Firebase project: delete the registration.</summary>
    Unregistered,

    /// <summary>Temporary (quota, 5xx, timeout, network): retry the job with backoff.</summary>
    Transient,

    /// <summary>Permanent for this message (e.g. APNs misconfiguration); logged, not retried, token kept.</summary>
    Failed,
}

/// <summary>Sends one message to one device token. Implementations never log the token.</summary>
internal interface IPushSender
{
    /// <summary>False for the disabled (no-op) sender.</summary>
    bool IsEnabled { get; }

    Task<PushOutcome> SendAsync(string token, PushMessage message, CancellationToken cancellationToken);
}

/// <summary><c>Push:Enabled=false</c> (the default; no Firebase project yet): sends nothing, logs only counts.</summary>
internal sealed partial class DisabledPushSender(ILogger<DisabledPushSender> logger) : IPushSender
{
    public bool IsEnabled => false;

    public Task<PushOutcome> SendAsync(string token, PushMessage message, CancellationToken cancellationToken)
    {
        var type = message.Data.GetValueOrDefault("type") ?? "unknown";
        LogSkipped(logger, type);
        return Task.FromResult(PushOutcome.Skipped);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Push disabled (Push:Enabled=false): {MessageType} not sent")]
    private static partial void LogSkipped(ILogger logger, string messageType);
}

using BayiOS.SharedKernel.Authorization;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>A stable Problem Details code (api-strategy §6).</summary>
/// <param name="RetryAfterSeconds">Sent as <c>Retry-After</c> (429).</param>
internal sealed record AppError(
    int Status,
    string Code,
    string Title,
    IDictionary<string, string[]>? Errors = null,
    int? RetryAfterSeconds = null,
    IReadOnlyDictionary<string, object>? Extensions = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static AppError Forbidden(string permission) =>
        new(403, AuthorizationProblems.Forbidden, $"Missing permission: {permission}", Extensions: new Dictionary<string, object> { ["permission"] = permission });
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Problem codes of the assistant (api-strategy §6).</summary>
internal static class AssistantProblems
{
    /// <summary>429: the tenant's hourly parse limit is reached (Retry-After until the next hour).</summary>
    public const string RateLimited = "ai.rate_limited";

    /// <summary>409: the parse was already approved or rejected.</summary>
    public const string NotReviewable = "ai.parse_not_reviewable";

    /// <summary>409: the message text was removed after the retention period; the parse can no longer be approved.</summary>
    public const string TextPurged = "ai.parse_text_purged";

    /// <summary>409: the tenant has not opted in to AI parsing (data-policy §7); an owner/admin can switch it on.</summary>
    public const string OptInRequired = "ai.opt_in_required";
}

/// <summary>Audit action codes (threat-model §7 "AI"). Changes carry counts and codes only — never message text.</summary>
internal static class AssistantAuditActions
{
    public const string EntityType = "ai_parse_request";

    /// <summary>A message was accepted for parsing (text length, whether a customer was given).</summary>
    public const string ParseRequested = "ai.parse_requested";

    /// <summary>The background job parsed and matched the message (parser, fallback reason, line/match counts, tokens).</summary>
    public const string ParseCompleted = "ai.parse_completed";

    /// <summary>The background job gave up on the message (failure code).</summary>
    public const string ParseFailed = "ai.parse_failed";

    public const string SettingsEntityType = "ai_settings";

    /// <summary>An owner/admin switched the tenant's AI opt-in (before/after).</summary>
    public const string SettingsChanged = "ai.settings_changed";

    public const string AliasEntityType = "ai_learned_alias";

    /// <summary>A learned alias was removed (phrase length and variant only).</summary>
    public const string AliasDeleted = "ai.alias_deleted";

    /// <summary>The reviewer created the draft order from the parse (order id, selection counts).</summary>
    public const string DraftCreated = "ai.draft_created";

    /// <summary>The reviewer rejected the parse (no order).</summary>
    public const string DraftRejected = "ai.draft_rejected";
}

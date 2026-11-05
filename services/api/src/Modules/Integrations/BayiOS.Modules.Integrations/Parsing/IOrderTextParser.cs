namespace BayiOS.Modules.Integrations.Parsing;

/// <summary>Which parser produced a parse.</summary>
public static class ParserKinds
{
    /// <summary>The Claude model (structured output), validated on the server.</summary>
    public const string Ai = "ai";

    /// <summary>The deterministic rules parser (AI off, unconfigured, over quota or failed).</summary>
    public const string Rules = "rules";

    public static readonly IReadOnlyList<string> All = [Ai, Rules];
}

/// <summary>Why the rules parser was used instead of the model (null when the model's parse was used).</summary>
public static class FallbackReasons
{
    public const string Disabled = "disabled";
    public const string QuotaExceeded = "quota_exceeded";
    public const string ProviderError = "provider_error";
    public const string Timeout = "timeout";
    public const string Refused = "refused";
    public const string Truncated = "truncated";
    public const string InvalidOutput = "invalid_output";

    public static readonly IReadOnlyList<string> All = [Disabled, QuotaExceeded, ProviderError, Timeout, Refused, Truncated, InvalidOutput];
}

/// <summary>Tokens of one model call (Messages API <c>usage</c>).</summary>
public sealed record AiTokenUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens)
{
    public static AiTokenUsage None { get; } = new(0, 0, 0, 0);

    public long Total => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}

/// <summary>Result of one parser call.</summary>
/// <param name="Parse">The validated parse; null when the parser failed (<paramref name="FailureReason"/> set).</param>
/// <param name="Model">Model id actually called (AI only).</param>
/// <param name="Usage">Tokens billed for the call (also for a failed or refused call).</param>
/// <param name="FailureReason">A <see cref="FallbackReasons"/> code when <paramref name="Parse"/> is null.</param>
/// <param name="FailureDetail">Sanitised technical detail for logs (never message text).</param>
public sealed record OrderTextParseResult(
    ParsedOrder? Parse,
    string Parser,
    string? Model,
    string PromptVersion,
    AiTokenUsage Usage,
    string? FailureReason = null,
    string? FailureDetail = null);

/// <summary>
/// Turns message text into a <see cref="ParsedOrder"/>. Implementations only read the text they are given (already
/// masked, data-policy §3); they never see the catalog, prices, stock or customer data, and their output is data.
/// </summary>
public interface IOrderTextParser
{
    string Kind { get; }

    Task<OrderTextParseResult> ParseAsync(string maskedText, CancellationToken cancellationToken);
}

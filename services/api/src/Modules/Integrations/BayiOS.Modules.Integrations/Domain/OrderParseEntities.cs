using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Integrations.Domain;

/// <summary>Review lifecycle of a parse: needs_review → approved (draft order created) | rejected.</summary>
internal static class OrderParseStatuses
{
    public const string NeedsReview = "needs_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All = [NeedsReview, Approved, Rejected];
}

internal static class OrderParseChannels
{
    /// <summary>Text pasted by a user (MVP). A WhatsApp Business webhook channel needs its own decision (ADR 0010 §5).</summary>
    public const string WhatsappPaste = "whatsapp_paste";

    public static readonly IReadOnlyList<string> All = [WhatsappPaste];
}

/// <summary>
/// integrations.ai_parse_requests — one pasted message, its validated parse and the deterministic match result
/// (data-model §5.10). <see cref="RawText"/>, <see cref="ParsedPayload"/> and <see cref="Review"/> carry message text
/// (personal data, data-policy §2): they are cleared after the retention window by
/// <c>integrations.purge_expired_parse_texts</c>; counts, tokens and the order link stay.
/// </summary>
internal sealed class OrderParseRequest : ITenantOwned
{
    private OrderParseRequest()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public string Channel { get; private init; } = OrderParseChannels.WhatsappPaste;

    /// <summary>Customer the user picked for the message (optional; never sent to the AI provider).</summary>
    public Guid? CustomerId { get; private init; }

    /// <summary>The message as pasted; null once purged.</summary>
    public string? RawText { get; set; }

    /// <summary>SHA-256 (hex) of the whitespace-normalised text: duplicate detection without keeping the text.</summary>
    public string TextSha256 { get; private init; } = string.Empty;

    public int TextLength { get; private init; }

    public string Status { get; set; } = OrderParseStatuses.NeedsReview;

    /// <summary><c>ai</c> or <c>rules</c> (ParserKinds).</summary>
    public string Parser { get; private init; } = string.Empty;

    /// <summary>Model id called (null for the rules parser).</summary>
    public string? Model { get; private init; }

    public string PromptVersion { get; private init; } = string.Empty;

    /// <summary>Why the rules parser was used although AI is enabled (FallbackReasons), else null.</summary>
    public string? FallbackReason { get; private init; }

    /// <summary>jsonb: the validated ParsedOrder; null once purged.</summary>
    public string? ParsedPayload { get; set; }

    /// <summary>jsonb: review lines with match status and candidates; null once purged.</summary>
    public string? Review { get; set; }

    /// <summary>jsonb array of warning codes (no text).</summary>
    public string Warnings { get; private init; } = "[]";

    public int LineCount { get; private init; }

    public int MatchedCount { get; private init; }

    public int AmbiguousCount { get; private init; }

    public int UnmatchedCount { get; private init; }

    public decimal OverallConfidence { get; private init; }

    public int InputTokens { get; private init; }

    public int OutputTokens { get; private init; }

    public int CacheReadTokens { get; private init; }

    public int CacheWriteTokens { get; private init; }

    public int DurationMs { get; private init; }

    /// <summary>The draft order created on approval.</summary>
    public Guid? OrderId { get; set; }

    public Guid? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public DateTimeOffset? TextPurgedAt { get; private init; }

    public long RowVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid? CreatedBy { get; private init; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public static OrderParseRequest Create(
        Guid tenantId,
        Guid? customerId,
        string rawText,
        string textSha256,
        string parser,
        string? model,
        string promptVersion,
        string? fallbackReason,
        string parsedPayload,
        string review,
        string warnings,
        (int Lines, int Matched, int Ambiguous, int Unmatched) counts,
        decimal overallConfidence,
        (long Input, long Output, long CacheRead, long CacheWrite) tokens,
        int durationMs,
        Guid userId,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CustomerId = customerId,
            RawText = rawText,
            TextSha256 = textSha256,
            TextLength = rawText.Length,
            Parser = parser,
            Model = model,
            PromptVersion = promptVersion,
            FallbackReason = fallbackReason,
            ParsedPayload = parsedPayload,
            Review = review,
            Warnings = warnings,
            LineCount = counts.Lines,
            MatchedCount = counts.Matched,
            AmbiguousCount = counts.Ambiguous,
            UnmatchedCount = counts.Unmatched,
            OverallConfidence = overallConfidence,
            InputTokens = Clamp(tokens.Input),
            OutputTokens = Clamp(tokens.Output),
            CacheReadTokens = Clamp(tokens.CacheRead),
            CacheWriteTokens = Clamp(tokens.CacheWrite),
            DurationMs = durationMs,
            CreatedAt = now,
            CreatedBy = userId,
        };

    public void Touch(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}

/// <summary>
/// integrations.ai_usage — per tenant and clock hour (UTC): parse requests, AI calls, failures, fallbacks and tokens.
/// Written only by AiUsageMeter with atomic upserts on its own short transactions (multi-instance safe); read for
/// the hourly rate limit, the daily quota and the usage endpoint. No personal data.
/// </summary>
internal sealed class AiUsageHour : ITenantOwned
{
    public Guid TenantId { get; private init; }

    public DateTimeOffset UsageHour { get; private init; }

    public int ParseRequests { get; private init; }

    public int AiCalls { get; private init; }

    public int AiFailures { get; private init; }

    public int Fallbacks { get; private init; }

    public long InputTokens { get; private init; }

    public long OutputTokens { get; private init; }

    public long CacheReadTokens { get; private init; }

    public long CacheWriteTokens { get; private init; }
}

using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Integrations.Domain;

/// <summary>
/// Lifecycle of a parse (ADR 0010 §2): pending (accepted, the background job has not finished) → needs_review
/// (parsed and matched) | failed (the job gave up); needs_review → approved (draft order created) | rejected.
/// </summary>
internal static class OrderParseStatuses
{
    public const string Pending = "pending";
    public const string Failed = "failed";
    public const string NeedsReview = "needs_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All = [Pending, Failed, NeedsReview, Approved, Rejected];

    /// <summary>The job finished and produced a review (whatever the reviewer did with it since).</summary>
    public static bool IsParsed(string status) => status is NeedsReview or Approved or Rejected;
}

/// <summary>Why a parse failed (stable codes on the status endpoint; no message text).</summary>
internal static class ParseFailureCodes
{
    /// <summary>The background job ran out of attempts (database or other unexpected error).</summary>
    public const string ProcessingFailed = "ai.parse_failed";

    /// <summary>The parse was not processed within <c>Ai:ProcessingDeadlineSeconds</c> (worker down, queue stuck).</summary>
    public const string TimedOut = "ai.parse_timed_out";

    /// <summary>The tenant switched AI parsing off before the job ran: the text was not sent anywhere.</summary>
    public const string OptInRevoked = "ai.opt_in_revoked";

    public static readonly IReadOnlyList<string> All = [ProcessingFailed, TimedOut, OptInRevoked];
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

    /// <summary>Warehouse the user picked on submit (optional): the default of the approval.</summary>
    public Guid? WarehouseId { get; private init; }

    /// <summary>The message as pasted (masked); null once purged.</summary>
    public string? RawText { get; set; }

    /// <summary>SHA-256 (hex) of the whitespace-normalised text: duplicate detection without keeping the text.</summary>
    public string TextSha256 { get; private init; } = string.Empty;

    public int TextLength { get; private init; }

    public string Status { get; set; } = OrderParseStatuses.Pending;

    /// <summary><c>ai</c> or <c>rules</c> (ParserKinds); null while pending or when the parse failed.</summary>
    public string? Parser { get; private set; }

    /// <summary>Model id called (null for the rules parser).</summary>
    public string? Model { get; private set; }

    public string? PromptVersion { get; private set; }

    /// <summary>Why the rules parser was used although AI is enabled (FallbackReasons), else null.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>ParseFailureCodes when <see cref="Status"/> is failed.</summary>
    public string? FailureCode { get; private set; }

    /// <summary>jsonb: the validated ParsedOrder; null while pending and once purged.</summary>
    public string? ParsedPayload { get; set; }

    /// <summary>jsonb: review lines with match status and candidates; null while pending and once purged.</summary>
    public string? Review { get; set; }

    /// <summary>jsonb array of warning codes (no text).</summary>
    public string Warnings { get; private set; } = "[]";

    public int LineCount { get; private set; }

    public int MatchedCount { get; private set; }

    public int AmbiguousCount { get; private set; }

    public int UnmatchedCount { get; private set; }

    public decimal OverallConfidence { get; private set; }

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public int CacheReadTokens { get; private set; }

    public int CacheWriteTokens { get; private set; }

    /// <summary>Parse + match time of the job (the provider call included), in milliseconds.</summary>
    public int DurationMs { get; private set; }

    /// <summary>When the job finished (parsed or failed).</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

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

    /// <summary>An accepted message waiting for the background parse (ADR 0010 §2.1).</summary>
    public static OrderParseRequest CreatePending(
        Guid tenantId,
        Guid? customerId,
        Guid? warehouseId,
        string maskedText,
        string textSha256,
        string warnings,
        Guid userId,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            RawText = maskedText,
            TextSha256 = textSha256,
            TextLength = maskedText.Length,
            Status = OrderParseStatuses.Pending,
            Warnings = warnings,
            CreatedAt = now,
            CreatedBy = userId,
        };

    /// <summary>The job's result: pending → needs_review.</summary>
    public void Complete(
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
        DateTimeOffset now)
    {
        if (Status != OrderParseStatuses.Pending)
        {
            throw new InvalidOperationException($"Parse {Id} is {Status}, not pending.");
        }

        Status = OrderParseStatuses.NeedsReview;
        Parser = parser;
        Model = model;
        PromptVersion = promptVersion;
        FallbackReason = fallbackReason;
        ParsedPayload = parsedPayload;
        Review = review;
        Warnings = warnings;
        LineCount = counts.Lines;
        MatchedCount = counts.Matched;
        AmbiguousCount = counts.Ambiguous;
        UnmatchedCount = counts.Unmatched;
        OverallConfidence = overallConfidence;
        InputTokens = Clamp(tokens.Input);
        OutputTokens = Clamp(tokens.Output);
        CacheReadTokens = Clamp(tokens.CacheRead);
        CacheWriteTokens = Clamp(tokens.CacheWrite);
        DurationMs = durationMs;
        CompletedAt = now;
        RowVersion++;
        UpdatedAt = now;
    }

    /// <summary>pending → failed (no review; the text is kept until retention like any other parse).</summary>
    public void Fail(string failureCode, DateTimeOffset now)
    {
        if (Status != OrderParseStatuses.Pending)
        {
            throw new InvalidOperationException($"Parse {Id} is {Status}, not pending.");
        }

        Status = OrderParseStatuses.Failed;
        FailureCode = failureCode;
        CompletedAt = now;
        RowVersion++;
        UpdatedAt = now;
    }

    public void Touch(Guid userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}

/// <summary>
/// integrations.ai_settings — the tenant's AI opt-in (data-policy §7: message text goes to a provider outside Türkiye
/// only when the tenant chose so). No row = off. Changed only by owner/admin (<c>tenant.settings.manage</c>), audited.
/// </summary>
internal sealed class AiTenantSettings : ITenantOwned
{
    private AiTenantSettings()
    {
    }

    public Guid TenantId { get; private init; }

    public bool AiParsingEnabled { get; private set; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static AiTenantSettings Create(Guid tenantId, bool enabled, Guid userId, DateTimeOffset now) =>
        new() { TenantId = tenantId, AiParsingEnabled = enabled, UpdatedAt = now, UpdatedBy = userId };

    public void Set(bool enabled, Guid userId, DateTimeOffset now)
    {
        AiParsingEnabled = enabled;
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>
/// integrations.learned_aliases — WAI-07 / ADR 0010 §2.6: a phrase a reviewer confirmed for a variant
/// ("beyaz ampul" → MTR-1040). Normalised phrase (TurkishText.Fold + tokens) → variant of the tenant's own catalog;
/// <see cref="CustomerId"/> set = only for that customer's messages (checked first), null = the whole tenant. Written
/// only from a successful approval (the Orders module validated the variant then); at match time an alias whose variant
/// is no longer an active catalog variant is ignored, so an alias can never introduce a product.
/// </summary>
internal sealed class LearnedAlias : ITenantOwned
{
    private LearnedAlias()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid? CustomerId { get; private init; }

    /// <summary>Normalised phrase (folded tokens joined by one space).</summary>
    public string Phrase { get; private init; } = string.Empty;

    public Guid VariantId { get; private set; }

    /// <summary>How often a reviewer confirmed this phrase → variant (reset when the variant changes).</summary>
    public int Confirmations { get; private set; }

    public Guid? LastParseId { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid? CreatedBy { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    // Written only by OrderParseService's atomic upsert (latest confirmation wins); read and deleted through EF.
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

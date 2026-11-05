namespace BayiOS.Modules.Integrations.Contracts;

// ---- Requests ----

/// <param name="Text">The pasted WhatsApp message (1–4000 characters by default, at most 150 lines).</param>
/// <param name="CustomerId">Optional customer the order is for; used as the default on approval, never sent to the AI provider.</param>
public sealed record SubmitOrderParseRequest(string? Text, Guid? CustomerId);

/// <summary>
/// Creates the draft order from the reviewed lines. Every line of the parse must be listed exactly once: included
/// lines name their variant explicitly (also for matched lines — the proposal is not taken implicitly), skipped lines
/// are listed with <c>include = false</c>.
/// </summary>
/// <param name="CustomerId">Null = the customer given on submit (one of the two is required).</param>
/// <param name="WarehouseId">Null = the tenant's default warehouse.</param>
/// <param name="Notes">Order notes typed by the reviewer (the message text is not copied into the order).</param>
public sealed record ApproveOrderParseRequest(Guid? CustomerId, Guid? WarehouseId, string? Notes, IReadOnlyList<ApproveOrderParseLine>? Lines);

/// <param name="LineNo">The review line number.</param>
/// <param name="Include">true = add to the draft; false = skip the line.</param>
/// <param name="VariantId">Required when included: a candidate of the line or any active variant of the tenant's catalog.</param>
/// <param name="Quantity">Null = the parsed quantity (required when the message had none).</param>
public sealed record ApproveOrderParseLine(int? LineNo, bool? Include, Guid? VariantId, decimal? Quantity);

// ---- Responses ----

/// <summary>A catalog candidate for a line. Never carries a price or a stock level.</summary>
/// <param name="Method">sku, barcode, name or similarity.</param>
public sealed record OrderParseCandidateResponse(Guid VariantId, Guid ProductId, string Sku, string Name, string Unit, string Method, decimal Score);

/// <param name="RawText">The part of the message the line was read from.</param>
/// <param name="ParseConfidence">The parser's certainty about reading the line (0–1).</param>
/// <param name="MatchStatus">matched (one proposal, still to be confirmed), ambiguous (choose a candidate) or unmatched.</param>
/// <param name="ProposedVariantId">Only for matched lines; never set for ambiguous ones.</param>
/// <param name="UnitMismatch">The message's unit differs from the proposed variant's unit (check the quantity).</param>
/// <param name="Selection">After approval: proposed, candidate, manual or skipped.</param>
public sealed record OrderParseLineResponse(
    int LineNo,
    string RawText,
    string? ProductCode,
    string ProductText,
    decimal? Quantity,
    string? Unit,
    string? BrandModel,
    decimal ParseConfidence,
    string? Notes,
    string MatchStatus,
    decimal MatchConfidence,
    Guid? ProposedVariantId,
    bool UnitMismatch,
    IReadOnlyList<OrderParseCandidateResponse> Candidates,
    Guid? SelectedVariantId,
    decimal? SelectedQuantity,
    string? Selection);

public sealed record OrderParseSummary(int Lines, int Matched, int Ambiguous, int Unmatched, decimal OverallConfidence);

/// <param name="Status">needs_review, approved (draft order created) or rejected.</param>
/// <param name="Parser">ai or rules.</param>
/// <param name="FallbackReason">Why the rules parser ran although AI is enabled: quota_exceeded, provider_error, timeout, refused, truncated, invalid_output; disabled when AI is off.</param>
/// <param name="MessageText">The pasted text; null after the retention period (<see cref="TextPurged"/>).</param>
/// <param name="Warnings">Codes: parser codes (price_mentioned, instruction_ignored, …) and server codes (ai_disabled, ai_quota_exceeded, ai_unavailable, line_not_in_message, …).</param>
public sealed record OrderParseResponse(
    Guid Id,
    string Status,
    string Channel,
    Guid? CustomerId,
    string Parser,
    string? Model,
    string PromptVersion,
    string? FallbackReason,
    string? MessageText,
    bool TextPurged,
    IReadOnlyList<OrderParseLineResponse> Lines,
    IReadOnlyList<string> UnparsedFragments,
    IReadOnlyList<string> Warnings,
    OrderParseSummary Summary,
    Guid? OrderId,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedBy,
    long Version);

/// <summary>List item: no message text.</summary>
public sealed record OrderParseListItem(
    Guid Id,
    string Status,
    string Parser,
    string? FallbackReason,
    OrderParseSummary Summary,
    Guid? CustomerId,
    Guid? OrderId,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy);

public sealed record OrderParseListResponse(IReadOnlyList<OrderParseListItem> Items);

/// <summary>Today's AI usage of the tenant (Europe/Istanbul day) against its limits.</summary>
/// <param name="AiEnabled">AI parsing is switched on and configured; false = every parse uses the rules parser.</param>
/// <param name="QuotaTokens">Tokens the daily quota counts: uncached input + cache writes + output.</param>
public sealed record AiUsageResponse(
    DateOnly Day,
    bool AiEnabled,
    string Model,
    int ParseRequests,
    int HourlyParseLimit,
    int AiCalls,
    int DailyCallLimit,
    long QuotaTokens,
    long DailyTokenLimit,
    int AiFailures,
    int Fallbacks,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);

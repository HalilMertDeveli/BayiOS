namespace BayiOS.Modules.Integrations.Contracts;

// ---- Requests ----

/// <param name="Text">The pasted WhatsApp message (limits: <c>GET /ai/settings</c> → limits; 1–4000 characters and at most 150 lines by default).</param>
/// <param name="CustomerId">Optional customer the order is for; used as the default on approval, never sent to the AI provider.</param>
/// <param name="WarehouseId">Optional warehouse for the draft; used as the default on approval (validated by Orders then).</param>
public sealed record SubmitOrderParseRequest(string? Text, Guid? CustomerId, Guid? WarehouseId = null);

/// <summary>
/// Creates the draft order from the reviewed lines. Every line of the parse must be listed exactly once: included
/// lines name their variant explicitly (also for matched lines — the proposal is not taken implicitly), skipped lines
/// are listed with <c>include = false</c>.
/// </summary>
/// <param name="CustomerId">Null = the customer given on submit (one of the two is required).</param>
/// <param name="WarehouseId">Null = the warehouse given on submit, else the tenant's default warehouse.</param>
/// <param name="Notes">Order notes typed by the reviewer (the message text is not copied into the order).</param>
public sealed record ApproveOrderParseRequest(Guid? CustomerId, Guid? WarehouseId, string? Notes, IReadOnlyList<ApproveOrderParseLine>? Lines);

/// <param name="LineNo">The review line number.</param>
/// <param name="Include">true = add to the draft; false = skip the line.</param>
/// <param name="VariantId">Required when included: a candidate of the line or any active variant of the tenant's catalog.</param>
/// <param name="Quantity">Null = the parsed quantity (required when the message had none). In the variant's unit.</param>
/// <param name="Unit">
/// Optional confirmation of the unit the quantity is in: when sent it must be the chosen variant's unit code (orders
/// are always in the variant's unit; no conversion is applied) — 422 otherwise, so a "koli" read from the message is
/// never silently ordered as "adet".
/// </param>
/// <param name="LearnAlias">
/// Null/true = remember this line's phrase → variant for this tenant (WAI-07, learned alias; also for the parse's
/// customer when it has one); false = do not learn from this line.
/// </param>
public sealed record ApproveOrderParseLine(int? LineNo, bool? Include, Guid? VariantId, decimal? Quantity, string? Unit = null, bool? LearnAlias = null);

/// <param name="AiParsingEnabled">Opt in (true) or out (false) of AI parsing for the tenant (data-policy §7).</param>
public sealed record UpdateAiSettingsRequest(bool? AiParsingEnabled);

// ---- Responses ----

/// <summary>A catalog candidate for a line. Never carries a price or a stock level.</summary>
/// <param name="Method">sku, barcode, alias (learned from an earlier confirmation), name or similarity.</param>
public sealed record OrderParseCandidateResponse(Guid VariantId, Guid ProductId, string Sku, string Name, string Unit, string Method, decimal Score);

/// <param name="RawText">The part of the message the line was read from.</param>
/// <param name="TextStart">
/// Offset (UTF-16 code units, 0-based) of <paramref name="RawText"/> in <c>messageText</c>; null when it cannot be
/// located exactly (e.g. the parser normalised spacing across lines).
/// </param>
/// <param name="TextLength">Length of that span in <c>messageText</c> (null with <paramref name="TextStart"/>).</param>
/// <param name="ParseConfidence">The parser's certainty about reading the line (0–1).</param>
/// <param name="MatchStatus">matched (one proposal, still to be confirmed), ambiguous (choose a candidate) or unmatched.</param>
/// <param name="ProposedVariantId">Only for matched lines; never set for ambiguous ones.</param>
/// <param name="UnitMismatch">The message's unit differs from the proposed variant's unit (check the quantity).</param>
/// <param name="Selection">After approval: proposed, candidate, manual or skipped.</param>
/// <param name="SelectedUnit">After approval: the unit code of the chosen variant (the unit the quantity is in).</param>
public sealed record OrderParseLineResponse(
    int LineNo,
    string RawText,
    int? TextStart,
    int? TextLength,
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
    string? Selection,
    string? SelectedUnit = null);

public sealed record OrderParseSummary(int Lines, int Matched, int Ambiguous, int Unmatched, decimal OverallConfidence);

/// <param name="Status">pending (being parsed), failed, needs_review, approved (draft order created) or rejected.</param>
/// <param name="Parser">ai or rules; null while pending or when failed.</param>
/// <param name="FallbackReason">Why the rules parser ran although AI is enabled: quota_exceeded, provider_error, timeout, refused, truncated, invalid_output; disabled when AI is off.</param>
/// <param name="FailureCode">When failed: ai.parse_failed, ai.parse_timed_out or ai.opt_in_revoked.</param>
/// <param name="MessageText">The pasted text (masked); null after the retention period (<see cref="TextPurged"/>).</param>
/// <param name="Warnings">Codes: parser codes (price_mentioned, instruction_ignored, …) and server codes (ai_disabled, ai_quota_exceeded, ai_unavailable, line_not_in_message, personal_data_masked, …).</param>
public sealed record OrderParseResponse(
    Guid Id,
    string Status,
    string Channel,
    Guid? CustomerId,
    Guid? WarehouseId,
    string? Parser,
    string? Model,
    string? PromptVersion,
    string? FallbackReason,
    string? FailureCode,
    string? MessageText,
    bool TextPurged,
    IReadOnlyList<OrderParseLineResponse> Lines,
    IReadOnlyList<string> UnparsedFragments,
    IReadOnlyList<string> Warnings,
    OrderParseSummary Summary,
    Guid? OrderId,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedBy,
    long Version);

/// <summary>202 of a submit: the message was accepted and is parsed in the background (ADR 0010 §2.1).</summary>
/// <param name="StatusUrl">Poll this (also the <c>Location</c> header) until the state is succeeded or failed.</param>
/// <param name="ResultUrl">The parse (review) itself.</param>
/// <param name="PollAfterMs">Suggested first polling delay; back off from there.</param>
public sealed record OrderParseAcceptedResponse(Guid Id, string Status, string StatusUrl, string ResultUrl, int PollAfterMs);

/// <summary>Processing state of a parse (the cheap polling target).</summary>
/// <param name="State">pending (queued or running), succeeded (the review is ready: <paramref name="ResultUrl"/>) or failed.</param>
/// <param name="ParseStatus">The parse's own status (pending, failed, needs_review, approved, rejected).</param>
/// <param name="FailureCode">When failed: ai.parse_failed, ai.parse_timed_out or ai.opt_in_revoked.</param>
/// <param name="PollAfterMs">While pending: suggested delay before the next poll.</param>
public sealed record OrderParseStatusResponse(
    Guid Id,
    string State,
    string ParseStatus,
    string? FailureCode,
    string ResultUrl,
    int? PollAfterMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>List item: no message text.</summary>
public sealed record OrderParseListItem(
    Guid Id,
    string Status,
    string? Parser,
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

/// <summary>What a message may be (validated on submit with 422).</summary>
/// <param name="MaxInputChars">Longest message, in characters after trimming.</param>
/// <param name="MaxInputLines">Most lines a message may have.</param>
/// <param name="HourlyParseLimit">Messages the tenant may submit per clock hour (429 above).</param>
public sealed record AiInputLimits(int MaxInputChars, int MaxInputLines, int HourlyParseLimit);

/// <summary>The tenant's AI settings (data-policy §7) and the input limits.</summary>
/// <param name="AiParsingEnabled">The tenant opted in. Off (the default) = submitting a message is refused (409 <c>ai.opt_in_required</c>).</param>
/// <param name="ProviderConfigured">The server has the AI provider switched on and configured; false = opted-in tenants get the deterministic rules parser.</param>
public sealed record AiSettingsResponse(
    bool AiParsingEnabled,
    bool ProviderConfigured,
    AiInputLimits Limits,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy,
    long Version);

/// <summary>A learned alias (WAI-07): a phrase a reviewer confirmed for a variant.</summary>
/// <param name="Phrase">Normalised phrase (lower case, Turkish letters folded, stop words removed).</param>
/// <param name="CustomerId">Only for this customer's messages; null = the whole tenant.</param>
/// <param name="Confirmations">How often reviewers confirmed it (reset when the variant changed).</param>
public sealed record LearnedAliasResponse(Guid Id, string Phrase, Guid VariantId, Guid? CustomerId, int Confirmations, DateTimeOffset UpdatedAt);

public sealed record LearnedAliasListResponse(IReadOnlyList<LearnedAliasResponse> Items);

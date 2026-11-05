using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.Modules.Integrations.Domain;
using BayiOS.Modules.Integrations.Infrastructure.Persistence;
using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Integrations.Parsing;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>The AI parser when it is enabled and has an API key; null otherwise (no provider call is possible then).</summary>
internal sealed class AiParserAccessor(IServiceProvider services, IOptions<AiOptions> options, IConfiguration configuration)
{
    public bool IsConfigured => options.Value.Enabled && !string.IsNullOrWhiteSpace(configuration[AiOptions.ApiKeyKey]);

    public IOrderTextParser? Parser => IsConfigured ? services.GetRequiredService<AnthropicOrderParser>() : null;
}

/// <summary>Stored review document (jsonb <c>review</c>): the response lines plus the unparsed fragments.</summary>
internal sealed record StoredReview(IReadOnlyList<OrderParseLineResponse> Lines, IReadOnlyList<string> UnparsedFragments);

/// <summary>
/// WhatsApp order assistant (#47, #48, #50; ADR 0010). Submit: validate size → hourly limit (429) → mask personal data
/// → AI parser when enabled, configured and under the daily quota, else (or on any provider failure) the rules
/// parser → ground + match against the tenant's catalog → store for review. Approve: the reviewer confirms a variant
/// for every included line; the draft is created through the Orders create command with source <c>whatsapp_ai</c>
/// (prices from Pricing; the order is never auto-approved). Nothing the parser outputs can set a price, a stock level,
/// a catalog id or an approval.
/// </summary>
internal sealed partial class OrderParseService(
    IntegrationsDbContext db,
    IRequestContext requestContext,
    IAuthorizationContext authorization,
    IAssistantCatalog catalog,
    IAssistantOrderDrafts drafts,
    AiUsageMeter meter,
    IEntitlements entitlements,
    AiParserAccessor ai,
    IOptions<AiOptions> options,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<OrderParseService> logger)
{
    public const int MaxListLimit = 100;
    private const int MaxNotes = 2000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No user resolved.");

    // ---------------------------------------------------------------- submit

    public async Task<AppResult<OrderParseResponse>> SubmitAsync(SubmitOrderParseRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var text = Normalise(request.Text);
        if (text.Length == 0)
        {
            return AppError.Validation("text", "Required.");
        }

        if (text.Length > settings.MaxInputChars)
        {
            return AppError.Validation("text", $"At most {settings.MaxInputChars} characters.");
        }

        if (text.Count(c => c == '\n') + 1 > settings.MaxInputLines)
        {
            return AppError.Validation("text", $"At most {settings.MaxInputLines} lines.");
        }

        // The endpoint gate already required ai_order_parsing; the add-on's monthly quota is checked with the hourly limit.
        var monthlyLimit = (await entitlements.CurrentAsync(cancellationToken)).LimitOf(EntitlementCodes.AiParsesPerMonth);
        var start = await meter.TryStartParseAsync(TenantId, UserId, settings.HourlyParseLimit, monthlyLimit, cancellationToken);
        if (start.Decision == ParseStartDecision.MonthlyLimitReached)
        {
            throw entitlements.LimitExceeded(EntitlementCodes.AiParsesPerMonth, monthlyLimit ?? 0, start.MonthlyUsed ?? 0);
        }

        if (start.Decision != ParseStartDecision.Allowed)
        {
            return new AppError(
                429, AssistantProblems.RateLimited, $"At most {settings.HourlyParseLimit} messages per hour can be parsed; try again later",
                RetryAfterSeconds: meter.SecondsUntilNextHour());
        }

        var started = Stopwatch.GetTimestamp();
        var masked = TurkishText.MaskPersonalData(text, out var maskedAny);
        var serverWarnings = new List<string>();
        if (maskedAny)
        {
            serverWarnings.Add(ParseWarnings.PersonalDataMasked);
        }

        var outcome = await ParseAsync(masked, serverWarnings, cancellationToken);
        var parsedText = outcome.Parser == ParserKinds.Ai ? AnthropicOrderParser.Neutralise(masked) : masked;
        var matcher = await MatcherAsync(outcome.Parse, serverWarnings, cancellationToken);
        var review = OrderParseReview.Build(outcome.Parse, parsedText, matcher, outcome.Parser == ParserKinds.Ai, serverWarnings);

        var stored = new StoredReview([.. review.Lines.Select(ToResponse)], review.UnparsedFragments);
        var counts = (review.Lines.Count, review.Count(MatchStatuses.Matched), review.Count(MatchStatuses.Ambiguous), review.Count(MatchStatuses.Unmatched));
        var usage = outcome.Usage;
        var entity = OrderParseRequest.Create(
            TenantId,
            request.CustomerId,
            masked,
            Sha256(masked),
            outcome.Parser,
            outcome.Model,
            outcome.PromptVersion,
            outcome.FallbackReason,
            JsonSerializer.Serialize(outcome.Parse, Json),
            JsonSerializer.Serialize(stored, Json),
            JsonSerializer.Serialize(review.Warnings, Json),
            counts,
            review.OverallConfidence,
            (usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens),
            (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds),
            UserId,
            clock.GetUtcNow());
        db.ParseRequests.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.ParseRequested, AssistantAuditActions.EntityType, entity.Id, new AuditChanges()
                .Value("parser", entity.Parser)
                .Value("model", entity.Model)
                .Value("promptVersion", entity.PromptVersion)
                .Value("fallbackReason", entity.FallbackReason)
                .Value("lines", entity.LineCount)
                .Value("matched", entity.MatchedCount)
                .Value("ambiguous", entity.AmbiguousCount)
                .Value("unmatched", entity.UnmatchedCount)
                .Value("warnings", review.Warnings)
                .Value("inputTokens", entity.InputTokens)
                .Value("outputTokens", entity.OutputTokens)
                .ToDictionary()),
            cancellationToken);
        return ToResponse(entity);
    }

    private sealed record ParseOutcome(
        ParsedOrder Parse, string Parser, string? Model, string PromptVersion, string? FallbackReason, AiTokenUsage Usage);

    private async Task<ParseOutcome> ParseAsync(string masked, List<string> serverWarnings, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var parser = ai.Parser;
        var usage = AiTokenUsage.None;
        string reason;
        if (parser is null)
        {
            reason = FallbackReasons.Disabled;
            serverWarnings.Add(ParseWarnings.AiDisabled);
        }
        else if (await meter.TryReserveAiCallAsync(TenantId, UserId, settings.DailyCallLimit, settings.DailyTokenLimit, cancellationToken)
                 is not AiQuotaDecision.Allowed)
        {
            reason = FallbackReasons.QuotaExceeded;
            serverWarnings.Add(ParseWarnings.AiQuotaExceeded);
        }
        else
        {
            var result = await parser.ParseAsync(masked, cancellationToken);
            usage = result.Usage;

            // Not tied to the request token: a call that was made is always counted.
            await meter.RecordAiCallAsync(TenantId, UserId, usage, failed: result.Parse is null, CancellationToken.None);
            if (result.Parse is not null)
            {
                return new ParseOutcome(result.Parse, ParserKinds.Ai, result.Model, result.PromptVersion, null, usage);
            }

            reason = result.FailureReason ?? FallbackReasons.ProviderError;
            serverWarnings.Add(ParseWarnings.AiUnavailable);
        }

        if (parser is not null)
        {
            await meter.RecordFallbackAsync(TenantId, UserId, CancellationToken.None);
            LogFallback(logger, TenantId, reason);
        }

        return new ParseOutcome(RuleBasedOrderParser.Parse(masked), ParserKinds.Rules, null, RuleBasedOrderParser.Version, reason, usage);
    }

    private async Task<CatalogMatcher> MatcherAsync(ParsedOrder parse, List<string> serverWarnings, CancellationToken cancellationToken)
    {
        var snapshot = await catalog.LoadActiveVariantsAsync(options.Value.MatchCatalogLimit, cancellationToken);
        if (!snapshot.Truncated)
        {
            return new CatalogMatcher(snapshot.Entries);
        }

        // Large catalog: fuzzy matching sees the first N variants, exact codes are looked up in the whole catalog.
        serverWarnings.Add(ParseWarnings.CatalogTruncated);
        var codes = parse.Lines
            .SelectMany(l => new[] { l.ProductCode }.Concat(l.RawText.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .OfType<string>()
            .Select(CatalogMatcher.NormaliseCode)
            .Where(c => c.Length >= 3 && c.Any(char.IsAsciiDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(500)
            .ToList();
        var extra = codes.Count == 0 ? [] : await catalog.FindByCodesAsync(codes, cancellationToken);
        return new CatalogMatcher(snapshot.Entries.Concat(extra).DistinctBy(e => e.VariantId));
    }

    // ---------------------------------------------------------------- reads

    public async Task<OrderParseResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.ParseRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is { } entity ? ToResponse(entity) : null;

    public async Task<OrderParseListResponse> ListAsync(string? status, int limit, CancellationToken cancellationToken)
    {
        var query = db.ParseRequests.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        var items = await query
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(Math.Clamp(limit, 1, MaxListLimit))
            .Select(r => new OrderParseListItem(
                r.Id, r.Status, r.Parser, r.FallbackReason,
                new OrderParseSummary(r.LineCount, r.MatchedCount, r.AmbiguousCount, r.UnmatchedCount, r.OverallConfidence),
                r.CustomerId, r.OrderId, r.CreatedAt, r.CreatedBy))
            .ToListAsync(cancellationToken);
        return new OrderParseListResponse(items);
    }

    public async Task<AiUsageResponse> UsageAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var today = await meter.TodayAsync(TenantId, UserId, cancellationToken);
        return new AiUsageResponse(
            today.Day, ai.IsConfigured, settings.Model, today.ParseRequests, settings.HourlyParseLimit, today.AiCalls, settings.DailyCallLimit,
            today.QuotaTokens, settings.DailyTokenLimit, today.AiFailures, today.Fallbacks, today.InputTokens, today.OutputTokens,
            today.CacheReadTokens, today.CacheWriteTokens);
    }

    // ---------------------------------------------------------------- approve / reject

    public async Task<AppResult<OrderParseResponse>> ApproveAsync(
        Guid id, ApproveOrderParseRequest request, long? expectedVersion, string? correlationId, CancellationToken cancellationToken)
    {
        // The draft is an order: whatsapp.parse opens the endpoint, orders.create is needed as well (data-model §3).
        if (await authorization.ScopeAsync(Permissions.OrdersCreate, cancellationToken) != PermissionScope.All)
        {
            return AppError.Forbidden(Permissions.OrdersCreate);
        }

        var (entity, error) = await LockReviewableAsync(id, expectedVersion, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var stored = JsonSerializer.Deserialize<StoredReview>(entity!.Review!, Json)
            ?? throw new InvalidOperationException($"Parse {id} has an empty review.");
        var errors = new Dictionary<string, string[]>();
        var customerId = request.CustomerId ?? entity.CustomerId;
        if (customerId is null)
        {
            errors["customerId"] = ["Required (not given on submit either)."];
        }

        if (request.Notes is { Length: > MaxNotes })
        {
            errors["notes"] = [$"At most {MaxNotes} characters."];
        }

        var choices = ReadChoices(stored, request.Lines, errors);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var included = choices.Where(c => c.Include).ToList();
        var result = await drafts.CreateDraftAsync(
            new AssistantDraftOrder(
                customerId!.Value,
                request.WarehouseId,
                entity.Id,
                string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                [.. included.Select(c => new AssistantDraftLine(c.VariantId!.Value, c.Quantity!.Value))]),
            correlationId,
            cancellationToken);
        if (!result.Succeeded)
        {
            return new AppError(result.Status, result.Code ?? "validation.failed", result.Title ?? "The draft order could not be created", result.Errors);
        }

        var lines = stored.Lines.Select(line =>
        {
            var choice = choices.Single(c => c.LineNo == line.LineNo);
            return line with
            {
                SelectedVariantId = choice.Include ? choice.VariantId : null,
                SelectedQuantity = choice.Include ? choice.Quantity : null,
                Selection = choice.Selection,
            };
        }).ToList();

        var now = clock.GetUtcNow();
        entity.Review = JsonSerializer.Serialize(stored with { Lines = lines }, Json);
        entity.Status = OrderParseStatuses.Approved;
        entity.OrderId = result.OrderId;
        entity.ReviewedBy = UserId;
        entity.ReviewedAt = now;
        entity.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.DraftCreated, AssistantAuditActions.EntityType, entity.Id, new AuditChanges()
                .Value("orderId", result.OrderId)
                .Value("orderNumber", result.OrderNumber)
                .Value("included", included.Count)
                .Value("proposed", choices.Count(c => c.Selection == Selections.Proposed))
                .Value("candidate", choices.Count(c => c.Selection == Selections.Candidate))
                .Value("manual", choices.Count(c => c.Selection == Selections.Manual))
                .Value("skipped", choices.Count(c => c.Selection == Selections.Skipped))
                .Value("parser", entity.Parser)
                .ToDictionary()),
            cancellationToken);
        return ToResponse(entity);
    }

    public async Task<AppResult<OrderParseResponse>> RejectAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var (entity, error) = await LockReviewableAsync(id, expectedVersion, cancellationToken, allowPurged: true);
        if (error is not null)
        {
            return error;
        }

        var now = clock.GetUtcNow();
        entity!.Status = OrderParseStatuses.Rejected;
        entity.ReviewedBy = UserId;
        entity.ReviewedAt = now;
        entity.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.DraftRejected, AssistantAuditActions.EntityType, entity.Id, new AuditChanges()
                .Value("lines", entity.LineCount)
                .Value("parser", entity.Parser)
                .ToDictionary()),
            cancellationToken);
        return ToResponse(entity);
    }

    private async Task<(OrderParseRequest? Entity, AppError? Error)> LockReviewableAsync(
        Guid id, long? expectedVersion, CancellationToken cancellationToken, bool allowPurged = false)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var tenantId = TenantId;
        var entity = (await db.ParseRequests
                .FromSql($"SELECT * FROM integrations.ai_parse_requests WHERE id = {id} AND tenant_id = {tenantId} FOR UPDATE")
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
        if (entity is null)
        {
            return (null, AppError.NotFound());
        }

        if (expectedVersion is { } version && version != entity.RowVersion)
        {
            return (null, AppError.VersionMismatch());
        }

        if (entity.Status != OrderParseStatuses.NeedsReview)
        {
            return (null, new AppError(409, AssistantProblems.NotReviewable, $"The parse is already {entity.Status}"));
        }

        if (!allowPurged && entity.Review is null)
        {
            return (null, new AppError(409, AssistantProblems.TextPurged, "The message text was removed after the retention period; parse the message again"));
        }

        return (entity, null);
    }

    // ---------------------------------------------------------------- line choices

    private static class Selections
    {
        public const string Proposed = "proposed";
        public const string Candidate = "candidate";
        public const string Manual = "manual";
        public const string Skipped = "skipped";
    }

    private sealed record Choice(int LineNo, bool Include, Guid? VariantId, decimal? Quantity, string Selection);

    /// <summary>
    /// Every review line exactly once; an included line names its variant explicitly (no implicit acceptance of a
    /// proposal, no automatic pick for ambiguous lines) and has a positive quantity (given, else the parsed one).
    /// </summary>
    private static List<Choice> ReadChoices(StoredReview stored, IReadOnlyList<ApproveOrderParseLine>? input, Dictionary<string, string[]> errors)
    {
        var choices = new List<Choice>();
        if (input is null || input.Count == 0)
        {
            errors["lines"] = ["List every review line once (include with a variant, or include = false)."];
            return choices;
        }

        var byNumber = stored.Lines.ToDictionary(l => l.LineNo);
        var seen = new HashSet<int>();
        for (var i = 0; i < input.Count; i++)
        {
            var item = input[i];
            var key = $"lines[{i}]";
            if (item.LineNo is not { } lineNo || !byNumber.TryGetValue(lineNo, out var line))
            {
                errors[$"{key}.lineNo"] = ["Unknown review line."];
                continue;
            }

            if (!seen.Add(lineNo))
            {
                errors[$"{key}.lineNo"] = ["Listed more than once."];
                continue;
            }

            if (item.Include is not { } include)
            {
                errors[$"{key}.include"] = ["Required (true or false)."];
                continue;
            }

            if (!include)
            {
                choices.Add(new Choice(lineNo, false, null, null, Selections.Skipped));
                continue;
            }

            if (item.VariantId is not { } variantId || variantId == Guid.Empty)
            {
                errors[$"{key}.variantId"] = ["Choose the product variant for this line."];
                continue;
            }

            var quantity = item.Quantity ?? line.Quantity;
            if (quantity is not { } q || q <= 0 || q > ParsedOrderValidator.MaxQuantity || decimal.Round(q, 3) != q)
            {
                errors[$"{key}.quantity"] = ["Required: a positive quantity with at most 3 decimals."];
                continue;
            }

            var selection = variantId == line.ProposedVariantId ? Selections.Proposed
                : line.Candidates.Any(c => c.VariantId == variantId) ? Selections.Candidate
                : Selections.Manual;
            choices.Add(new Choice(lineNo, true, variantId, q, selection));
        }

        var missing = stored.Lines.Select(l => l.LineNo).Where(n => !seen.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            errors["lines"] = [$"Every review line must be listed; missing: {string.Join(", ", missing)}."];
        }
        else if (errors.Count == 0 && choices.All(c => !c.Include))
        {
            errors["lines"] = ["Include at least one line (or reject the parse)."];
        }

        return choices;
    }

    // ---------------------------------------------------------------- mapping

    private static OrderParseLineResponse ToResponse(ReviewLine line) =>
        new(
            line.LineNo,
            line.Parsed.RawText,
            line.Parsed.ProductCode,
            line.Parsed.ProductText,
            line.Parsed.Quantity,
            line.Parsed.Unit,
            line.Parsed.BrandModel,
            line.Parsed.Confidence,
            line.Parsed.Notes,
            line.Match.Status,
            line.Match.Confidence,
            line.Match.ProposedVariantId,
            line.Match.UnitMismatch,
            [.. line.Match.Candidates.Select(c => new OrderParseCandidateResponse(c.VariantId, c.ProductId, c.Sku, c.Name, c.Unit, c.Method, c.Score))],
            null,
            null,
            null);

    private static OrderParseResponse ToResponse(OrderParseRequest entity)
    {
        var stored = entity.Review is null ? null : JsonSerializer.Deserialize<StoredReview>(entity.Review, Json);
        var warnings = JsonSerializer.Deserialize<List<string>>(entity.Warnings, Json) ?? [];
        return new OrderParseResponse(
            entity.Id,
            entity.Status,
            entity.Channel,
            entity.CustomerId,
            entity.Parser,
            entity.Model,
            entity.PromptVersion,
            entity.FallbackReason,
            entity.RawText,
            entity.TextPurgedAt is not null,
            stored?.Lines ?? [],
            stored?.UnparsedFragments ?? [],
            warnings,
            new OrderParseSummary(entity.LineCount, entity.MatchedCount, entity.AmbiguousCount, entity.UnmatchedCount, entity.OverallConfidence),
            entity.OrderId,
            entity.CreatedAt,
            entity.CreatedBy,
            entity.ReviewedAt,
            entity.ReviewedBy,
            entity.RowVersion);
    }

    /// <summary>Line breaks normalised, control characters (other than line breaks and tabs) removed, trimmed.</summary>
    internal static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var unified = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new string([.. unified.Where(c => c is '\n' or '\t' || !char.IsControl(c))]).Trim();
    }

    private static string Sha256(string text)
    {
        var normalised = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI order parse for tenant {TenantId} used the rules parser ({Reason})")]
    private static partial void LogFallback(ILogger logger, Guid tenantId, string reason);
}

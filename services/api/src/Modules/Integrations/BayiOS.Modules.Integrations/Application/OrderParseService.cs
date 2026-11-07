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
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>The AI parser when it is enabled and has an API key; null otherwise (no provider call is possible then).</summary>
internal sealed class AiParserAccessor(IServiceProvider services, IOptions<AiOptions> options, IConfiguration configuration)
{
    public bool IsConfigured => options.Value.Enabled && !string.IsNullOrWhiteSpace(configuration[AiOptions.ApiKeyKey]);

    public IOrderTextParser? Parser => IsConfigured ? services.GetRequiredService<AnthropicOrderParser>() : null;
}

/// <summary>Stored review document (jsonb <c>review</c>): the response lines plus the unparsed fragments.</summary>
internal sealed record StoredReview(IReadOnlyList<OrderParseLineResponse> Lines, IReadOnlyList<string> UnparsedFragments);

/// <summary>What the background parse needs from the pending row (read in phase 1, ADR 0010 §2.1).</summary>
internal sealed record PendingParse(Guid Id, Guid TenantId, string MaskedText, Guid? CustomerId, Guid? CreatedBy, IReadOnlyList<string> Warnings);

/// <summary>Result of the parser step (AI or rules) of a background parse.</summary>
internal sealed record ParseOutcome(
    ParsedOrder Parse, string Parser, string? Model, string PromptVersion, string? FallbackReason, AiTokenUsage Usage);

/// <summary>
/// WhatsApp order assistant (#47, #48, #50, #121; ADR 0010).
/// <para>
/// Submit (request): validate size → tenant opt-in (409) → hourly/monthly limit (429/403) → mask personal data → store
/// a <c>pending</c> parse and enqueue <see cref="AiOrderParseJob"/> in the same transaction → 202. The provider is never
/// called inside a request.
/// </para>
/// <para>
/// Background (<see cref="AiOrderParseJob"/>): <see cref="BeginJobAsync"/> (short transaction) → parser call with no
/// connection held → <see cref="CompleteJobAsync"/> (short transaction: ground + match against the tenant's catalog
/// and learned aliases, store the review). Approve: the reviewer confirms a variant for every included line; the draft
/// is created through the Orders create command with source <c>whatsapp_ai</c> (prices from Pricing; the order is never
/// auto-approved) and confirmed phrases are learned as aliases. Nothing the parser outputs can set a price, a stock
/// level, a catalog id or an approval.
/// </para>
/// </summary>
internal sealed class OrderParseService(
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
    IBackgroundJobs jobs,
    TimeProvider clock)
{
    public const int MaxListLimit = 100;
    public const int MaxAliasPhrase = 200;
    private const int MaxNotes = 2000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No user resolved.");

    public static string StatusUrl(Guid id) => $"/api/v1/ai/order-parses/{id}/status";

    public static string ResultUrl(Guid id) => $"/api/v1/ai/order-parses/{id}";

    // ---------------------------------------------------------------- submit (request)

    public async Task<AppResult<OrderParseAcceptedResponse>> SubmitAsync(SubmitOrderParseRequest request, CancellationToken cancellationToken)
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

        // data-policy §7: the message may leave Türkiye only when the tenant opted in. Checked before anything is counted.
        if (!await IsOptedInAsync(cancellationToken))
        {
            return new AppError(
                409, AssistantProblems.OptInRequired, "AI order parsing is switched off for this business; an owner or admin can switch it on");
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

        // Masked before it is stored: the raw text never reaches the database, the queue or the provider.
        var masked = TurkishText.MaskPersonalData(text, out var maskedAny);
        string[] warnings = maskedAny ? [ParseWarnings.PersonalDataMasked] : [];
        var entity = OrderParseRequest.CreatePending(
            TenantId, request.CustomerId, request.WarehouseId, masked, Sha256(masked), JsonSerializer.Serialize(warnings, Json), UserId, clock.GetUtcNow());
        db.ParseRequests.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        // Same transaction: the job exists only if the parse does (and vice versa). Payload = ids only.
        await jobs.EnqueueAsync(
            new JobRequest(AiOrderParseJob.Type, new AiOrderParsePayload(entity.Id), MaxAttempts: AiOrderParseJob.MaxAttempts, DedupeKey: entity.Id.ToString("N")),
            cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.ParseRequested, AssistantAuditActions.EntityType, entity.Id, new AuditChanges()
                .Value("textLength", entity.TextLength)
                .Value("customerGiven", entity.CustomerId is not null)
                .Value("warehouseGiven", entity.WarehouseId is not null)
                .Value("personalDataMasked", maskedAny)
                .ToDictionary()),
            cancellationToken);
        return new OrderParseAcceptedResponse(entity.Id, entity.Status, StatusUrl(entity.Id), ResultUrl(entity.Id), settings.PollAfterMs);
    }

    // ---------------------------------------------------------------- background job (phases 1 and 3)

    /// <summary>
    /// Phase 1 of <see cref="AiOrderParseJob"/>: the pending row and what the parser needs. Null when there is nothing
    /// to do — the parse is gone, already processed (a retried or duplicate job), past the processing deadline or the
    /// tenant opted out in the meantime (the last two are recorded as failed; the text is then sent nowhere).
    /// </summary>
    public async Task<PendingParse?> BeginJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await LockAsync(id, cancellationToken);
        if (entity is null || entity.Status != OrderParseStatuses.Pending)
        {
            return null;
        }

        string? failure = null;
        if (clock.GetUtcNow() - entity.CreatedAt > TimeSpan.FromSeconds(options.Value.ProcessingDeadlineSeconds))
        {
            failure = ParseFailureCodes.TimedOut;
        }
        else if (!await IsOptedInAsync(cancellationToken))
        {
            failure = ParseFailureCodes.OptInRevoked;
        }
        else if (entity.RawText is null)
        {
            failure = ParseFailureCodes.ProcessingFailed; // purged before it was ever processed
        }

        if (failure is not null)
        {
            await FailAsync(entity, failure, cancellationToken);
            return null;
        }

        var warnings = JsonSerializer.Deserialize<List<string>>(entity.Warnings, Json) ?? [];
        return new PendingParse(entity.Id, entity.TenantId, entity.RawText!, entity.CustomerId, entity.CreatedBy, warnings);
    }

    /// <summary>
    /// Phase 3 of <see cref="AiOrderParseJob"/>: ground and match the parse against the tenant's catalog and learned
    /// aliases and store the review (pending → needs_review). A no-op when the parse is no longer pending.
    /// </summary>
    public async Task<bool> CompleteJobAsync(PendingParse pending, ParseOutcome outcome, IReadOnlyCollection<string> serverWarnings, int durationMs, CancellationToken cancellationToken)
    {
        var entity = await LockAsync(pending.Id, cancellationToken);
        if (entity is null || entity.Status != OrderParseStatuses.Pending)
        {
            return false;
        }

        var warnings = new List<string>(serverWarnings);
        var parsedText = outcome.Parser == ParserKinds.Ai ? AnthropicOrderParser.Neutralise(pending.MaskedText) : pending.MaskedText;
        var matcher = await MatcherAsync(outcome.Parse, pending.CustomerId, warnings, cancellationToken);
        var review = OrderParseReview.Build(outcome.Parse, parsedText, matcher, outcome.Parser == ParserKinds.Ai, warnings);

        // Character offsets into messageText (the neutralised text has the same length as the masked one).
        var cursor = 0;
        var lines = new List<OrderParseLineResponse>(review.Lines.Count);
        foreach (var line in review.Lines)
        {
            var span = OrderParseReview.Locate(parsedText, line.Parsed.RawText, cursor);
            if (span is { } found)
            {
                cursor = found.Start + found.Length;
            }

            lines.Add(ToResponse(line, span));
        }

        var stored = new StoredReview(lines, review.UnparsedFragments);
        var usage = outcome.Usage;
        entity.Complete(
            outcome.Parser,
            outcome.Model,
            outcome.PromptVersion,
            outcome.FallbackReason,
            JsonSerializer.Serialize(outcome.Parse, Json),
            JsonSerializer.Serialize(stored, Json),
            JsonSerializer.Serialize(review.Warnings, Json),
            (review.Lines.Count, review.Count(MatchStatuses.Matched), review.Count(MatchStatuses.Ambiguous), review.Count(MatchStatuses.Unmatched)),
            review.OverallConfidence,
            (usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens),
            durationMs,
            clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.ParseCompleted, AssistantAuditActions.EntityType, entity.Id, new AuditChanges()
                .Value("parser", entity.Parser)
                .Value("model", entity.Model)
                .Value("promptVersion", entity.PromptVersion)
                .Value("fallbackReason", entity.FallbackReason)
                .Value("lines", entity.LineCount)
                .Value("matched", entity.MatchedCount)
                .Value("ambiguous", entity.AmbiguousCount)
                .Value("unmatched", entity.UnmatchedCount)
                .Value("aliasMatches", review.Lines.Count(l => l.Match.Candidates is [{ Method: MatchMethods.Alias }]))
                .Value("warnings", review.Warnings)
                .Value("inputTokens", entity.InputTokens)
                .Value("outputTokens", entity.OutputTokens)
                .ToDictionary()),
            cancellationToken);
        return true;
    }

    /// <summary>The job gave up (last attempt failed): pending → failed, so pollers stop waiting.</summary>
    public async Task FailJobAsync(Guid id, string failureCode, CancellationToken cancellationToken)
    {
        var entity = await LockAsync(id, cancellationToken);
        if (entity is not null && entity.Status == OrderParseStatuses.Pending)
        {
            await FailAsync(entity, failureCode, cancellationToken);
        }
    }

    private async Task FailAsync(OrderParseRequest entity, string failureCode, CancellationToken cancellationToken)
    {
        entity.Fail(failureCode, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.ParseFailed, AssistantAuditActions.EntityType, entity.Id, new AuditChanges()
                .Value("failureCode", failureCode)
                .ToDictionary()),
            cancellationToken);
    }

    private async Task<OrderParseRequest?> LockAsync(Guid id, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var tenantId = TenantId;
        return (await db.ParseRequests
                .FromSql($"SELECT * FROM integrations.ai_parse_requests WHERE id = {id} AND tenant_id = {tenantId} FOR UPDATE")
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private async Task<CatalogMatcher> MatcherAsync(ParsedOrder parse, Guid? customerId, List<string> serverWarnings, CancellationToken cancellationToken)
    {
        var snapshot = await catalog.LoadActiveVariantsAsync(options.Value.MatchCatalogLimit, cancellationToken);
        var entries = snapshot.Entries.ToList();
        if (snapshot.Truncated)
        {
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
            if (codes.Count > 0)
            {
                entries.AddRange(await catalog.FindByCodesAsync(codes, cancellationToken));
            }
        }

        // Learned aliases of exactly these phrases (tenant-wide and the parse's customer); RLS + query filter = this tenant.
        var phrases = parse.Lines.Select(CatalogMatcher.AliasKey).Where(p => p.Length is > 0 and <= MaxAliasPhrase).Distinct(StringComparer.Ordinal).ToList();
        var aliases = phrases.Count == 0
            ? []
            : await db.Aliases.AsNoTracking()
                .Where(a => phrases.Contains(a.Phrase) && (a.CustomerId == null || a.CustomerId == customerId))
                .Select(a => new AliasEntry(a.Phrase, a.VariantId, a.CustomerId != null))
                .ToListAsync(cancellationToken);

        // An alias only counts while its variant is an active variant of this tenant's catalog.
        var known = entries.Select(e => e.VariantId).ToHashSet();
        var missing = aliases.Select(a => a.VariantId).Where(v => !known.Contains(v)).Distinct().ToList();
        if (missing.Count > 0)
        {
            entries.AddRange(await catalog.FindByIdsAsync(missing, cancellationToken));
        }

        return new CatalogMatcher(entries.DistinctBy(e => e.VariantId), aliases);
    }

    // ---------------------------------------------------------------- reads

    public async Task<OrderParseResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.ParseRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is { } entity ? ToResponse(entity) : null;

    public async Task<OrderParseStatusResponse?> StatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.ParseRequests.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.Id, r.Status, r.FailureCode, r.CreatedAt, r.CompletedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var settings = options.Value;
        if (OrderParseStatuses.IsParsed(row.Status))
        {
            return new OrderParseStatusResponse(row.Id, ParseStates.Succeeded, row.Status, null, ResultUrl(row.Id), null, row.CreatedAt, row.CompletedAt);
        }

        if (row.Status == OrderParseStatuses.Failed)
        {
            return new OrderParseStatusResponse(row.Id, ParseStates.Failed, row.Status, row.FailureCode, ResultUrl(row.Id), null, row.CreatedAt, row.CompletedAt);
        }

        // Pending past the deadline: the worker is down or the queue is stuck — report it instead of letting clients wait.
        return clock.GetUtcNow() - row.CreatedAt > TimeSpan.FromSeconds(settings.ProcessingDeadlineSeconds)
            ? new OrderParseStatusResponse(row.Id, ParseStates.Failed, row.Status, ParseFailureCodes.TimedOut, ResultUrl(row.Id), null, row.CreatedAt, null)
            : new OrderParseStatusResponse(row.Id, ParseStates.Pending, row.Status, null, ResultUrl(row.Id), settings.PollAfterMs, row.CreatedAt, null);
    }

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

    // ---------------------------------------------------------------- tenant settings (opt-in, data-policy §7)

    private async Task<bool> IsOptedInAsync(CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking().Where(s => s.TenantId == TenantId).Select(s => s.AiParsingEnabled).SingleOrDefaultAsync(cancellationToken);

    public async Task<AiSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == TenantId, cancellationToken);

        // No row = the default (off) at version 1: the row an update creates starts there, so If-Match stays valid.
        return row is null ? Settings(false, null, null, 1) : Settings(row.AiParsingEnabled, row.UpdatedAt, row.UpdatedBy, row.RowVersion);
    }

    public async Task<AppResult<AiSettingsResponse>> UpdateSettingsAsync(UpdateAiSettingsRequest request, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (request.AiParsingEnabled is not { } enabled)
        {
            return AppError.Validation("aiParsingEnabled", "Required (true or false).");
        }

        await db.JoinTransactionAsync(cancellationToken);
        var tenantId = TenantId;
        var now = clock.GetUtcNow();

        // Concurrent first updates must not race on the insert: make sure the (default, off) row exists, then lock it.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO integrations.ai_settings (tenant_id, ai_parsing_enabled, row_version, updated_at, updated_by)
            VALUES ({tenantId}, false, 1, {now}, {UserId}) ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);
        var row = (await db.Settings
                .FromSql($"SELECT * FROM integrations.ai_settings WHERE tenant_id = {tenantId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .Single();
        if (expectedVersion is { } version && version != row.RowVersion)
        {
            return AppError.VersionMismatch();
        }

        var before = row.AiParsingEnabled;
        if (before != enabled)
        {
            row.Set(enabled, UserId, now);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(
                new AuditEvent(AssistantAuditActions.SettingsChanged, AssistantAuditActions.SettingsEntityType, tenantId, new AuditChanges()
                    .Field("aiParsingEnabled", before, enabled)
                    .ToDictionary()),
                cancellationToken);
        }

        return Settings(row.AiParsingEnabled, row.UpdatedAt, row.UpdatedBy, row.RowVersion);
    }

    private AiSettingsResponse Settings(bool enabled, DateTimeOffset? updatedAt, Guid? updatedBy, long version)
    {
        var settings = options.Value;
        return new AiSettingsResponse(
            enabled, ai.IsConfigured, new AiInputLimits(settings.MaxInputChars, settings.MaxInputLines, settings.HourlyParseLimit), updatedAt, updatedBy, version);
    }

    // ---------------------------------------------------------------- learned aliases (WAI-07)

    public async Task<LearnedAliasListResponse> ListAliasesAsync(Guid? variantId, Guid? customerId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Aliases.AsNoTracking();
        if (variantId is { } variant)
        {
            query = query.Where(a => a.VariantId == variant);
        }

        if (customerId is { } customer)
        {
            query = query.Where(a => a.CustomerId == customer);
        }

        var items = await query
            .OrderByDescending(a => a.UpdatedAt).ThenBy(a => a.Id)
            .Take(Math.Clamp(limit, 1, MaxListLimit))
            .Select(a => new LearnedAliasResponse(a.Id, a.Phrase, a.VariantId, a.CustomerId, a.Confirmations, a.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new LearnedAliasListResponse(items);
    }

    public async Task<bool> DeleteAliasAsync(Guid id, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var alias = await db.Aliases.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (alias is null)
        {
            return false;
        }

        db.Aliases.Remove(alias);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AssistantAuditActions.AliasDeleted, AssistantAuditActions.AliasEntityType, alias.Id, new AuditChanges()
                .Value("variantId", alias.VariantId)
                .Value("customerSpecific", alias.CustomerId is not null)
                .Value("confirmations", alias.Confirmations)
                .ToDictionary()),
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Upserts phrase → variant in the tenant scope and, when the order has a customer, in that customer's scope
    /// (latest confirmation wins; confirmations count up while the variant stays the same). Atomic under concurrency.
    /// </summary>
    private async Task LearnAsync(Guid parseId, Guid? customerId, IReadOnlyList<(string Phrase, Guid VariantId)> learned, CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var (phrase, variantId) in learned)
        {
            foreach (var scope in customerId is null ? new Guid?[] { null } : [null, customerId])
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO integrations.learned_aliases AS a
                        (id, tenant_id, customer_id, phrase, variant_id, confirmations, last_parse_id, created_at, created_by, updated_at, updated_by)
                    VALUES (@id, @tenant, @customer, @phrase, @variant, 1, @parse, @now, @user, @now, @user)
                    ON CONFLICT (tenant_id, customer_id, phrase) DO UPDATE SET
                        confirmations = CASE WHEN a.variant_id = EXCLUDED.variant_id THEN a.confirmations + 1 ELSE 1 END,
                        variant_id = EXCLUDED.variant_id,
                        last_parse_id = EXCLUDED.last_parse_id,
                        updated_at = EXCLUDED.updated_at,
                        updated_by = EXCLUDED.updated_by
                    """,
                    [
                        new NpgsqlParameter<Guid>("id", Guid.CreateVersion7()),
                        new NpgsqlParameter<Guid>("tenant", TenantId),
                        new NpgsqlParameter("customer", NpgsqlDbType.Uuid) { Value = (object?)scope ?? DBNull.Value },
                        new NpgsqlParameter<string>("phrase", phrase),
                        new NpgsqlParameter<Guid>("variant", variantId),
                        new NpgsqlParameter<Guid>("parse", parseId),
                        new NpgsqlParameter<DateTimeOffset>("now", now),
                        new NpgsqlParameter<Guid>("user", UserId),
                    ],
                    cancellationToken);
            }
        }
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

        // Units: the order is in the variant's unit. A unit the reviewer confirms must be exactly that unit.
        var included = choices.Where(c => c.Include).ToList();
        var variants = (await catalog.FindByIdsAsync([.. included.Select(c => c.VariantId!.Value).Distinct()], cancellationToken))
            .ToDictionary(v => v.VariantId);
        foreach (var choice in included.Where(c => c.Unit is not null))
        {
            if (variants.TryGetValue(choice.VariantId!.Value, out var variant)
                && !string.Equals(choice.Unit!.Trim(), variant.Unit, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"lines[{choice.Index}].unit"] = [$"The order line is in the variant's unit ({variant.Unit}); convert the quantity to {variant.Unit}."];
            }
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var result = await drafts.CreateDraftAsync(
            new AssistantDraftOrder(
                customerId!.Value,
                request.WarehouseId ?? entity.WarehouseId,
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
                SelectedUnit = choice.Include && variants.TryGetValue(choice.VariantId!.Value, out var variant) ? variant.Unit : null,
            };
        }).ToList();

        // WAI-07: the reviewer's decisions become aliases (the Orders module has just accepted these variants). Lines the
        // message already pinned down by an exact code or name teach nothing new.
        var learned = included
            .Where(c => c.Learn)
            .Select(c => (Line: stored.Lines.Single(l => l.LineNo == c.LineNo), c.VariantId!.Value))
            .Where(x => !x.Line.Candidates.Any(k => k.VariantId == x.Value && k.Method is MatchMethods.Sku or MatchMethods.Barcode or MatchMethods.Name))
            .Select(x => (Phrase: CatalogMatcher.AliasKey(ToParsed(x.Line)), VariantId: x.Value))
            .Where(x => x.Phrase.Length is > 0 and <= MaxAliasPhrase)
            .DistinctBy(x => x.Phrase)
            .ToList();
        await LearnAsync(entity.Id, customerId, learned, cancellationToken);

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
                .Value("aliasesLearned", learned.Count)
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
        var entity = await LockAsync(id, cancellationToken);
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
            return (null, new AppError(409, AssistantProblems.NotReviewable, $"The parse is {entity.Status}"));
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

    private sealed record Choice(int Index, int LineNo, bool Include, Guid? VariantId, decimal? Quantity, string Selection, string? Unit, bool Learn);

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
                choices.Add(new Choice(i, lineNo, false, null, null, Selections.Skipped, null, false));
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

            if (item.Unit is { } unit && (string.IsNullOrWhiteSpace(unit) || unit.Length > 32))
            {
                errors[$"{key}.unit"] = ["A unit code of at most 32 characters, or omit it."];
                continue;
            }

            var selection = variantId == line.ProposedVariantId ? Selections.Proposed
                : line.Candidates.Any(c => c.VariantId == variantId) ? Selections.Candidate
                : Selections.Manual;
            choices.Add(new Choice(i, lineNo, true, variantId, q, selection, item.Unit, item.LearnAlias ?? true));
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

    private static ParsedLine ToParsed(OrderParseLineResponse line) =>
        new(line.RawText, line.ProductCode, line.ProductText, line.Quantity, line.Unit, line.BrandModel, line.ParseConfidence, line.Notes);

    private static OrderParseLineResponse ToResponse(ReviewLine line, (int Start, int Length)? span) =>
        new(
            line.LineNo,
            line.Parsed.RawText,
            span?.Start,
            span?.Length,
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
            entity.WarehouseId,
            entity.Parser,
            entity.Model,
            entity.PromptVersion,
            entity.FallbackReason,
            entity.FailureCode,
            entity.RawText,
            entity.TextPurgedAt is not null,
            stored?.Lines ?? [],
            stored?.UnparsedFragments ?? [],
            warnings,
            new OrderParseSummary(entity.LineCount, entity.MatchedCount, entity.AmbiguousCount, entity.UnmatchedCount, entity.OverallConfidence),
            entity.OrderId,
            entity.CreatedAt,
            entity.CreatedBy,
            entity.CompletedAt,
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
}

/// <summary>Processing states of the status endpoint.</summary>
internal static class ParseStates
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

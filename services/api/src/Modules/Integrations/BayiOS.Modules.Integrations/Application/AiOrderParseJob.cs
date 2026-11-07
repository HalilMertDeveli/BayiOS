using System.Diagnostics;
using BayiOS.Modules.Integrations.Domain;
using BayiOS.Modules.Integrations.Parsing;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>Payload of <see cref="AiOrderParseJob"/>: the parse id only (never message text).</summary>
internal sealed record AiOrderParsePayload(Guid ParseId);

/// <summary>
/// <c>integrations.ai_order_parse</c> (ADR 0010 §2.1, #121): parses one accepted message in the background. It is an
/// <see cref="IExternalCallJobHandler"/>, so the worker holds no connection or transaction while the provider answers:
/// <list type="number">
/// <item>short transaction: read the pending parse (skip if already processed, past the deadline or opted out) → commit;</item>
/// <item>no connection: reserve the daily AI quota (own short transaction), call the AI parser (≤ <c>Ai:TimeoutSeconds</c>
/// × (1 + <c>Ai:MaxRetries</c>)), record tokens (own short transaction) — or the rules parser when AI is off, over quota
/// or failed;</item>
/// <item>short transaction: match against the catalog and aliases, store the review, mark the job succeeded — all or nothing.
/// If the lease was lost meanwhile (another worker took the job), this rolls back and the other worker's result stands.</item>
/// </list>
/// Idempotent: a retry or a duplicate run of a processed parse is a no-op. When the last attempt fails the parse is
/// marked failed so pollers stop.
/// </summary>
internal sealed partial class AiOrderParseJob(
    IJobTransactions transactions,
    AiParserAccessor ai,
    AiUsageMeter meter,
    IOptions<AiOptions> options,
    ILogger<AiOrderParseJob> logger) : IExternalCallJobHandler
{
    public const string Type = IntegrationsModule.OrderParseJobType;

    /// <summary>Attempts before the parse is marked failed (the provider itself is retried by the SDK within one attempt).</summary>
    public const int MaxAttempts = 3;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.TenantId is null)
        {
            return; // tenant jobs only
        }

        var parseId = job.PayloadAs<AiOrderParsePayload>().ParseId;
        try
        {
            var pending = await transactions.RunAsync(
                (sp, ct) => sp.GetRequiredService<OrderParseService>().BeginJobAsync(parseId, ct), cancellationToken);
            if (pending is null)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var serverWarnings = new List<string>(pending.Warnings);
            var outcome = await ParseTextAsync(pending, serverWarnings, cancellationToken);
            var duration = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await transactions.CompleteAsync(
                (sp, ct) => sp.GetRequiredService<OrderParseService>().CompleteJobAsync(pending, outcome, serverWarnings, duration, ct),
                cancellationToken);
        }
        catch (Exception ex) when (job.Attempt >= job.MaxAttempts && ex is not OperationCanceledException)
        {
            // Last attempt: record the failure so the status endpoint answers "failed"; the job still goes dead (alarm).
            await transactions.RunAsync(
                async (sp, ct) =>
                {
                    await sp.GetRequiredService<OrderParseService>().FailJobAsync(parseId, ParseFailureCodes.ProcessingFailed, ct);
                    return true;
                },
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Phase 2, with no connection of the job held: the AI parser when it is configured and under the tenant's daily
    /// quota, else (or on any provider failure) the deterministic rules parser.
    /// </summary>
    private async Task<ParseOutcome> ParseTextAsync(PendingParse pending, List<string> serverWarnings, CancellationToken cancellationToken)
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
        else if (await meter.TryReserveAiCallAsync(pending.TenantId, pending.CreatedBy, settings.DailyCallLimit, settings.DailyTokenLimit, cancellationToken)
                 is not AiQuotaDecision.Allowed)
        {
            reason = FallbackReasons.QuotaExceeded;
            serverWarnings.Add(ParseWarnings.AiQuotaExceeded);
        }
        else
        {
            var result = await parser.ParseAsync(pending.MaskedText, cancellationToken);
            usage = result.Usage;

            // Not tied to the job token: a call that was made is always counted.
            await meter.RecordAiCallAsync(pending.TenantId, pending.CreatedBy, usage, failed: result.Parse is null, CancellationToken.None);
            if (result.Parse is not null)
            {
                return new ParseOutcome(result.Parse, ParserKinds.Ai, result.Model, result.PromptVersion, null, usage);
            }

            reason = result.FailureReason ?? FallbackReasons.ProviderError;
            serverWarnings.Add(ParseWarnings.AiUnavailable);
        }

        if (parser is not null)
        {
            await meter.RecordFallbackAsync(pending.TenantId, pending.CreatedBy, CancellationToken.None);
            BayiOsMetrics.AiFallbacks.Add(1, new KeyValuePair<string, object?>("reason", reason));
            LogFallback(logger, pending.TenantId, reason);
        }

        return new ParseOutcome(RuleBasedOrderParser.Parse(pending.MaskedText), ParserKinds.Rules, null, RuleBasedOrderParser.Version, reason, usage);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI order parse for tenant {TenantId} used the rules parser ({Reason})")]
    private static partial void LogFallback(ILogger logger, Guid tenantId, string reason);
}

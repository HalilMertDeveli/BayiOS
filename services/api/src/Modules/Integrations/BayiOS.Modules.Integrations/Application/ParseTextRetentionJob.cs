using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>
/// <c>integrations.ai_text_retention</c> (tenant-less maintenance job, scheduled by the platform worker every
/// maintenance interval): clears the message text of parse requests older than <see cref="AiOptions.TextRetentionDays"/>
/// (data-policy §4: 30 days) — <c>raw_text</c>, <c>parsed_payload</c> and <c>review</c> become null, <c>text_purged_at</c>
/// is set; counts, tokens, status and the order link stay. Runs through
/// <c>integrations.purge_expired_parse_texts</c> (SECURITY DEFINER: a job without a tenant sees nothing under RLS; the
/// owner may only null those columns of rows older than one day). Batched, oldest first; idempotent.
/// </summary>
internal sealed partial class ParseTextRetentionJob(UnitOfWork unitOfWork, IOptions<AiOptions> options, ILogger<ParseTextRetentionJob> logger) : IJobHandler
{
    public const string Type = IntegrationsModule.TextRetentionJobType;
    public const int BatchSize = 500;
    public const int MaxBatches = 50;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.TenantId is not null)
        {
            return; // system job only
        }

        var retention = TimeSpan.FromDays(options.Value.TextRetentionDays);
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT integrations.purge_expired_parse_texts(@retention, @limit)", unitOfWork.Connection, unitOfWork.Transaction);
            command.Parameters.Add(new NpgsqlParameter<TimeSpan>("retention", retention));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", BatchSize));
            var purged = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            total += purged;
            if (purged < BatchSize)
            {
                break;
            }
        }

        LogPurged(logger, total);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI parse text retention cleared {Count} messages past the retention period")]
    private static partial void LogPurged(ILogger logger, int count);
}

using System.Globalization;
using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Domain;
using BayiOS.Modules.Imports.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Modules.Imports.Application;

/// <summary>
/// Permission rules of import runs and rollbacks (roles-permissions §3.3), shared by the commands and the jobs that
/// re-check them on every chunk: <c>imports.run</c> / <c>imports.rollback</c> plus the write permission of whatever
/// the import touches — <c>catalog.write</c> for products, <c>prices.write</c> for any price list.
/// </summary>
internal static class ImportPermissions
{
    public static AppError? RunDenied(string? role, string type, bool mapsPrice)
    {
        if (type == ImportTypes.Products && !RolePermissions.IsGranted(role, Permissions.CatalogWrite))
        {
            return AppError.Forbidden("Product imports need the catalog.write permission");
        }

        return (type == ImportTypes.Prices || mapsPrice) && !RolePermissions.IsGranted(role, Permissions.PricesWrite)
            ? AppError.Forbidden("Price imports need the prices.write permission")
            : null;
    }

    public static AppError? RollbackDenied(ImportJob import, string? role) =>
        !RolePermissions.IsGranted(role, Permissions.ImportsRollback)
            ? AppError.Forbidden("Rolling back an import needs the imports.rollback permission")
            : RunDenied(role, import.Type, import.PriceListId is not null);
}

/// <summary>
/// <c>imports.maintenance</c> (tenant-less, enqueued by <see cref="ImportsMaintenanceScheduler"/>):
/// <list type="number">
/// <item>deletes raw uploaded files past the retention window (data-policy §4: 30 days after the import finished)
/// through <c>imports.purge_expired_files</c> — SECURITY DEFINER, because a job without a tenant sees nothing under
/// RLS; it never touches an active import and keeps the job metadata and row results (rollback does not need the
/// file);</item>
/// <item>fans out <c>imports.sweep_stalled</c> to the owning tenant for every import run whose job is <c>dead</c>
/// with no live job left. The tenant job decides under RLS and the import's row lock.</item>
/// </list>
/// </summary>
internal sealed partial class ImportsMaintenanceJob(
    UnitOfWork unitOfWork,
    IImportSystemJobs systemJobs,
    IOptions<ImportsOptions> options,
    ILogger<ImportsMaintenanceJob> logger) : IJobHandler
{
    public const string Type = "imports.maintenance";
    public const int PurgeBatchSize = 200;
    public const int MaxPurgeBatches = 50;
    public const int MaxSweepsPerRun = 500;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.TenantId is not null)
        {
            return; // system job only
        }

        var settings = options.Value;
        var purged = 0;
        for (var batch = 0; batch < MaxPurgeBatches; batch++)
        {
            await using var command = new NpgsqlCommand("SELECT imports.purge_expired_files(@retention, @limit)", unitOfWork.Connection, unitOfWork.Transaction);
            command.Parameters.Add(new NpgsqlParameter<TimeSpan>("retention", settings.FileRetention));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", PurgeBatchSize));
            var deleted = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            purged += deleted;
            if (deleted < PurgeBatchSize)
            {
                break;
            }
        }

        var dead = await systemJobs.FindDeadJobsAsync(
            [ValidateImportJob.Type, ExecuteImportJob.Type, RollbackImportJob.Type], settings.DeadJobLookback, MaxSweepsPerRun, cancellationToken);
        foreach (var run in dead)
        {
            await systemJobs.EnqueueForTenantAsync(run.TenantId, SweepStalledImportJob.Request(run), cancellationToken);
        }

        LogMaintenance(logger, purged, dead.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imports maintenance: purged {Files} raw files, {Runs} dead import runs to sweep")]
    private static partial void LogMaintenance(ILogger logger, int files, int runs);
}

/// <summary>Payload of <c>imports.sweep_stalled</c>: the run whose job died, and that job's type.</summary>
internal sealed record StalledImportPayload(Guid ImportId, Guid RunId, string JobType);

/// <summary>
/// <c>imports.sweep_stalled</c> (tenant job): an import whose job exhausted its retries stays in its working state
/// forever. Once it has made no progress for <see cref="ImportsOptions.StallTimeout"/> and is still on the dead run, it
/// is failed — <c>failed</c> for validation/execution (start resumes from the row checkpoint), <c>rollback_failed</c>
/// for a rollback (requesting it again resumes) — with an audit event. Anything else (newer run, progress, already
/// finished) is a no-op, so the sweep is idempotent.
/// </summary>
internal sealed class SweepStalledImportJob(
    ImportsDbContext db,
    IAuditLog audit,
    IRequestContext requestContext,
    IOptions<ImportsOptions> options,
    TimeProvider clock) : IJobHandler
{
    public const string Type = "imports.sweep_stalled";

    public static JobRequest Request(DeadImportJob run) =>
        new(Type, new StalledImportPayload(run.ImportId, run.RunId, run.Type), MaxAttempts: 3, DedupeKey: $"{Type}:{run.ImportId:N}:{run.RunId:N}");

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        var payload = job.PayloadAs<StalledImportPayload>();
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("Import jobs need a tenant.");
        var import = await ImportLocks.LockAsync(db, payload.ImportId, tenantId, cancellationToken);
        var expected = payload.JobType switch
        {
            ValidateImportJob.Type => ImportStatuses.Validating,
            ExecuteImportJob.Type => ImportStatuses.Running,
            RollbackImportJob.Type => ImportStatuses.RollingBack,
            _ => null,
        };
        if (import is null || expected is null || import.Status != expected || import.RunId != payload.RunId)
        {
            return;
        }

        var now = ImportClock.Now(clock);
        if ((import.UpdatedAt ?? import.CreatedAt) > now - options.Value.StallTimeout)
        {
            return; // made progress recently; the next maintenance run looks again
        }

        var minutes = ((int)options.Value.StallTimeout.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        import.RunId = null; // a late job of the dead run becomes a no-op
        import.FailureMessage = $"The background job failed repeatedly and the import made no progress for {minutes} minutes; it can be resumed.";
        if (import.Status == ImportStatuses.RollingBack)
        {
            import.Status = ImportStatuses.RollbackFailed;
            import.FailureCode = "imports.rollback_stalled";
        }
        else
        {
            import.Status = ImportStatuses.Failed;
            import.FailureCode = "imports.stalled";
            import.FinishedAt = now;
        }

        import.Touch(null, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                import.Status == ImportStatuses.RollbackFailed ? ImportAuditActions.RollbackFailed : ImportAuditActions.Failed,
                "import_job",
                import.Id,
                new AuditChanges()
                    .Value("reason", import.FailureCode)
                    .Value("deadJob", payload.JobType)
                    .Value("processedRows", import.ProcessedRows)
                    .Value("checkpointRow", import.CheckpointRow)
                    .Value("rollbackHandledRows", import.RollbackProcessedRows)
                    .ToDictionary()),
            cancellationToken);
    }
}

/// <summary>
/// The scheduling primitive the platform job API does not have yet: every <see cref="ImportsOptions.MaintenanceInterval"/>
/// each instance with a background worker enqueues <c>imports.maintenance</c> with an interval-bucket dedupe key, so
/// several instances enqueue it once per interval (same pattern as the platform's idempotency cleanup).
/// </summary>
internal sealed partial class ImportsMaintenanceScheduler(
    IServiceScopeFactory scopes,
    IOptions<ImportsOptions> options,
    TimeProvider clock,
    ILogger<ImportsMaintenanceScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.MaintenanceInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : options.Value.MaintenanceInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var systemJobs = scope.ServiceProvider.GetRequiredService<IImportSystemJobs>();
                if (!systemJobs.WorkerEnabled)
                {
                    return;
                }

                var bucket = clock.GetUtcNow().Ticks / interval.Ticks;
                await systemJobs.EnqueueSystemAsync(ImportsMaintenanceJob.Type, $"{ImportsMaintenanceJob.Type}:{bucket.ToString(CultureInfo.InvariantCulture)}", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database outage etc.: keep the host alive and try again next interval.
                LogScheduleFailed(logger, ex);
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not enqueue the imports maintenance job; retrying next interval")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception);
}

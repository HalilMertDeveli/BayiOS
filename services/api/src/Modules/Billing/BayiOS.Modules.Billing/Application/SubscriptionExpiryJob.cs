using BayiOS.Modules.Billing.Domain;
using BayiOS.Modules.Billing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BayiOS.Modules.Billing.Application;

/// <summary>
/// <c>billing.subscription_expiry</c> (tenant-less maintenance job, scheduled by the platform worker every maintenance
/// interval). Entitlements never wait for it — the resolver already treats an ended trial as Basic at that instant —
/// it only records the change: a trial past its end becomes <c>expired</c> on the fallback plan, a module trial past its
/// end becomes <c>expired</c>, each audited. <b>No data is deleted or changed anywhere else</b> (PRD SUB-06).
/// <para>
/// A job without a tenant sees nothing under RLS, so <c>billing.due_subscription_tenants</c> (SECURITY DEFINER, pinned
/// search_path; the owner's policies expose only trialing rows whose end has passed) lists the tenant ids only. Each
/// tenant is then processed in its own tenant context: reads, updates and audit go through RLS as in a request.
/// Idempotent and batched.
/// </para>
/// </summary>
internal sealed partial class SubscriptionExpiryJob(
    UnitOfWork unitOfWork,
    BillingDbContext db,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<SubscriptionExpiryJob> logger) : IJobHandler
{
    public const string Type = BillingModule.ExpiryJobType;
    public const int BatchSize = 200;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        if (job.TenantId is not null)
        {
            return; // system job only
        }

        var tenants = new List<Guid>();
        await using (var command = new NpgsqlCommand("SELECT billing.due_subscription_tenants(@limit)", unitOfWork.Connection, unitOfWork.Transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<int>("limit", BatchSize));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tenants.Add(reader.GetGuid(0));
            }
        }

        int trials = 0, modules = 0;
        foreach (var tenantId in tenants)
        {
            await unitOfWork.SetTenantAsync(tenantId, cancellationToken);
            await db.JoinTransactionAsync(cancellationToken);
            var now = clock.Now();

            var subscription = await db.Subscriptions
                .SingleOrDefaultAsync(s => s.Status == SubscriptionStatuses.Trialing && s.TrialEndsAt <= now, cancellationToken);
            if (subscription is not null)
            {
                var endedAt = subscription.TrialEndsAt;
                subscription.ExpireTrial(now);
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(
                    new AuditEvent(BillingAuditActions.TrialExpired, "tenant_subscription", subscription.Id, new AuditChanges()
                        .Field("status", SubscriptionStatuses.Trialing, subscription.Status)
                        .Field("plan", BillingCatalog.TrialPlan.Code, subscription.PlanCode)
                        .Value("trialEndedAt", endedAt)
                        .ToDictionary()),
                    cancellationToken);
                trials++;
            }

            var ended = await db.Modules
                .Where(m => m.Status == ModuleStatuses.Trialing && m.ExpiresAt <= now)
                .ToListAsync(cancellationToken);
            foreach (var module in ended)
            {
                module.Status = ModuleStatuses.Expired;
                module.Touch(null, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var module in ended)
            {
                await audit.RecordAsync(
                    new AuditEvent(BillingAuditActions.ModuleTrialExpired, "tenant_module", module.Id, new AuditChanges()
                        .Value("module", module.ModuleCode)
                        .Field("status", ModuleStatuses.Trialing, module.Status)
                        .Value("expiredAt", module.ExpiresAt)
                        .ToDictionary()),
                    cancellationToken);
            }

            modules += ended.Count;
            db.ChangeTracker.Clear();
        }

        LogExpired(logger, tenants.Count, trials, modules);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription expiry: {Tenants} tenants, {Trials} trials and {Modules} module trials expired")]
    private static partial void LogExpired(ILogger logger, int tenants, int trials, int modules);
}

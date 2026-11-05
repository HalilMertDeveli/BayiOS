using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Billing.Domain;
using BayiOS.Modules.Billing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Billing.Application;

/// <summary>
/// The module store (PRD SUB-04, #52): catalog, the tenant's subscription and add-ons, usage against limits, and the
/// free activation / deactivation of an add-on. There is no payment: "activation" is a time-limited free trial of the
/// module (<see cref="BillingOptions.ModuleTrialDays"/>), once per module and tenant; deactivating keeps every record.
/// Changes take the tenant's subscription row lock (<c>FOR UPDATE</c>) so concurrent store actions serialize.
/// </summary>
internal sealed class BillingService(
    BillingDbContext db,
    EntitlementService entitlements,
    IRequestContext requestContext,
    IAuditLog audit,
    IOptions<BillingOptions> options,
    IServiceProvider services,
    TimeProvider clock)
{
    public const string StatusIncludedInTrial = "included_in_trial";
    public const string StatusAvailable = "available";
    public const string StatusUnavailable = "unavailable";

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public static BillingCatalogResponse Catalog(BillingOptions settings) => new(
        BillingCatalog.Version,
        BillingCatalog.TrialPlan.Code,
        BillingCatalog.FallbackPlan.Code,
        settings.TrialDays,
        settings.ModuleTrialDays,
        [.. BillingCatalog.Plans.OrderBy(p => p.SortOrder).Select(p => new PlanResponse(p.Code, p.Name, p.Description, p.Features, Limits(p.Limits)))],
        [.. BillingCatalog.Modules.OrderBy(m => m.SortOrder).Select(m => new CatalogModuleResponse(m.Code, m.Name, m.Description, m.Available, m.Features, Limits(m.Limits)))]);

    public async Task<SubscriptionResponse> SubscriptionAsync(CancellationToken cancellationToken)
    {
        var resolved = await entitlements.ResolveAsync(cancellationToken);
        var snapshot = resolved.Snapshot;
        return new SubscriptionResponse(
            snapshot.Plan,
            BillingCatalog.FindPlan(snapshot.Plan)?.Name ?? snapshot.Plan,
            snapshot.Status,
            snapshot.TrialEndsAt,
            snapshot.CatalogVersion,
            [.. snapshot.Features],
            Limits(snapshot.Limits),
            await ModulesAsync(resolved, cancellationToken));
    }

    public async Task<TenantModuleListResponse> ListModulesAsync(CancellationToken cancellationToken) =>
        new(await ModulesAsync(await entitlements.ResolveAsync(cancellationToken), cancellationToken));

    public async Task<UsageResponse> UsageAsync(CancellationToken cancellationToken)
    {
        var snapshot = await entitlements.CurrentAsync(cancellationToken);
        var current = services.GetService<IBillingUsageSource>() is { } source
            ? await source.CurrentAsync(cancellationToken)
            : new Dictionary<string, long>();
        var items = EntitlementCodes.Limits.Select(code =>
        {
            var limit = snapshot.LimitOf(code);
            var used = current.GetValueOrDefault(code);
            return new UsageItemResponse(
                code, limit, used, limit is { } l ? Math.Max(0, l - used) : null, code == EntitlementCodes.AiParsesPerMonth ? "month" : null);
        });
        return new UsageResponse(snapshot.Plan, snapshot.Status, [.. items]);
    }

    public async Task<AppResult<TenantModuleResponse>> ActivateAsync(string code, CancellationToken cancellationToken)
    {
        if (BillingCatalog.FindModule(code) is not { } definition)
        {
            return AppError.NotFound();
        }

        if (!definition.Available)
        {
            return new AppError(409, BillingProblems.ModuleUnavailable, "This module is not available yet");
        }

        var subscription = await LockSubscriptionAsync(cancellationToken);
        var now = clock.Now();
        var row = await db.Modules.SingleOrDefaultAsync(m => m.ModuleCode == code, cancellationToken);
        if (row is not null && EntitlementResolver.IsOn(row.Facts, now))
        {
            return new AppError(409, BillingProblems.ModuleAlreadyActive, "The module is already active");
        }

        string before;
        if (row is null)
        {
            // Activating during the tenant trial keeps the module after the trial: the free period starts when the trial ends.
            var trial = EntitlementResolver.EffectivePlan(subscription.Facts, now);
            var start = trial.Status == SubscriptionStatuses.Trialing && trial.TrialEndsAt is { } ends && ends > now ? ends : now;
            row = TenantModule.StartTrial(TenantId, code, start.AddDays(options.Value.ModuleTrialDays), UserId, now);
            db.Modules.Add(row);
            before = StatusAvailable;
        }
        else if (row.Status == ModuleStatuses.Cancelled && row.ExpiresAt > now)
        {
            before = row.Status;
            row.Status = ModuleStatuses.Trialing;
            row.DeactivatedAt = null;
            row.DeactivatedBy = null;
            row.Touch(UserId, now);
        }
        else
        {
            return new AppError(409, BillingProblems.TrialUsed, "The free activation of this module was already used; paid activation is not available yet");
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(BillingAuditActions.ModuleActivated, "tenant_module", row.Id, new AuditChanges()
                .Value("module", code)
                .Field("status", before, row.Status)
                .Value("expiresAt", row.ExpiresAt)
                .ToDictionary()),
            cancellationToken);
        entitlements.Invalidate();
        return Module(definition, row, await entitlements.ResolveAsync(cancellationToken), now);
    }

    public async Task<AppResult<TenantModuleResponse>> DeactivateAsync(string code, CancellationToken cancellationToken)
    {
        if (BillingCatalog.FindModule(code) is not { } definition)
        {
            return AppError.NotFound();
        }

        await LockSubscriptionAsync(cancellationToken);
        var now = clock.Now();
        var row = await db.Modules.SingleOrDefaultAsync(m => m.ModuleCode == code, cancellationToken);
        if (row is null || !EntitlementResolver.IsOn(row.Facts, now))
        {
            return new AppError(409, BillingProblems.ModuleNotActive, "The module is not activated (a trial-included module ends with the trial)");
        }

        var before = row.Status;
        row.Status = ModuleStatuses.Cancelled;
        row.DeactivatedAt = now;
        row.DeactivatedBy = UserId;
        row.Touch(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(BillingAuditActions.ModuleDeactivated, "tenant_module", row.Id, new AuditChanges()
                .Value("module", code)
                .Field("status", before, row.Status)
                .ToDictionary()),
            cancellationToken);
        entitlements.Invalidate();
        return Module(definition, row, await entitlements.ResolveAsync(cancellationToken), now);
    }

    /// <summary>Serializes store changes per tenant; provisions the trial first for tenants that predate billing.</summary>
    private async Task<TenantSubscription> LockSubscriptionAsync(CancellationToken cancellationToken)
    {
        await entitlements.ResolveAsync(cancellationToken);
        await db.JoinTransactionAsync(cancellationToken);
        return await db.Subscriptions.FromSql($"SELECT * FROM billing.tenant_subscriptions FOR UPDATE").SingleAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<TenantModuleResponse>> ModulesAsync(ResolvedEntitlements resolved, CancellationToken cancellationToken)
    {
        var now = clock.Now();
        var rows = await db.Modules.AsNoTracking().ToListAsync(cancellationToken);
        return [.. BillingCatalog.Modules.OrderBy(m => m.SortOrder).Select(m => Module(m, rows.FirstOrDefault(r => r.ModuleCode == m.Code), resolved, now))];
    }

    private static TenantModuleResponse Module(ModuleDefinition definition, TenantModule? row, ResolvedEntitlements resolved, DateTimeOffset now)
    {
        if (!definition.Available)
        {
            return new TenantModuleResponse(definition.Code, definition.Name, false, StatusUnavailable, null, false, false);
        }

        if (row is not null && EntitlementResolver.IsOn(row.Facts, now))
        {
            return new TenantModuleResponse(definition.Code, definition.Name, true, row.Status, row.ExpiresAt, false, true);
        }

        var canActivate = row is null || (row.Status == ModuleStatuses.Cancelled && row.ExpiresAt > now);
        if (resolved.Modules.FirstOrDefault(m => m.ModuleCode == definition.Code && m.Source == EntitlementResolver.SourceTrial) is { } trial)
        {
            return new TenantModuleResponse(definition.Code, definition.Name, true, StatusIncludedInTrial, trial.Until, canActivate, false);
        }

        if (row is null)
        {
            return new TenantModuleResponse(definition.Code, definition.Name, true, StatusAvailable, null, true, false);
        }

        var status = canActivate ? ModuleStatuses.Cancelled : ModuleStatuses.Expired;
        return new TenantModuleResponse(definition.Code, definition.Name, true, status, row.ExpiresAt, canActivate, false);
    }

    private static IReadOnlyList<LimitValueResponse> Limits(IReadOnlyDictionary<string, long?> limits) =>
        [.. limits.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new LimitValueResponse(l.Key, l.Value))];
}

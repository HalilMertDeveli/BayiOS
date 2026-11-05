using BayiOS.Modules.Billing.Domain;
using BayiOS.Modules.Billing.Infrastructure.Persistence;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BayiOS.Modules.Billing.Application;

/// <summary>
/// <see cref="IEntitlements"/> of the active tenant (request-scoped, resolved once per request through RLS on the
/// request transaction) and the trial provisioning of new tenants.
/// <para>
/// A tenant without a subscription row (created before billing existed) gets one on its first write request — a fresh
/// trial, audited. A read-only request cannot write, so it resolves the same trial virtually (starting now) without
/// storing it. Either way the tenant is never left without entitlements nor granted more than a trial.
/// </para>
/// </summary>
internal sealed class EntitlementService(
    BillingDbContext db,
    UnitOfWork unitOfWork,
    IRequestContext requestContext,
    IAuditLog audit,
    IOptions<BillingOptions> options,
    TimeProvider clock) : IEntitlements, ITenantSubscriptionProvisioner
{
    private Guid? _resolvedFor;
    private ResolvedEntitlements? _resolved;

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public async Task<EntitlementSnapshot> CurrentAsync(CancellationToken cancellationToken) =>
        (await ResolveAsync(cancellationToken)).Snapshot;

    public async Task EnsureFeatureAsync(string feature, CancellationToken cancellationToken)
    {
        if (!(await CurrentAsync(cancellationToken)).Has(feature))
        {
            throw Required(feature);
        }
    }

    public async Task EnsureCapacityAsync(string limit, long current, CancellationToken cancellationToken)
    {
        if ((await CurrentAsync(cancellationToken)).LimitOf(limit) is { } value && current >= value)
        {
            throw LimitExceeded(limit, value, current);
        }
    }

    public EntitlementException Required(string feature) =>
        new(EntitlementProblems.Required, feature, Suggest(feature));

    public EntitlementException LimitExceeded(string limit, long limitValue, long current) =>
        new(EntitlementProblems.LimitExceeded, limit, Suggest(limit), limitValue, current);

    /// <summary>Plan + add-ons + overrides of the active tenant at this instant (cached for the request).</summary>
    public async Task<ResolvedEntitlements> ResolveAsync(CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        if (_resolved is not null && _resolvedFor == tenantId)
        {
            return _resolved;
        }

        var now = clock.Now();
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (subscription is null && unitOfWork.IsActive && !unitOfWork.IsReadOnly)
        {
            await StartTrialAsync(cancellationToken);
            subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        }

        var facts = subscription?.Facts
            ?? new SubscriptionFacts(BillingCatalog.TrialPlan.Code, SubscriptionStatuses.Trialing, now.AddDays(options.Value.TrialDays), null);
        var modules = await db.Modules.AsNoTracking().Select(m => new ModuleFacts(m.ModuleCode, m.Status, m.ExpiresAt)).ToListAsync(cancellationToken);
        var overrides = await db.Overrides.AsNoTracking()
            .Select(o => new OverrideFacts(o.FeatureCode, o.Enabled, o.LimitValue, o.ExpiresAt))
            .ToListAsync(cancellationToken);

        _resolved = EntitlementResolver.Resolve(facts, modules, overrides, now);
        _resolvedFor = tenantId;
        return _resolved;
    }

    /// <summary>Forget the cached resolution (after a change in this request).</summary>
    public void Invalidate() => _resolved = null;

    /// <summary>
    /// Inserts the trial subscription of the current tenant unless it has one (<c>ON CONFLICT DO NOTHING</c>: concurrent
    /// first requests never fail the transaction). Audited as <c>billing.trial_started</c>.
    /// </summary>
    public async Task StartTrialAsync(CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var transaction = unitOfWork.Transaction ?? throw new InvalidOperationException("The unit of work has not started.");
        var now = clock.Now();
        var endsAt = now.AddDays(options.Value.TrialDays);
        var id = Guid.CreateVersion7();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO billing.tenant_subscriptions
                (id, tenant_id, plan_code, status, trial_started_at, trial_ends_at, current_period_start, catalog_version, row_version, created_at, created_by)
            VALUES (@id, @tenant, @plan, 'trialing', @now, @ends, @now, @version, 1, @now, @user)
            ON CONFLICT (tenant_id) DO NOTHING
            RETURNING id
            """,
            unitOfWork.Connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        command.Parameters.Add(new NpgsqlParameter<Guid>("tenant", tenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("plan", BillingCatalog.TrialPlan.Code));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", now));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("ends", endsAt));
        command.Parameters.Add(new NpgsqlParameter<string>("version", BillingCatalog.Version));
        command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)requestContext.UserId ?? DBNull.Value });
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            return;
        }

        Invalidate();
        await audit.RecordAsync(
            new AuditEvent(BillingAuditActions.TrialStarted, "tenant_subscription", id, new AuditChanges()
                .Value("plan", BillingCatalog.TrialPlan.Code)
                .Value("trialEndsAt", endsAt)
                .Value("catalogVersion", BillingCatalog.Version)
                .ToDictionary()),
            cancellationToken);
    }

    /// <summary>The catalog suggestion, dropped when the suggested module is already on (nothing more to offer).</summary>
    private EntitlementSuggestion Suggest(string code)
    {
        var suggestion = BillingCatalog.Suggest(code);
        if (suggestion.Plan is { } plan && _resolved?.Snapshot.Plan == plan)
        {
            return EntitlementSuggestion.None;
        }

        return suggestion.Module is { } module && _resolved?.Modules.Any(m => m.ModuleCode == module) == true
            ? EntitlementSuggestion.None
            : suggestion;
    }
}

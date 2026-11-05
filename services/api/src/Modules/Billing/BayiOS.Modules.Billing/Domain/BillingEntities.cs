using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Billing.Domain;

/// <summary>
/// <c>tenant_subscriptions.status</c>. <c>trialing</c>: the trial bundle applies until <c>trial_ends_at</c>;
/// <c>active</c>: the stored plan applies (until <c>current_period_end</c> when set); <c>expired</c> / <c>cancelled</c>:
/// the fallback plan (Basic) applies. Nothing is deleted in any state (PRD SUB-06).
/// </summary>
internal static class SubscriptionStatuses
{
    public const string Trialing = "trialing";
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Trialing, Active, Expired, Cancelled];
}

/// <summary>
/// <c>tenant_modules.status</c>. <c>trialing</c>: free activation until <c>expires_at</c> (this phase has no payment);
/// <c>active</c>: paid activation (reserved — only reachable once billing exists); <c>expired</c>: the trial ran out;
/// <c>cancelled</c>: switched off by the tenant (may be switched on again until the original <c>expires_at</c>).
/// </summary>
internal static class ModuleStatuses
{
    public const string Trialing = "trialing";
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Trialing, Active, Expired, Cancelled];
}

internal abstract class BillingEntity : ITenantOwned
{
    public Guid Id { get; protected init; }

    public Guid TenantId { get; protected init; }

    public long RowVersion { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; protected init; }

    public Guid? CreatedBy { get; protected init; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Touch(Guid? userId, DateTimeOffset now)
    {
        RowVersion++;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>billing.tenant_subscriptions (data-model §5.2): one per tenant.</summary>
internal sealed class TenantSubscription : BillingEntity
{
    private TenantSubscription()
    {
    }

    public string PlanCode { get; set; } = BillingCatalog.Basic;

    public string Status { get; set; } = SubscriptionStatuses.Trialing;

    public DateTimeOffset? TrialStartedAt { get; private init; }

    public DateTimeOffset? TrialEndsAt { get; private init; }

    public DateTimeOffset CurrentPeriodStart { get; set; }

    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary><see cref="BillingCatalog.Version"/> at the last change.</summary>
    public string CatalogVersion { get; set; } = BillingCatalog.Version;

    /// <summary>Payment provider (none in this phase; owner approval required).</summary>
    public string? Provider { get; private init; }

    public string? ProviderRef { get; private init; }

    public SubscriptionFacts Facts => new(PlanCode, Status, TrialEndsAt, CurrentPeriodEnd);

    /// <summary>The trial is over: the fallback plan applies from its end (data is kept; PRD SUB-05/SUB-06).</summary>
    public void ExpireTrial(DateTimeOffset now)
    {
        PlanCode = BillingCatalog.FallbackPlan.Code;
        Status = SubscriptionStatuses.Expired;
        CurrentPeriodStart = TrialEndsAt ?? now;
        CurrentPeriodEnd = null;
        CatalogVersion = BillingCatalog.Version;
        Touch(null, now);
    }
}

/// <summary>billing.tenant_modules: add-on activations of a tenant. UQ (tenant_id, module_code).</summary>
internal sealed class TenantModule : BillingEntity
{
    private TenantModule()
    {
    }

    public string ModuleCode { get; private init; } = string.Empty;

    public string Status { get; set; } = ModuleStatuses.Trialing;

    public DateTimeOffset StartedAt { get; private init; }

    /// <summary>End of the free activation; null only for a paid (<c>active</c>) activation.</summary>
    public DateTimeOffset? ExpiresAt { get; private init; }

    public DateTimeOffset? DeactivatedAt { get; set; }

    public Guid? DeactivatedBy { get; set; }

    public static TenantModule StartTrial(Guid tenantId, string moduleCode, DateTimeOffset expiresAt, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ModuleCode = moduleCode,
        Status = ModuleStatuses.Trialing,
        StartedAt = now,
        ExpiresAt = expiresAt,
        CreatedAt = now,
        CreatedBy = userId,
    };

    public ModuleFacts Facts => new(ModuleCode, Status, ExpiresAt);
}

/// <summary>
/// billing.tenant_entitlements (data-model §5.2, SUB-08): operator overrides on top of plan + add-ons — a feature
/// switched on/off (<see cref="Enabled"/>) or a limit replaced (<see cref="LimitValue"/>; null = unlimited). Written only
/// by operators through the migrator role (no API in this phase); the API reads them.
/// </summary>
internal sealed class EntitlementOverride : ITenantOwned
{
    private EntitlementOverride()
    {
    }

    public Guid Id { get; private init; }

    public Guid TenantId { get; private init; }

    public string FeatureCode { get; private init; } = string.Empty;

    /// <summary>Feature codes: on/off. Ignored for limit codes.</summary>
    public bool Enabled { get; private init; }

    /// <summary>Limit codes: the replacement value (null = unlimited). Ignored for feature codes.</summary>
    public long? LimitValue { get; private init; }

    public string Source { get; private init; } = "override";

    public string? Reason { get; private init; }

    public DateTimeOffset? ExpiresAt { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }
}

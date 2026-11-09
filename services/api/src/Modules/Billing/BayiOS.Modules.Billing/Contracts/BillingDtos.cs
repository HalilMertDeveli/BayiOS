namespace BayiOS.Modules.Billing.Contracts;

/// <param name="Value">null = unlimited.</param>
public sealed record LimitValueResponse(string Code, long? Value);

public sealed record PlanResponse(string Code, string Name, string Description, IReadOnlyList<string> Features, IReadOnlyList<LimitValueResponse> Limits);

/// <param name="Available">false: announced, not built yet — cannot be activated.</param>
public sealed record CatalogModuleResponse(
    string Code,
    string Name,
    string Description,
    bool Available,
    IReadOnlyList<string> Features,
    IReadOnlyList<LimitValueResponse> Limits);

/// <summary>The versioned plan / add-on catalog (no prices: there is no payment in this phase).</summary>
/// <param name="TrialPlan">The plan a new tenant tries, together with every add-on, for <paramref name="TrialDays"/> days.</param>
/// <param name="FallbackPlan">The plan that applies when a trial or subscription ends.</param>
/// <param name="ModuleTrialDays">Length of the free activation of one add-on.</param>
public sealed record BillingCatalogResponse(
    string CatalogVersion,
    string TrialPlan,
    string FallbackPlan,
    int TrialDays,
    int ModuleTrialDays,
    IReadOnlyList<PlanResponse> Plans,
    IReadOnlyList<CatalogModuleResponse> Modules);

/// <summary>An add-on as the active tenant has it.</summary>
/// <param name="Status">
/// <c>included_in_trial</c> (the tenant trial grants it), <c>trialing</c> (free activation), <c>active</c> (paid; not
/// reachable yet), <c>cancelled</c> (switched off; can be switched on until <paramref name="ExpiresAt"/>), <c>expired</c>
/// (free activation used up), <c>available</c> (never activated), <c>unavailable</c> (not built yet).
/// </param>
/// <param name="ExpiresAt">When the current grant ends (trial end / activation end); null when none or open-ended.</param>
public sealed record TenantModuleResponse(
    string Code,
    string Name,
    bool Available,
    string Status,
    DateTimeOffset? ExpiresAt,
    bool CanActivate,
    bool CanDeactivate);

public sealed record TenantModuleListResponse(IReadOnlyList<TenantModuleResponse> Items);

/// <summary>The active tenant's effective subscription and entitlements.</summary>
/// <param name="Plan">The plan that applies now (the trial plan while <c>trialing</c>).</param>
/// <param name="Status"><c>trialing</c>, <c>active</c>, <c>expired</c> (trial/period over: fallback plan) or <c>cancelled</c>.</param>
/// <param name="TrialEndsAt">Only while <c>trialing</c>.</param>
public sealed record SubscriptionResponse(
    string Plan,
    string PlanName,
    string Status,
    DateTimeOffset? TrialEndsAt,
    string CatalogVersion,
    IReadOnlyList<string> Features,
    IReadOnlyList<LimitValueResponse> Limits,
    IReadOnlyList<TenantModuleResponse> Modules);

/// <param name="Limit">null = unlimited.</param>
/// <param name="Remaining">null = unlimited; never negative (a downgrade can leave more in use than the limit).</param>
/// <param name="Period"><c>month</c> for monthly counters (Europe/Istanbul calendar month), otherwise null (current total).</param>
public sealed record UsageItemResponse(string Code, long? Limit, long Current, long? Remaining, string? Period);

public sealed record UsageResponse(string Plan, string Status, IReadOnlyList<UsageItemResponse> Items);

/// <summary>
/// Current usage per limit code of the active tenant (provided by the host from the owning modules: warehouses,
/// members, AI usage). Codes it does not report are shown as 0.
/// </summary>
public interface IBillingUsageSource
{
    Task<IReadOnlyDictionary<string, long>> CurrentAsync(CancellationToken cancellationToken);
}

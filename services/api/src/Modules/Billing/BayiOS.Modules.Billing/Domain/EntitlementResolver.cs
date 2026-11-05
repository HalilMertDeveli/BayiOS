using BayiOS.SharedKernel.Entitlements;

namespace BayiOS.Modules.Billing.Domain;

/// <summary>The stored subscription facts the resolver needs.</summary>
internal sealed record SubscriptionFacts(string PlanCode, string Status, DateTimeOffset? TrialEndsAt, DateTimeOffset? CurrentPeriodEnd);

/// <summary>An add-on row of the tenant.</summary>
internal sealed record ModuleFacts(string ModuleCode, string Status, DateTimeOffset? ExpiresAt);

/// <summary>An operator override row.</summary>
internal sealed record OverrideFacts(string Code, bool Enabled, long? LimitValue, DateTimeOffset? ExpiresAt);

/// <summary>Where each effective module comes from.</summary>
internal sealed record ResolvedModule(string ModuleCode, string Source, DateTimeOffset? Until);

internal sealed record ResolvedEntitlements(EntitlementSnapshot Snapshot, IReadOnlyList<ResolvedModule> Modules);

/// <summary>
/// Pure entitlement rules (no I/O; unit tested). Order: effective plan (trial / active / expired → fallback) → active
/// add-ons (and, during the trial, every add-on) → operator overrides. Time is an input, so expiry never depends on a
/// background job having run: a trial whose end has passed is Basic at that instant. Limits merge by "higher wins"
/// (null = unlimited); an override replaces the value. Unknown plan codes fall back to Basic (fail-closed).
/// </summary>
internal static class EntitlementResolver
{
    public const string SourceTrial = "trial";
    public const string SourceModule = "module";

    public static ResolvedEntitlements Resolve(
        SubscriptionFacts subscription,
        IReadOnlyCollection<ModuleFacts> modules,
        IReadOnlyCollection<OverrideFacts> overrides,
        DateTimeOffset now)
    {
        var (plan, status, trialEndsAt) = EffectivePlan(subscription, now);
        var features = new HashSet<string>(plan.Features, StringComparer.Ordinal);
        var limits = new Dictionary<string, long?>(plan.Limits, StringComparer.Ordinal);
        var resolved = new List<ResolvedModule>();

        foreach (var module in BillingCatalog.Modules)
        {
            ResolvedModule? on = null;
            if (status == SubscriptionStatuses.Trialing)
            {
                on = new ResolvedModule(module.Code, SourceTrial, trialEndsAt);
            }

            var row = modules.FirstOrDefault(m => m.ModuleCode == module.Code);
            if (row is not null && IsOn(row, now) && (on is null || Later(row.ExpiresAt, on.Until)))
            {
                on = new ResolvedModule(module.Code, SourceModule, row.ExpiresAt);
            }

            if (on is null)
            {
                continue;
            }

            resolved.Add(on);
            features.UnionWith(module.Features);
            foreach (var (code, value) in module.Limits)
            {
                if (!limits.TryGetValue(code, out var current) || BillingCatalog.IsHigher(value, current))
                {
                    limits[code] = value;
                }
            }
        }

        foreach (var item in overrides.Where(o => o.ExpiresAt is null || o.ExpiresAt > now))
        {
            if (EntitlementCodes.Limits.Contains(item.Code))
            {
                limits[item.Code] = item.LimitValue;
            }
            else if (item.Enabled)
            {
                features.Add(item.Code);
            }
            else
            {
                features.Remove(item.Code);
            }
        }

        var snapshot = new EntitlementSnapshot(
            plan.Code,
            status,
            status == SubscriptionStatuses.Trialing ? trialEndsAt : null,
            BillingCatalog.Version,
            new SortedSet<string>(features, StringComparer.Ordinal),
            new SortedDictionary<string, long?>(limits, StringComparer.Ordinal));
        return new ResolvedEntitlements(snapshot, resolved);
    }

    /// <summary>The plan that applies at <paramref name="now"/> and the effective status.</summary>
    public static (PlanDefinition Plan, string Status, DateTimeOffset? TrialEndsAt) EffectivePlan(SubscriptionFacts subscription, DateTimeOffset now)
    {
        switch (subscription.Status)
        {
            case SubscriptionStatuses.Trialing when subscription.TrialEndsAt > now:
                return (BillingCatalog.TrialPlan, SubscriptionStatuses.Trialing, subscription.TrialEndsAt);
            case SubscriptionStatuses.Trialing:
                return (BillingCatalog.FallbackPlan, SubscriptionStatuses.Expired, null);
            case SubscriptionStatuses.Active when subscription.CurrentPeriodEnd is null || subscription.CurrentPeriodEnd > now:
                return (BillingCatalog.FindPlan(subscription.PlanCode) ?? BillingCatalog.FallbackPlan, SubscriptionStatuses.Active, null);
            case SubscriptionStatuses.Cancelled:
                return (BillingCatalog.FallbackPlan, SubscriptionStatuses.Cancelled, null);
            default:
                return (BillingCatalog.FallbackPlan, SubscriptionStatuses.Expired, null);
        }
    }

    public static bool IsOn(ModuleFacts module, DateTimeOffset now) =>
        (module.Status == ModuleStatuses.Trialing && module.ExpiresAt > now)
        || (module.Status == ModuleStatuses.Active && (module.ExpiresAt is null || module.ExpiresAt > now));

    /// <summary>null (open-ended) is the latest.</summary>
    private static bool Later(DateTimeOffset? candidate, DateTimeOffset? current) =>
        current is not null && (candidate is null || candidate > current);
}

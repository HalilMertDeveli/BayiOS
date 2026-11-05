using BayiOS.Modules.Billing.Domain;
using BayiOS.SharedKernel.Entitlements;

namespace BayiOS.UnitTests.Billing;

/// <summary>#51: the pure entitlement rules — plan, trial, expiry, add-ons, overrides, suggestions — and catalog integrity.</summary>
public class EntitlementResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static SubscriptionFacts Trial(DateTimeOffset ends) => new(BillingCatalog.Professional, SubscriptionStatuses.Trialing, ends, null);

    private static SubscriptionFacts Active(string plan, DateTimeOffset? periodEnd = null) => new(plan, SubscriptionStatuses.Active, null, periodEnd);

    private static EntitlementSnapshot Resolve(SubscriptionFacts subscription, ModuleFacts[]? modules = null, OverrideFacts[]? overrides = null) =>
        EntitlementResolver.Resolve(subscription, modules ?? [], overrides ?? [], Now).Snapshot;

    [Fact]
    public void Trial_grants_professional_and_every_add_on_until_it_ends()
    {
        var running = Resolve(Trial(Now.AddDays(3)));
        Assert.Equal((BillingCatalog.Professional, SubscriptionStatuses.Trialing, (DateTimeOffset?)Now.AddDays(3)), (running.Plan, running.Status, running.TrialEndsAt));
        Assert.True(running.Has(EntitlementCodes.WarehouseLocations));
        Assert.True(running.Has(EntitlementCodes.AiOrderParsing));
        Assert.Null(running.LimitOf(EntitlementCodes.MaxWarehouses));
        Assert.Equal(1000, running.LimitOf(EntitlementCodes.AiParsesPerMonth));

        // The end instant itself is already over (fail-closed), without any job having run.
        var ended = Resolve(Trial(Now));
        Assert.Equal((BillingCatalog.Basic, SubscriptionStatuses.Expired, (DateTimeOffset?)null), (ended.Plan, ended.Status, ended.TrialEndsAt));
        Assert.Empty(ended.Features);
        Assert.Equal(1, ended.LimitOf(EntitlementCodes.MaxWarehouses));
        Assert.Equal(0, ended.LimitOf(EntitlementCodes.AiParsesPerMonth));
    }

    [Theory]
    [InlineData(BillingCatalog.Basic, false, 1L)]
    [InlineData(BillingCatalog.Professional, true, null)]
    [InlineData("platinum", false, 1L)] // unknown plan → fallback (fail-closed)
    public void Active_plan_applies_its_features_and_limits(string plan, bool locations, long? maxWarehouses)
    {
        var snapshot = Resolve(Active(plan));
        Assert.Equal(locations, snapshot.Has(EntitlementCodes.WarehouseLocations));
        Assert.Equal(maxWarehouses, snapshot.LimitOf(EntitlementCodes.MaxWarehouses));
        Assert.False(snapshot.Has(EntitlementCodes.AiOrderParsing));
    }

    [Theory]
    [InlineData(SubscriptionStatuses.Expired)]
    [InlineData(SubscriptionStatuses.Cancelled)]
    public void Ended_subscriptions_fall_back_to_basic(string status)
    {
        var snapshot = Resolve(new SubscriptionFacts(BillingCatalog.Professional, status, null, null));
        Assert.Equal((BillingCatalog.Basic, status), (snapshot.Plan, snapshot.Status));
        Assert.False(snapshot.Has(EntitlementCodes.WarehouseLocations));
    }

    [Fact]
    public void Active_period_end_is_honoured()
    {
        Assert.Equal(BillingCatalog.Professional, Resolve(Active(BillingCatalog.Professional, Now.AddMinutes(1))).Plan);
        Assert.Equal(BillingCatalog.Basic, Resolve(Active(BillingCatalog.Professional, Now)).Plan);
    }

    [Theory]
    [InlineData(ModuleStatuses.Trialing, 1, true)]
    [InlineData(ModuleStatuses.Trialing, -1, false)]
    [InlineData(ModuleStatuses.Cancelled, 1, false)]
    [InlineData(ModuleStatuses.Expired, 1, false)]
    [InlineData(ModuleStatuses.Active, null, true)]
    public void Add_on_is_on_only_while_trialing_or_active_and_unexpired(string status, int? expiresInDays, bool on)
    {
        var module = new ModuleFacts(BillingCatalog.AiAssistantModule, status, expiresInDays is { } days ? Now.AddDays(days) : null);
        var snapshot = Resolve(Active(BillingCatalog.Basic), [module]);
        Assert.Equal(on, snapshot.Has(EntitlementCodes.AiOrderParsing));
        Assert.Equal(on ? 1000 : 0, snapshot.LimitOf(EntitlementCodes.AiParsesPerMonth));
        Assert.False(snapshot.Has(EntitlementCodes.WarehouseLocations)); // an add-on never grants plan features
    }

    [Fact]
    public void Overrides_switch_features_and_replace_limits_until_they_expire()
    {
        var snapshot = Resolve(
            Active(BillingCatalog.Basic),
            overrides:
            [
                new OverrideFacts(EntitlementCodes.WarehouseLocations, true, null, null),
                new OverrideFacts(EntitlementCodes.MaxWarehouses, false, 3, null),
                new OverrideFacts(EntitlementCodes.MaxUsers, false, null, null),
                new OverrideFacts(EntitlementCodes.BarcodeOperations, true, null, Now), // expired at this instant
            ]);
        Assert.True(snapshot.Has(EntitlementCodes.WarehouseLocations));
        Assert.False(snapshot.Has(EntitlementCodes.BarcodeOperations));
        Assert.Equal(3, snapshot.LimitOf(EntitlementCodes.MaxWarehouses));
        Assert.Null(snapshot.LimitOf(EntitlementCodes.MaxUsers));

        var off = Resolve(Trial(Now.AddDays(1)), overrides: [new OverrideFacts(EntitlementCodes.AiOrderParsing, false, null, null)]);
        Assert.False(off.Has(EntitlementCodes.AiOrderParsing));
    }

    [Fact]
    public void Unknown_limit_codes_are_zero()
    {
        Assert.Equal(0, Resolve(Active(BillingCatalog.Professional)).LimitOf("max_rockets"));
    }

    [Theory]
    [InlineData(EntitlementCodes.WarehouseLocations, BillingCatalog.Professional, null)]
    [InlineData(EntitlementCodes.MaxWarehouses, BillingCatalog.Professional, null)]
    [InlineData(EntitlementCodes.MaxUsers, BillingCatalog.Professional, null)]
    [InlineData(EntitlementCodes.AiOrderParsing, null, BillingCatalog.AiAssistantModule)]
    [InlineData(EntitlementCodes.AiParsesPerMonth, null, BillingCatalog.AiAssistantModule)]
    [InlineData(EntitlementCodes.DealerPortal, null, null)] // announced, not available
    public void Suggestions_name_the_cheapest_available_grant(string code, string? plan, string? module) =>
        Assert.Equal(new EntitlementSuggestion(plan, module), BillingCatalog.Suggest(code));

    [Fact]
    public void Catalog_only_uses_known_codes_and_plans_are_ordered()
    {
        var features = EntitlementCodes.Features.ToHashSet();
        var limits = EntitlementCodes.Limits.ToHashSet();
        foreach (var plan in BillingCatalog.Plans)
        {
            Assert.Subset(features, plan.Features.ToHashSet());
            Assert.Subset(limits, plan.Limits.Keys.ToHashSet());
            Assert.Contains(EntitlementCodes.MaxWarehouses, plan.Limits.Keys);
        }

        foreach (var module in BillingCatalog.Modules)
        {
            Assert.Subset(features, module.Features.ToHashSet());
            Assert.Subset(limits, module.Limits.Keys.ToHashSet());
        }

        // Every feature has a grant somewhere; Professional includes everything Basic has.
        Assert.All(features, f => Assert.True(
            BillingCatalog.Plans.Any(p => p.Features.Contains(f)) || BillingCatalog.Modules.Any(m => m.Features.Contains(f)), f));
        Assert.Subset(BillingCatalog.ProfessionalPlan.Features.ToHashSet(), BillingCatalog.BasicPlan.Features.ToHashSet());
        Assert.Equal(BillingCatalog.Plans.Count, BillingCatalog.Plans.Select(p => p.Code).Distinct().Count());
        Assert.Equal(BillingCatalog.Modules.Count, BillingCatalog.Modules.Select(m => m.Code).Distinct().Count());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}\.\d+$", BillingCatalog.Version);
    }

    [Fact]
    public void Denials_carry_the_problem_extensions()
    {
        var limit = new EntitlementException(EntitlementProblems.LimitExceeded, EntitlementCodes.MaxWarehouses, new EntitlementSuggestion("professional", null), 1, 1)
            .ToProblem();
        Assert.Equal(403, limit.Status);
        Assert.Equal(
            (EntitlementProblems.LimitExceeded, EntitlementCodes.MaxWarehouses, "professional", (object?)1L, (object?)1L),
            ((string)limit.Extensions["code"]!, (string)limit.Extensions["feature"]!, (string)limit.Extensions["suggestedPlan"]!, limit.Extensions["limit"], limit.Extensions["current"]));

        var required = new EntitlementException(EntitlementProblems.Required, EntitlementCodes.AiOrderParsing, EntitlementSuggestion.None).ToProblem();
        Assert.False(required.Extensions.ContainsKey("limit"));
        Assert.Null(required.Extensions["suggestedModule"]);
    }
}

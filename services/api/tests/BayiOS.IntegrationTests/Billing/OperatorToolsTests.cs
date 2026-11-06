using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;

namespace BayiOS.IntegrationTests.Billing;

/// <summary>
/// #129 item 7: <c>infra/ops/shorten-trial.sql</c>, the non-HTTP UAT tool that ends one tenant's trial. Run with the
/// real psql in the database container, as the RLS-bound runtime role.
/// </summary>
public sealed class OperatorToolsTests(ApiFixture fixture)
{
    private const string Script = "infra/ops/shorten-trial.sql";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shorten_trial_ends_one_tenants_trial_through_rls_and_audits_it()
    {
        var target = await CatalogClient.NewTenantOwnerAsync(fixture);
        var bystander = await CatalogClient.NewTenantOwnerAsync(fixture);
        Assert.Equal("trialing", (await BillingKit.SubscriptionAsync(target)).Status);

        // A UAT tenant that has been on its trial for a day (the end can never precede the start + 1 s).
        await BillingKit.ExecuteAsync(
            fixture,
            "UPDATE billing.tenant_subscriptions SET trial_started_at = trial_started_at - interval '1 day', current_period_start = current_period_start - interval '1 day' WHERE tenant_id = $1",
            target.TenantId);

        var run = await fixture.PsqlAsync(Script, "bayios_app", new Dictionary<string, string> { ["tenant_id"] = target.TenantId.ToString() }, Ct);
        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Contains("trialing", run.Stdout, StringComparison.Ordinal);

        // Effective at once (entitlements are derived per request); the bystander keeps its trial.
        var ended = await BillingKit.SubscriptionAsync(target);
        Assert.Equal(("basic", "expired"), (ended.Plan, ended.Status));
        Assert.Equal("trialing", (await BillingKit.SubscriptionAsync(bystander)).Status);
        Assert.Equal(1L, await BillingKit.ScalarAsync<long>(
            fixture,
            "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'billing.trial_shortened' AND actor_role = 'operator'",
            target.TenantId));
        Assert.Equal(0L, await BillingKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'billing.trial_shortened'", bystander.TenantId));

        // The regular expiry job then records the end as usual; re-running the tool changes nothing.
        await BillingKit.RunExpiryJobAsync(fixture);
        Assert.Equal("expired", await BillingKit.ScalarAsync<string>(fixture, "SELECT status FROM billing.tenant_subscriptions WHERE tenant_id = $1", target.TenantId));
        var again = await fixture.PsqlAsync(Script, "bayios_app", new Dictionary<string, string> { ["tenant_id"] = target.TenantId.ToString() }, Ct);
        Assert.True(again.ExitCode == 0, again.Stderr);
        Assert.Equal(1L, await BillingKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM audit.audit_events WHERE tenant_id = $1 AND action = 'billing.trial_shortened'", target.TenantId));
    }

    [Fact]
    public async Task Shorten_trial_only_shortens_and_refuses_other_roles_and_missing_input()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var before = await BillingKit.ScalarAsync<DateTime>(fixture, "SELECT trial_ends_at FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId);

        // Later than today's end: nothing changes.
        var later = await fixture.PsqlAsync(
            Script, "bayios_app", new Dictionary<string, string> { ["tenant_id"] = owner.TenantId.ToString(), ["ends_at"] = "2099-01-01T00:00:00Z" }, Ct);
        Assert.True(later.ExitCode == 0, later.Stderr);
        Assert.Equal(before, await BillingKit.ScalarAsync<DateTime>(fixture, "SELECT trial_ends_at FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId));

        // A chosen earlier end (e.g. tomorrow) is applied as given.
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);
        var shorter = await fixture.PsqlAsync(
            Script, "bayios_app", new Dictionary<string, string> { ["tenant_id"] = owner.TenantId.ToString(), ["ends_at"] = tomorrow.ToString("O") }, Ct);
        Assert.True(shorter.ExitCode == 0, shorter.Stderr);
        Assert.Equal("trialing", (await BillingKit.SubscriptionAsync(owner)).Status);
        var ends = await BillingKit.ScalarAsync<DateTime>(fixture, "SELECT trial_ends_at FROM billing.tenant_subscriptions WHERE tenant_id = $1", owner.TenantId);
        Assert.InRange(ends, tomorrow.UtcDateTime.AddSeconds(-1), tomorrow.UtcDateTime.AddSeconds(1));

        // Not the runtime role: refused before any change.
        var migrator = await fixture.PsqlAsync(Script, "bayios_migrator", new Dictionary<string, string> { ["tenant_id"] = owner.TenantId.ToString() }, Ct);
        Assert.NotEqual(0, migrator.ExitCode);
        Assert.Contains("run this script as bayios_app", migrator.Stderr, StringComparison.Ordinal);

        // No tenant: usage only.
        var usage = await fixture.PsqlAsync(Script, "bayios_app", new Dictionary<string, string>(), Ct);
        Assert.Equal(0, usage.ExitCode);
        Assert.Contains("usage:", usage.Stdout + usage.Stderr, StringComparison.Ordinal);
        Assert.Equal("trialing", (await BillingKit.SubscriptionAsync(owner)).Status);
    }
}

using System.Net;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Billing;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Jobs;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BayiOS.IntegrationTests.Billing;

/// <summary>
/// Puts a tenant on an explicit plan / state for tests (superuser SQL — the API has no plan change without payment)
/// and drives the module store and the expiry job. Synthetic data only.
/// </summary>
internal static class BillingKit
{
    public const string Basic = "basic";
    public const string Professional = "professional";
    public const string AiModule = "ai_assistant";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The tenant is on <paramref name="plan"/> (status active, trial over, no add-ons switched on by this call).</summary>
    public static Task SetPlanAsync(ApiFixture fixture, Guid tenantId, string plan) =>
        ExecuteAsync(
            fixture,
            """
            UPDATE billing.tenant_subscriptions
            SET plan_code = $2, status = 'active', trial_ends_at = least(trial_ends_at, now()), current_period_start = now(),
                current_period_end = NULL, row_version = row_version + 1
            WHERE tenant_id = $1
            """,
            tenantId,
            plan);

    /// <summary>Moves the tenant's trial into the past (as if 31 days had gone by) without running the expiry job.</summary>
    public static Task EndTrialAsync(ApiFixture fixture, Guid tenantId) =>
        ExecuteAsync(
            fixture,
            """
            UPDATE billing.tenant_subscriptions
            SET trial_started_at = now() - interval '31 days', trial_ends_at = now() - interval '1 minute',
                current_period_start = now() - interval '31 days'
            WHERE tenant_id = $1 AND status = 'trialing'
            """,
            tenantId);

    /// <summary>Operator override (SUB-08): a limit value (null = unlimited) or a feature switch.</summary>
    public static Task OverrideAsync(ApiFixture fixture, Guid tenantId, string code, bool enabled = true, long? limit = null) =>
        ExecuteAsync(
            fixture,
            """
            INSERT INTO billing.tenant_entitlements (id, tenant_id, feature_code, enabled, limit_value, reason, created_at)
            VALUES ($1, $2, $3, $4, $5, 'test', now())
            ON CONFLICT (tenant_id, feature_code) DO UPDATE SET enabled = excluded.enabled, limit_value = excluded.limit_value
            """,
            Guid.CreateVersion7(),
            tenantId,
            code,
            enabled,
            limit.HasValue ? limit.Value : (object)DBNull.Value);

    public static async Task<TenantModuleResponse> ActivateAsync(CatalogClient owner, string module = AiModule) =>
        await owner.OkAsync<TenantModuleResponse>(await owner.PostAsync($"/api/v1/billing/modules/{module}/activate", new { }));

    public static async Task<SubscriptionResponse> SubscriptionAsync(CatalogClient member) =>
        await member.OkAsync<SubscriptionResponse>(await member.GetAsync("/api/v1/billing/subscription"));

    /// <summary>Runs the tenant-less expiry job once, like the maintenance worker.</summary>
    public static async Task RunExpiryJobAsync(ApiFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
        await unitOfWork.BeginAsync(readOnly: false, Ct);
        var job = scope.ServiceProvider.GetServices<JobHandlerRegistration>().Single(r => r.Type == BillingModule.ExpiryJobType);
        var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(job.HandlerType);
        await handler.HandleAsync(new JobExecution(Guid.NewGuid(), job.Type, null, default, 1, 1, context.CorrelationId), Ct);
        await unitOfWork.CommitAsync(Ct);
    }

    public static async Task<T> ScalarAsync<T>(ApiFixture fixture, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public static async Task ExecuteAsync(ApiFixture fixture, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, string code, string feature)
    {
        var problem = await HttpAssert.ProblemJsonAsync(response, HttpStatusCode.Forbidden, code);
        Assert.Equal(feature, problem.GetProperty("feature").GetString());
    }
}

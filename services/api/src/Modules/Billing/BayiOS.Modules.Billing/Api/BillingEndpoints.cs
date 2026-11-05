using BayiOS.Modules.Billing.Application;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Billing.Api;

/// <summary>
/// Billing & entitlements / module store (api-strategy §12.2, PRD SUB-04, #51/#52). The catalog needs only a signed-in
/// user; the tenant's subscription, modules and usage need <c>billing.read</c> (owner, admin); activating or
/// deactivating an add-on needs <c>billing.manage</c> (owner) and an Idempotency-Key. No payment exists in this phase.
/// </summary>
internal static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing")
            .WithTags("Billing")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // Static catalog: no tenant, no database.
        group.MapGet("/plans", Catalog).WithName("Billing_Plans")
            .WithSummary("Paket ve ek modül kataloğu (sürümlü; fiyat yok)");

        var tenant = group.MapGroup(string.Empty)
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        tenant.MapGet("/subscription", Subscription).WithName("Billing_Subscription")
            .WithSummary("Aktif tenant'ın etkin paketi, durumu (deneme/aktif/süresi doldu), özellikleri, limitleri ve modülleri")
            .RequirePermission(Permissions.BillingRead);
        tenant.MapGet("/modules", Modules).WithName("Billing_Modules")
            .WithSummary("Modül mağazası: ek modüller ve tenant'taki durumları")
            .RequirePermission(Permissions.BillingRead);
        tenant.MapGet("/usage", Usage).WithName("Billing_Usage")
            .WithSummary("Limitlere göre güncel kullanım (depo, kullanıcı, aylık AI ayrıştırma)")
            .RequirePermission(Permissions.BillingRead);
        Transition(tenant.MapPost("/modules/{code}/activate", Activate).WithName("Billing_ActivateModule")
            .WithSummary("Ek modülü ücretsiz deneme olarak etkinleştirir (ödeme yok; modül başına bir kez, denetim kaydı)")
            .WithDescription(
                "Starts the free activation of an add-on for the configured number of days (during the tenant trial it starts "
                + "when the trial ends). Switched-off modules can be switched on again until that date. 409 "
                + "`billing.module_unavailable` (not built yet), `billing.module_already_active`, `billing.trial_used` (free "
                + "activation used up; paid activation does not exist yet)."));
        Transition(tenant.MapPost("/modules/{code}/deactivate", Deactivate).WithName("Billing_DeactivateModule")
            .WithSummary("Ek modülü kapatır; veriler silinmez (denetim kaydı)")
            .WithDescription(
                "Existing records stay readable; only new use of the feature is refused. 409 `billing.module_not_active` "
                + "(not activated, or only included in the tenant trial)."));
        return app;
    }

    private static void Transition(RouteHandlerBuilder builder) => builder
        .RequirePermission(Permissions.BillingManage)
        .RequireIdempotencyKey()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

    private static Ok<BillingCatalogResponse> Catalog(IOptions<BillingOptions> options) => TypedResults.Ok(BillingService.Catalog(options.Value));

    private static async Task<Ok<SubscriptionResponse>> Subscription(BillingService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.SubscriptionAsync(cancellationToken));

    private static async Task<Ok<TenantModuleListResponse>> Modules(BillingService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.ListModulesAsync(cancellationToken));

    private static async Task<Ok<UsageResponse>> Usage(BillingService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.UsageAsync(cancellationToken));

    private static async Task<Results<Ok<TenantModuleResponse>, ProblemHttpResult>> Activate(
        string code, BillingService service, CancellationToken cancellationToken) =>
        ToResult(await service.ActivateAsync(code, cancellationToken));

    private static async Task<Results<Ok<TenantModuleResponse>, ProblemHttpResult>> Deactivate(
        string code, BillingService service, CancellationToken cancellationToken) =>
        ToResult(await service.DeactivateAsync(code, cancellationToken));

    private static Results<Ok<TenantModuleResponse>, ProblemHttpResult> ToResult(AppResult<TenantModuleResponse> result) =>
        result.Error is { } error
            ? TypedResults.Problem(ApiProblems.Create(error.Status, error.Code, error.Title))
            : TypedResults.Ok(result.Value!);
}

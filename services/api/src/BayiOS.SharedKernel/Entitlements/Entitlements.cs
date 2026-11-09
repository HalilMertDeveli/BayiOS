using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.SharedKernel.Entitlements;

/// <summary>
/// Stable feature and limit codes of the plan/add-on model (PRD SUB-01…SUB-09, roles-permissions §5). The Billing
/// module's versioned catalog decides which plan or add-on grants them; modules only name the code they need.
/// <para>Permission ≠ entitlement: a permission says "may this role do it", an entitlement "does this tenant have it".</para>
/// </summary>
public static class EntitlementCodes
{
    // ---- Feature flags ----

    /// <summary>Shelf/bin locations in a warehouse (PRD INV-04, Professional).</summary>
    public const string WarehouseLocations = "warehouse_locations";

    /// <summary>Barcode scanning / mobile picking (every plan since OD-02, 2026-11-09). Defined; no endpoint gates it.</summary>
    public const string BarcodeOperations = "barcode_operations";

    /// <summary>AI WhatsApp order assistant (add-on, PRD WAI, api-strategy §12.10).</summary>
    public const string AiOrderParsing = "ai_order_parsing";

    /// <summary>Dealer portal (add-on, outside the MVP).</summary>
    public const string DealerPortal = "dealer_portal";

    /// <summary>Serial number / warranty tracking (add-on, outside the MVP).</summary>
    public const string SerialWarranty = "serial_warranty";

    /// <summary>ERP integrations (add-on, outside the MVP).</summary>
    public const string ErpIntegrations = "erp_integrations";

    /// <summary>Official WhatsApp automation (add-on, outside the MVP; never sends without owner approval).</summary>
    public const string WhatsappAutomation = "whatsapp_automation";

    /// <summary>Advanced analytics (add-on, outside the MVP). Basic reports and every export stay ungated (REP-08).</summary>
    public const string AdvancedAnalytics = "advanced_analytics";

    public static readonly IReadOnlyList<string> Features =
    [
        WarehouseLocations, BarcodeOperations, AiOrderParsing, DealerPortal, SerialWarranty, ErpIntegrations, WhatsappAutomation,
        AdvancedAnalytics,
    ];

    // ---- Numeric limits (null = unlimited) ----

    /// <summary>Active warehouses (Basic: 999, OD-02; Professional: unlimited).</summary>
    public const string MaxWarehouses = "max_warehouses";

    /// <summary>User seats: active members plus open invitations; enforced on invite, resend, accept and re-enable (#129).</summary>
    public const string MaxUsers = "max_users";

    /// <summary>
    /// Products that are not archived (archiving frees a slot; restoring takes one). Enforced on product create and on
    /// restoring an archived product, interactive and import alike, when a limit is set (operator override); every plan is
    /// unlimited (OD-02, 2026-11-09).
    /// </summary>
    public const string MaxProducts = "max_products";

    /// <summary>WhatsApp parse requests per calendar month (Europe/Istanbul), on top of the hourly rate limit (WAI-11).</summary>
    public const string AiParsesPerMonth = "ai_parses_per_month";

    public static readonly IReadOnlyList<string> Limits = [MaxWarehouses, MaxUsers, MaxProducts, AiParsesPerMonth];
}

/// <summary>Stable 403 problem codes of the entitlement model (api-strategy §6).</summary>
public static class EntitlementProblems
{
    /// <summary>The tenant's plan/add-ons do not include the feature. Carries <c>feature</c> and a suggestion.</summary>
    public const string Required = "entitlement.required";

    /// <summary>One more would exceed a plan limit. Carries <c>feature</c> (the limit code), <c>limit</c> and <c>current</c>.</summary>
    public const string LimitExceeded = "entitlement.limit_exceeded";
}

/// <summary>What would unlock a feature or raise a limit: a plan, an add-on module, or nothing in the catalog.</summary>
public sealed record EntitlementSuggestion(string? Plan, string? Module)
{
    public static readonly EntitlementSuggestion None = new(null, null);
}

/// <summary>
/// The effective entitlements of the active tenant at one instant: plan + active add-ons + overrides, after trial and
/// expiry rules (Billing module). <see cref="Limits"/>: a null value is unlimited; a code that is absent is 0 (fail-closed).
/// </summary>
public sealed record EntitlementSnapshot(
    string Plan,
    string Status,
    DateTimeOffset? TrialEndsAt,
    string CatalogVersion,
    IReadOnlySet<string> Features,
    IReadOnlyDictionary<string, long?> Limits)
{
    public bool Has(string feature) => Features.Contains(feature);

    /// <summary>The limit value; null = unlimited; unknown codes are 0 (fail-closed).</summary>
    public long? LimitOf(string limit) => Limits.TryGetValue(limit, out var value) ? value : 0;
}

/// <summary>
/// Request-scoped entitlements of the active tenant (implemented by the Billing module). Endpoint gates use
/// <see cref="EntitlementConventions.RequireEntitlement"/>; services check limits with <see cref="EnsureCapacityAsync"/>,
/// whose <see cref="EntitlementException"/> the unit of work turns into a 403 problem and a rollback.
/// </summary>
public interface IEntitlements
{
    /// <summary>The active tenant's effective entitlements (resolved once per request).</summary>
    Task<EntitlementSnapshot> CurrentAsync(CancellationToken cancellationToken);

    /// <summary>Throws <see cref="EntitlementException"/> (<c>entitlement.required</c>) unless the feature is granted.</summary>
    Task EnsureFeatureAsync(string feature, CancellationToken cancellationToken);

    /// <summary>
    /// Throws <see cref="EntitlementException"/> (<c>entitlement.limit_exceeded</c>) when adding one more to
    /// <paramref name="current"/> would go over the limit. Callers serialize the count (lock) themselves.
    /// </summary>
    Task EnsureCapacityAsync(string limit, long current, CancellationToken cancellationToken);

    /// <summary>The <c>entitlement.required</c> denial for a feature, with the catalog's suggestion.</summary>
    EntitlementException Required(string feature);

    /// <summary>The <c>entitlement.limit_exceeded</c> denial for a limit, with the catalog's suggestion.</summary>
    EntitlementException LimitExceeded(string limit, long limitValue, long current);
}

/// <summary>Starts the plan trial of the tenant that is being created (called in the tenant creation transaction).</summary>
public interface ITenantSubscriptionProvisioner
{
    /// <summary>Idempotent: a tenant that already has a subscription keeps it. Requires a resolved tenant.</summary>
    Task StartTrialAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An entitlement denial (fail-closed). Thrown by services; the unit of work filter answers it with
/// <see cref="ToProblem"/> (403) and rolls the request back.
/// </summary>
public sealed class EntitlementException : Exception
{
    public EntitlementException(string code, string feature, EntitlementSuggestion suggestion, long? limit = null, long? current = null)
        : base(code == EntitlementProblems.LimitExceeded
            ? $"Limit '{feature}' reached ({current}/{limit})."
            : $"Feature '{feature}' is not included in the tenant's plan.")
    {
        Code = code;
        Feature = feature;
        Suggestion = suggestion;
        Limit = limit;
        Current = current;
    }

    public string Code { get; }

    /// <summary>The feature code (<c>entitlement.required</c>) or limit code (<c>entitlement.limit_exceeded</c>).</summary>
    public string Feature { get; }

    public EntitlementSuggestion Suggestion { get; }

    public long? Limit { get; }

    public long? Current { get; }

    public ProblemDetails ToProblem()
    {
        var limited = Code == EntitlementProblems.LimitExceeded;
        var problem = ApiProblems.Create(
            StatusCodes.Status403Forbidden,
            Code,
            limited ? "Plan limit reached" : "Not included in your plan",
            limited
                ? $"The limit '{Feature}' of your plan is {Limit}; {Current} already in use. Existing data stays available."
                : $"The feature '{Feature}' is not included in your plan. Existing data stays readable.");
        problem.Extensions["feature"] = Feature;
        problem.Extensions["suggestedPlan"] = Suggestion.Plan;
        problem.Extensions["suggestedModule"] = Suggestion.Module;
        if (limited)
        {
            problem.Extensions["limit"] = Limit;
            problem.Extensions["current"] = Current;
        }

        return problem;
    }
}

/// <summary>The endpoint needs the tenant feature <see cref="Feature"/> (emitted as <c>x-entitlement</c> in OpenAPI).</summary>
public sealed record EntitlementMetadata(string Feature);

/// <summary>The endpoint enforces the numeric limit <see cref="Limit"/> (emitted as <c>x-entitlement-limit</c>).</summary>
public sealed record EntitlementLimitMetadata(string Limit);

/// <summary>Endpoint requirement: the active tenant must have <see cref="Feature"/>.</summary>
public sealed record EntitlementRequirement(string Feature) : IAuthorizationRequirement;

/// <summary>
/// The entitlement gate. Fail-closed: no tenant, no <see cref="IEntitlements"/> implementation or a missing feature all
/// deny. Runs inside the request transaction that <c>TenantContextMiddleware</c> opened (RLS = the active tenant).
/// </summary>
public sealed class EntitlementAuthorizationHandler(IServiceProvider services, IRequestContext requestContext)
    : AuthorizationHandler<EntitlementRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, EntitlementRequirement requirement)
    {
        if (requestContext.TenantId is null || services.GetService<IEntitlements>() is not { } entitlements)
        {
            return;
        }

        var snapshot = await entitlements.CurrentAsync(CancellationToken.None);
        if (snapshot.Has(requirement.Feature))
        {
            context.Succeed(requirement);
            return;
        }

        // Not context.Fail(): an explicit failure hides which requirements failed, and the result handler must know
        // whether the permission passed (auth.forbidden first). The denial travels with the request instead.
        if (context.Resource is HttpContext http)
        {
            http.Items[DenialKey] = entitlements.Required(requirement.Feature);
        }
    }

    /// <summary><see cref="HttpContext.Items"/> key of the gate's <see cref="EntitlementException"/>.</summary>
    public static readonly object DenialKey = new();

    /// <summary>The gate's denial for the request when the entitlement requirement failed.</summary>
    public static EntitlementException? DenialOf(HttpContext context) => context.Items[DenialKey] as EntitlementException;
}

public static class EntitlementConventions
{
    /// <summary>
    /// Tenant-scoped endpoint that needs the tenant feature <paramref name="feature"/> in addition to its permission:
    /// 403 <c>entitlement.required</c> (with <c>feature</c>, <c>suggestedPlan</c> / <c>suggestedModule</c>) when the
    /// plan, add-ons and overrides do not grant it. A missing permission still answers <c>auth.forbidden</c> first.
    /// Never put this on a read of existing data or on an export (PRD SUB-06, REP-08).
    /// </summary>
    public static RouteHandlerBuilder RequireEntitlement(this RouteHandlerBuilder builder, string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new EntitlementRequirement(feature))
            .Build();
        return builder
            .WithMetadata(new TenantScopedMetadata(), new EntitlementMetadata(feature))
            .RequireAuthorization(policy);
    }

    /// <summary>Documents that the endpoint enforces the limit (403 <c>entitlement.limit_exceeded</c>, <c>x-entitlement-limit</c>).</summary>
    public static RouteHandlerBuilder EnforcesEntitlementLimit(this RouteHandlerBuilder builder, string limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limit);
        return builder.WithMetadata(new EntitlementLimitMetadata(limit)).ProducesProblem(StatusCodes.Status403Forbidden);
    }

    /// <summary>Registers the entitlement gate handler (the implementation of <see cref="IEntitlements"/> is separate).</summary>
    public static IServiceCollection AddEntitlementGate(this IServiceCollection services) =>
        services.AddScoped<IAuthorizationHandler, EntitlementAuthorizationHandler>();
}

using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.SharedKernel.Platform.Audit;

/// <summary>Action codes of the access-denial audit events (threat-model §7, #89).</summary>
public static class AccessDenialActions
{
    /// <summary><c>X-Tenant-Id</c> names a tenant where the caller has no active membership, or the tenant is not active.</summary>
    public const string MembershipDenied = "auth.membership_denied";

    /// <summary>A permission gate, a K / O record-level condition or the fail-closed safety net answered 403.</summary>
    public const string AccessDenied = "auth.access_denied";

    /// <summary>The tenant's plan does not include the feature or a plan limit is reached (<c>entitlement.*</c>).</summary>
    public const string EntitlementDenied = "auth.entitlement_denied";
}

/// <summary>One denied request, as the host saw it. User, correlation id and IP come from the request, never from here.</summary>
/// <param name="Action">One of <see cref="AccessDenialActions"/>.</param>
/// <param name="Code">The 403 problem <c>code</c> (a server constant, e.g. <c>auth.forbidden</c>, <c>orders.not_owner</c>).</param>
/// <param name="TenantId">
/// The resolved request tenant; for <see cref="AccessDenialActions.MembershipDenied"/> the tenant named by
/// <c>X-Tenant-Id</c> (unverified — the row is only written when that tenant exists).
/// </param>
/// <param name="Role">The caller's role in the tenant; null when there is no active membership.</param>
/// <param name="Permissions">The denied permission, or the endpoint's <c>anyOf</c> list.</param>
/// <param name="Condition">The K / O condition code that a tenant rule switched off.</param>
/// <param name="Feature">The entitlement feature or limit code.</param>
public sealed record AccessDenial(
    string Action,
    string Code,
    Guid TenantId,
    string? Role,
    IReadOnlyList<string>? Permissions = null,
    string? Condition = null,
    string? Feature = null);

/// <summary>
/// Appends access-denial audit rows on its <b>own</b> short connection and transaction (as <c>bayios_app</c>, tenant
/// context via <c>set_config</c>, RLS applies): the request's unit of work rolls back on a 403, the denial row must not.
/// Deduplicated per (user, tenant, code) and bounded (threat-model §7). Only authenticated callers are recorded.
/// </summary>
public interface IAccessDenialAudit
{
    /// <summary>Never throws: a failed or skipped write is logged and the 403 is answered regardless.</summary>
    Task RecordAsync(HttpContext context, AccessDenial denial);
}

/// <summary>Turns a 403 problem of the authorization result handler or the unit of work filter into an audit row.</summary>
public static class AccessDenials
{
    /// <summary>
    /// Records <paramref name="problem"/> for the request's resolved tenant. No-op without a tenant (nothing to scope the
    /// row to), without a caller or when no <see cref="IAccessDenialAudit"/> is registered.
    /// </summary>
    public static Task RecordAsync(HttpContext context, ProblemDetails? problem)
    {
        var services = context.RequestServices;
        if (services.GetService<IAccessDenialAudit>() is not { } audit
            || services.GetService<IRequestContext>() is not { TenantId: { } tenantId, UserId: not null } requestContext)
        {
            return Task.CompletedTask;
        }

        var code = problem?.Extensions.TryGetValue("code", out var value) == true && value is string text ? text : AuthorizationProblems.Forbidden;
        var entitlement = code.StartsWith("entitlement.", StringComparison.Ordinal);
        IReadOnlyList<string>? permissions = Extension(problem, "permission") is { } permission
            ? [permission]
            : context.GetEndpoint()?.Metadata.GetMetadata<PermissionMetadata>()?.AnyOf;
        return audit.RecordAsync(context, new AccessDenial(
            entitlement ? AccessDenialActions.EntitlementDenied : AccessDenialActions.AccessDenied,
            code,
            tenantId,
            requestContext.Role,
            entitlement ? null : permissions,
            Extension(problem, "condition"),
            entitlement ? Extension(problem, "feature") : null));
    }

    /// <summary>The problem of an endpoint result, unwrapping <c>Results&lt;…&gt;</c> unions.</summary>
    public static ProblemDetails? ProblemOf(object? result) => result switch
    {
        INestedHttpResult nested => ProblemOf(nested.Result),
        Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem => problem.ProblemDetails,
        _ => null,
    };

    /// <summary>Recorded problem members are server constants; anything else (or an over-long value) is dropped.</summary>
    private static string? Extension(ProblemDetails? problem, string name) =>
        problem?.Extensions.TryGetValue(name, out var value) == true && value is string { Length: > 0 and <= 100 } text ? text : null;
}

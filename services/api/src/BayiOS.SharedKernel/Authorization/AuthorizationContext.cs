using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace BayiOS.SharedKernel.Authorization;

/// <summary>What the caller may do with a permission in the active tenant, after tenant rules are applied.</summary>
public enum PermissionScope
{
    /// <summary>Not granted (or a tenant rule switched a conditional grant off).</summary>
    None,

    /// <summary>Granted for every record.</summary>
    All,

    /// <summary>Granted for some records only (K / O footnote); the service applies the record-level condition.</summary>
    Restricted,
}

/// <summary>One effective permission of the caller (<c>GET /me</c>).</summary>
/// <param name="Scope"><c>all</c>, <c>conditional</c> (K, record-level) or <c>own</c> (O).</param>
/// <param name="Condition">The <see cref="GrantCondition.Code"/> for <c>conditional</c> / <c>own</c>.</param>
/// <param name="Parameters">The condition's tenant values (e.g. the sales discount limit); null when it has none.</param>
public sealed record PermissionAccess(string Permission, string Scope, string? Condition, GrantParameters? Parameters = null);

/// <summary>
/// Request-scoped authorization for K / O grants (#69).
/// <para>
/// The endpoint gate (<see cref="Web.EndpointConventionExtensions.RequirePermission"/>) lets a caller through when
/// any listed permission is granted. An unconditional grant needs nothing more. A record-level grant
/// (<see cref="PermissionScope.Restricted"/>) obliges the application service to ask
/// <see cref="ScopeAsync"/> / <see cref="CanAsync"/> for that permission and apply the condition; if no service does,
/// the unit of work rolls the request back and answers 403 (fail-closed).
/// </para>
/// </summary>
public interface IAuthorizationContext
{
    /// <summary>
    /// The caller's effective scope for <paramref name="permission"/>. Calling it records that the caller now applies
    /// the record-level condition for this permission.
    /// </summary>
    ValueTask<PermissionScope> ScopeAsync(string permission, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="PermissionScope.All"/> → true; <see cref="PermissionScope.Restricted"/> → <paramref name="recordCondition"/>;
    /// <see cref="PermissionScope.None"/> → false. Records the check like <see cref="ScopeAsync"/>.
    /// </summary>
    ValueTask<bool> CanAsync(string permission, bool recordCondition, CancellationToken cancellationToken);

    /// <summary>Tenant rules of the active tenant (loaded once per request).</summary>
    Task<TenantAccessRules> RulesAsync(CancellationToken cancellationToken);

    /// <summary>Every permission the caller holds in the active tenant; empty without a tenant.</summary>
    ValueTask<IReadOnlyList<PermissionAccess>> EffectivePermissionsAsync(CancellationToken cancellationToken);

    /// <summary>Endpoint gate: the best scope among <paramref name="anyOf"/>; remembers record-level passes.</summary>
    ValueTask<PermissionScope> GateAsync(IReadOnlyList<string> anyOf, CancellationToken cancellationToken);

    /// <summary>Permissions the gate let through as record-level that no service checked (should be empty).</summary>
    IReadOnlyList<string> UncheckedRestrictedGrants { get; }
}

public sealed class AuthorizationContext(IRequestContext requestContext, ITenantAccessRules tenantRules) : IAuthorizationContext
{
    private readonly List<string[]> _gatePasses = [];
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    private TenantAccessRules? _rules;

    /// <summary>The caller's role in the active tenant (null without one).</summary>
    public string? Role => requestContext.Role;

    public IReadOnlyList<string> UncheckedRestrictedGrants =>
        [.. _gatePasses.Where(group => !group.Any(_checked.Contains)).SelectMany(group => group)];

    public async Task<TenantAccessRules> RulesAsync(CancellationToken cancellationToken) =>
        _rules ??= requestContext.TenantId is null ? TenantAccessRules.Defaults : await tenantRules.GetAsync(cancellationToken);

    public async ValueTask<PermissionScope> ScopeAsync(string permission, CancellationToken cancellationToken)
    {
        var scope = await ResolveAsync(permission, cancellationToken);
        _checked.Add(permission);
        return scope;
    }

    public async ValueTask<bool> CanAsync(string permission, bool recordCondition, CancellationToken cancellationToken) =>
        await ScopeAsync(permission, cancellationToken) switch
        {
            PermissionScope.All => true,
            PermissionScope.Restricted => recordCondition,
            _ => false,
        };

    public async ValueTask<PermissionScope> GateAsync(IReadOnlyList<string> anyOf, CancellationToken cancellationToken)
    {
        var restricted = new List<string>();
        foreach (var permission in anyOf)
        {
            switch (await ResolveAsync(permission, cancellationToken))
            {
                case PermissionScope.All:
                    return PermissionScope.All;
                case PermissionScope.Restricted:
                    restricted.Add(permission);
                    break;
            }
        }

        if (restricted.Count == 0)
        {
            return PermissionScope.None;
        }

        _gatePasses.Add([.. restricted]);
        return PermissionScope.Restricted;
    }

    public async ValueTask<IReadOnlyList<PermissionAccess>> EffectivePermissionsAsync(CancellationToken cancellationToken)
    {
        var result = new List<PermissionAccess>();
        foreach (var permission in RolePermissions.AllPermissions)
        {
            var scope = await ResolveAsync(permission, cancellationToken);
            if (scope == PermissionScope.None)
            {
                continue;
            }

            if (scope == PermissionScope.All)
            {
                result.Add(new PermissionAccess(permission, "all", null));
                continue;
            }

            var own = RolePermissions.GetGrant(requestContext.Role, permission) == PermissionGrant.OwnRecordsOnly;
            var condition = ConditionalGrants.Find(requestContext.Role, permission);
            var parameters = condition?.Parameters is { } read ? read(await RulesAsync(cancellationToken)) : null;
            result.Add(new PermissionAccess(permission, own ? "own" : "conditional", condition?.Code, parameters));
        }

        return result;
    }

    private async ValueTask<PermissionScope> ResolveAsync(string permission, CancellationToken cancellationToken)
    {
        if (requestContext.TenantId is null)
        {
            return PermissionScope.None;
        }

        // Tenant rules are loaded only when a conditional cell actually depends on one.
        var needsRules = ConditionalGrants.Find(requestContext.Role, permission)?.Rule is not null
            && RolePermissions.GetGrant(requestContext.Role, permission) is not (PermissionGrant.None or PermissionGrant.Allowed);
        var rules = needsRules ? await RulesAsync(cancellationToken) : TenantAccessRules.Defaults;
        return Evaluate(requestContext.Role, permission, rules);
    }

    /// <summary>
    /// The scope a role has for a permission under the given tenant rules: ✓ → All; – → None; K / O → None when the
    /// cell has no condition or its tenant rule is off, otherwise Restricted (record-level) or All (rule only).
    /// </summary>
    public static PermissionScope Evaluate(string? role, string permission, TenantAccessRules rules)
    {
        var grant = RolePermissions.GetGrant(role, permission);
        if (grant == PermissionGrant.Allowed)
        {
            return PermissionScope.All;
        }

        if (grant == PermissionGrant.None || ConditionalGrants.Find(role, permission) is not { } condition)
        {
            return PermissionScope.None;
        }

        if (condition.Rule is { } rule && !rule(rules))
        {
            return PermissionScope.None;
        }

        return condition.RecordLevel ? PermissionScope.Restricted : PermissionScope.All;
    }

    /// <summary>
    /// The K / O cell condition that denies <paramref name="permission"/> to <paramref name="role"/> only because its
    /// tenant rule is off (roles-permissions §4) — null when the role has no such cell or the rule is on.
    /// </summary>
    public static GrantCondition? SwitchedOffByTenantRule(string? role, string permission, TenantAccessRules rules) =>
        RolePermissions.GetGrant(role, permission) is PermissionGrant.Conditional or PermissionGrant.OwnRecordsOnly
        && ConditionalGrants.Find(role, permission) is { Rule: { } rule } condition
        && !rule(rules)
            ? condition
            : null;
}

/// <summary>Stable 403 problem codes of the permission model (api-strategy §6).</summary>
public static class AuthorizationProblems
{
    /// <summary>
    /// Missing permission, or a record-level grant no service evaluated (fail-closed safety net). Title kept as is.
    /// </summary>
    public const string Forbidden = "auth.forbidden";

    /// <summary>
    /// The role holds the permission conditionally (K) and the tenant rule behind it is off (e.g. footnote 9 "sales may
    /// approve orders"). The problem carries <c>permission</c> and <c>condition</c>.
    /// </summary>
    public const string DisabledByTenantRule = "auth.disabled_by_tenant_rule";
}

/// <summary>Why the endpoint gate failed when a tenant rule switched the caller's conditional grant off.</summary>
public sealed class TenantRuleFailureReason(IAuthorizationHandler handler, string permission, string condition)
    : AuthorizationFailureReason(handler, $"{permission}: switched off by tenant rule '{condition}'")
{
    public string Permission { get; } = permission;

    public string Condition { get; } = condition;
}

/// <summary>Endpoint requirement: any of <see cref="AnyOf"/> must be granted (fully or record-level).</summary>
public sealed record PermissionRequirement(IReadOnlyList<string> AnyOf) : IAuthorizationRequirement;

/// <summary>The endpoint gate: evaluates <see cref="PermissionRequirement"/> through <see cref="IAuthorizationContext"/>.</summary>
/// <remarks>
/// When every listed permission is denied and one of them only because a tenant rule is off, the failure carries a
/// <see cref="TenantRuleFailureReason"/> so the 403 can say so (<see cref="AuthorizationProblems.DisabledByTenantRule"/>).
/// </remarks>
public sealed class PermissionAuthorizationHandler(IAuthorizationContext authorization, IRequestContext requestContext)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (await authorization.GateAsync(requirement.AnyOf, CancellationToken.None) != PermissionScope.None)
        {
            context.Succeed(requirement);
            return;
        }

        if (requestContext.TenantId is null)
        {
            return;
        }

        var rules = await authorization.RulesAsync(CancellationToken.None);
        foreach (var permission in requirement.AnyOf)
        {
            if (AuthorizationContext.SwitchedOffByTenantRule(requestContext.Role, permission, rules) is { } condition)
            {
                context.Fail(new TenantRuleFailureReason(this, permission, condition.Code));
                return;
            }
        }
    }
}

/// <summary>
/// Writes the 403 of a failed endpoint gate: <see cref="AuthorizationProblems.DisabledByTenantRule"/> when a tenant rule
/// switched the grant off, otherwise the default behaviour (<c>auth.forbidden</c> from the JWT scheme's OnForbidden).
/// </summary>
public sealed class PermissionAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.OfType<TenantRuleFailureReason>().FirstOrDefault() is { } reason)
        {
            var problem = ApiProblems.Create(StatusCodes.Status403Forbidden, AuthorizationProblems.DisabledByTenantRule, "Not allowed");
            problem.Extensions["permission"] = reason.Permission;
            problem.Extensions["condition"] = reason.Condition;
            await ApiProblems.WriteAsync(context, problem);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

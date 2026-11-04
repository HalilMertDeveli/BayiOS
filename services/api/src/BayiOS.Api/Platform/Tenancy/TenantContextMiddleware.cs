using BayiOS.Api.Platform.Auth;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Authorization;

namespace BayiOS.Api.Platform.Tenancy;

/// <summary>
/// Runs after authentication, before authorization. For unit-of-work endpoints it binds the caller (JWT <c>sub</c>)
/// to the request, opens the transaction and — for tenant-scoped endpoints — accepts <c>X-Tenant-Id</c> only when
/// the caller has an <b>active</b> membership in that tenant (ADR 0002). The header is a selection, never a grant.
/// </summary>
internal sealed partial class TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, RequestContext requestContext, UnitOfWork unitOfWork)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<UnitOfWorkMetadata>() is null
            || context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.User.FindFirst(AuthenticationSetup.SubjectClaim)?.Value, out var userId))
        {
            // Unauthenticated calls fall through: the authorization middleware answers 401.
            await next(context);
            return;
        }

        requestContext.UserId = userId;
        var cancellationToken = context.RequestAborted;
        var readOnly = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        await unitOfWork.BeginAsync(readOnly, cancellationToken);

        if (endpoint.Metadata.GetMetadata<TenantScopedMetadata>() is not null)
        {
            if (!Guid.TryParse(context.Request.Headers[TenantHeader], out var tenantId))
            {
                await ApiProblems.WriteAsync(context, ApiProblems.Create(
                    StatusCodes.Status400BadRequest, "request.malformed", $"{TenantHeader} header must be a tenant UUID"));
                return;
            }

            var lookup = context.RequestServices.GetRequiredService<IMembershipLookup>();
            var role = await lookup.FindActiveRoleAsync(userId, tenantId, cancellationToken);
            if (role is null)
            {
                LogTenantAccessDenied(logger, userId, tenantId);
                await ApiProblems.WriteAsync(context, ApiProblems.Create(
                    StatusCodes.Status403Forbidden, "tenant.access_denied", "No access to this tenant"));
                return;
            }

            await unitOfWork.SetTenantAsync(tenantId, cancellationToken);
            requestContext.Role = role;
        }

        await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant access denied for user {UserId} to tenant {TenantId}")]
    private static partial void LogTenantAccessDenied(ILogger logger, Guid userId, Guid tenantId);
}

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Grants a permission policy from the caller's membership role via the code-defined matrix.</summary>
internal sealed class PermissionAuthorizationHandler(IRequestContext requestContext) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requestContext.TenantId is not null && RolePermissions.IsGranted(requestContext.Role, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class PermissionAuthorizationSetup
{
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorizationBuilder().AddPermissionPolicies();
        return services;
    }

    private static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        foreach (var permission in RolePermissions.AllPermissions)
        {
            builder.AddPolicy(PermissionPolicy.Name(permission), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }

        return builder;
    }
}

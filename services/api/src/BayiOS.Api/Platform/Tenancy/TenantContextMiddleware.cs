using BayiOS.Api.Platform.Auth;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Api.Platform.Tenancy;

/// <summary>
/// Runs after authentication, before authorization. For unit-of-work endpoints it binds the caller (JWT <c>sub</c>)
/// to the request, opens the transaction and — for tenant-scoped endpoints — accepts <c>X-Tenant-Id</c> only when
/// the caller has an <b>active</b> membership in that tenant (ADR 0002). The header is a selection, never a grant.
/// </summary>
internal sealed partial class TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        // Already server-formatted by CorrelationIdMiddleware; used by audit events, jobs and outbox messages.
        requestContext.CorrelationId = context.TraceIdentifier;
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
        var unitOfWork = context.RequestServices.GetRequiredService<UnitOfWork>();
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
            var access = await lookup.FindAccessAsync(userId, tenantId, cancellationToken);
            if (access is null)
            {
                LogTenantAccessDenied(logger, userId, tenantId);
                await ApiProblems.WriteAsync(context, ApiProblems.Create(
                    StatusCodes.Status403Forbidden, "tenant.access_denied", "No access to this tenant"));
                return;
            }

            // Checked after the membership, so a non-member learns nothing about the tenant's status.
            if (!access.TenantIsActive)
            {
                LogTenantInactive(logger, userId, tenantId, access.TenantStatus);
                await ApiProblems.WriteAsync(context, ApiProblems.Create(
                    StatusCodes.Status403Forbidden, "tenant.inactive", "This tenant is not active"));
                return;
            }

            await unitOfWork.SetTenantAsync(tenantId, cancellationToken);
            requestContext.Role = access.Role;
        }
        else if (endpoint.Metadata.GetMetadata<TenantOptionalMetadata>() is not null
            && Guid.TryParse(context.Request.Headers[TenantHeader], out var optionalTenantId))
        {
            // Optional selection (GET /me): an unknown, inaccessible or inactive tenant leaves the request tenant-less.
            var lookup = context.RequestServices.GetRequiredService<IMembershipLookup>();
            if (await lookup.FindAccessAsync(userId, optionalTenantId, cancellationToken) is { TenantIsActive: true } access)
            {
                await unitOfWork.SetTenantAsync(optionalTenantId, cancellationToken);
                requestContext.Role = access.Role;
            }
        }

        await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant access denied for user {UserId} to tenant {TenantId}")]
    private static partial void LogTenantAccessDenied(ILogger logger, Guid userId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inactive tenant {TenantId} ({Status}) refused for user {UserId}")]
    private static partial void LogTenantInactive(ILogger logger, Guid userId, Guid tenantId, string status);
}

internal static class PermissionAuthorizationSetup
{
    /// <summary>
    /// The endpoint gate (<see cref="PermissionAuthorizationHandler"/>) and the request-scoped
    /// <see cref="IAuthorizationContext"/> that services use for K / O grants; tenant rules come from Identity.
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        // 403 auth.disabled_by_tenant_rule when a tenant rule switched a K grant off; otherwise the default (auth.forbidden).
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionAuthorizationResultHandler>();
        services.AddScoped<IAuthorizationContext, AuthorizationContext>();
        services.AddScoped<ITenantAccessRules, TenantSettingsAccessRules>();
        return services;
    }
}

/// <summary>Tenant rules (roles-permissions §4) from <c>identity.tenant_settings</c> (RLS: the request's tenant).</summary>
/// <remarks>Resolves the DbContext lazily: the authorization handler (and so this adapter) is built for every request, including ones that never reach the database.</remarks>
internal sealed class TenantSettingsAccessRules(IServiceProvider services) : ITenantAccessRules
{
    public async Task<TenantAccessRules> GetAsync(CancellationToken cancellationToken) =>
        await services.GetRequiredService<IdentityDbContext>().TenantSettings.AsNoTracking()
            .Select(s => new TenantAccessRules(
                s.SalesCanApproveOrders,
                s.SalesMaxDiscountPercent,
                s.ShowCostToManager,
                s.ShowCostToViewer,
                s.StaffCanAssignBarcode,
                s.StaffCanApproveReceipt))
            .SingleOrDefaultAsync(cancellationToken)
        ?? TenantAccessRules.Defaults;
}

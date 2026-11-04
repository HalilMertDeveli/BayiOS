using BayiOS.SharedKernel.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.SharedKernel.Web;

/// <summary>Endpoint needs the request unit of work (one transaction with RLS settings).</summary>
public sealed class UnitOfWorkMetadata;

/// <summary>Endpoint runs inside a tenant: <c>X-Tenant-Id</c> is required and checked against memberships.</summary>
public sealed class TenantScopedMetadata;

/// <summary>Permission the endpoint requires (also emitted as <c>x-permission</c> in OpenAPI).</summary>
public sealed record PermissionMetadata(string Permission);

public static class PermissionPolicy
{
    public const string Prefix = "permission:";

    public static string Name(string permission) => Prefix + permission;
}

public static class EndpointConventionExtensions
{
    /// <summary>Authenticated endpoints that read/write the database through the shared unit of work.</summary>
    public static TBuilder WithUnitOfWork<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new UnitOfWorkMetadata());
        builder.AddEndpointFilter(UnitOfWorkFilter.InvokeAsync);
        return builder;
    }

    /// <summary>Tenant-scoped endpoint guarded by a permission from the role → permission matrix.</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder
            .WithMetadata(new TenantScopedMetadata(), new PermissionMetadata(permission))
            .RequireAuthorization(PermissionPolicy.Name(permission));
}

/// <summary>
/// Commits the request transaction before the result is written (so a reported success is durable) and rolls it
/// back for error results. Exceptions leave it uncommitted; disposal rolls it back.
/// </summary>
internal static class UnitOfWorkFilter
{
    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var unitOfWork = context.HttpContext.RequestServices.GetRequiredService<UnitOfWork>();
        if (!unitOfWork.IsActive)
        {
            throw new InvalidOperationException("The request unit of work was not started.");
        }

        var result = await next(context);
        // Not tied to RequestAborted: once the handler finished, completing the transaction must not be half-cancelled.
        var cancellationToken = CancellationToken.None;

        if (result is IStatusCodeHttpResult { StatusCode: >= 400 })
        {
            await unitOfWork.RollbackAsync(cancellationToken);
        }
        else
        {
            await unitOfWork.CommitAsync(cancellationToken);
        }

        return result;
    }
}

using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Idempotency;
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

    /// <summary>
    /// Create / state-transition endpoint that requires an <c>Idempotency-Key</c> (api-strategy §7). The endpoint
    /// must also use <see cref="WithUnitOfWork{TBuilder}"/> (directly or on its group) so the stored response
    /// commits atomically with the business write.
    /// </summary>
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder) =>
        builder
            .WithMetadata(new IdempotencyMetadata())
            .AddEndpointFilter(IdempotencyFilter.InvokeAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

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

        if (EndpointResults.StatusCodeOf(result) >= 400)
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

/// <summary>Inspects endpoint results before they are executed.</summary>
internal static class EndpointResults
{
    /// <summary>
    /// The status code a result will write, unwrapping <c>Results&lt;…&gt;</c> unions (which do not expose a status
    /// themselves). Values that are not status-code results are written as 200.
    /// </summary>
    public static int StatusCodeOf(object? result) => result switch
    {
        INestedHttpResult nested => StatusCodeOf(nested.Result),
        IStatusCodeHttpResult { StatusCode: { } status } => status,
        _ => StatusCodes.Status200OK,
    };
}

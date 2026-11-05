using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Idempotency;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BayiOS.SharedKernel.Web;

/// <summary>Endpoint needs the request unit of work (one transaction with RLS settings).</summary>
public sealed class UnitOfWorkMetadata;

/// <summary>Endpoint runs inside a tenant: <c>X-Tenant-Id</c> is required and checked against memberships.</summary>
public sealed class TenantScopedMetadata;

/// <summary>
/// Endpoint may run inside a tenant: when <c>X-Tenant-Id</c> names one of the caller's active memberships the tenant
/// is resolved, otherwise the request runs without one (never an error). Used by <c>GET /me</c>.
/// </summary>
public sealed class TenantOptionalMetadata;

/// <summary>
/// Permission(s) the endpoint requires: any one of <see cref="AnyOf"/> (emitted as <c>x-permission</c> and, for more
/// than one, <c>x-permission-any-of</c> in OpenAPI).
/// </summary>
public sealed record PermissionMetadata(IReadOnlyList<string> AnyOf)
{
    public string Permission => AnyOf[0];
}

/// <summary>409 codes of the endpoint that are transient lock conflicts answered with <c>Retry-After</c> (OpenAPI).</summary>
public sealed record RetryableConflictMetadata(IReadOnlyList<string> Codes);

/// <summary>Problem responses of the endpoint may carry <c>shortages</c> / <c>confirmationRequired</c> (OpenAPI).</summary>
public sealed class StockProblemMetadata;

/// <summary>409 problems of the endpoint may carry a <c>conflict</c> member naming the clashing record (OpenAPI).</summary>
public sealed class ConflictDetailMetadata;

/// <summary>A family of dynamic query parameters (e.g. <c>attr.&lt;key&gt;</c>) that OpenAPI cannot list by name.</summary>
public sealed record QueryPatternMetadata(string Pattern, string Description);

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

    /// <summary>
    /// Tenant-scoped endpoint guarded by the role → permission matrix: the caller needs <b>any one</b> of
    /// <paramref name="anyOf"/>. Unconditional grants pass as is; conditional (K) grants whose tenant rule is off are
    /// denied; record-level K / O grants pass the gate but the service must apply the condition through
    /// <see cref="IAuthorizationContext"/>, or the request is rolled back with 403 (see <see cref="UnitOfWorkFilter"/>).
    /// </summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, params string[] anyOf)
    {
        if (anyOf.Length == 0)
        {
            throw new ArgumentException("At least one permission is required.", nameof(anyOf));
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement([.. anyOf]))
            .Build();
        return builder
            .WithMetadata(new TenantScopedMetadata(), new PermissionMetadata([.. anyOf]))
            .RequireAuthorization(policy);
    }

    /// <summary>Documents 409 <paramref name="codes"/> as transient lock conflicts that come with <c>Retry-After</c>.</summary>
    public static RouteHandlerBuilder ProducesRetryableConflict(this RouteHandlerBuilder builder, params string[] codes) =>
        builder.WithMetadata(new RetryableConflictMetadata([.. codes])).ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>Documents the stock problem extensions <c>shortages</c> and <c>confirmationRequired</c>.</summary>
    public static RouteHandlerBuilder ProducesStockProblems(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new StockProblemMetadata()).ProducesProblem(StatusCodes.Status409Conflict);
}

/// <summary>
/// Commits the request transaction before the result is written (so a reported success is durable) and rolls it
/// back for error results. Exceptions leave it uncommitted; disposal rolls it back.
/// <para>
/// Fail-closed for K / O grants (#69): when the endpoint gate let the caller through on a record-level grant and no
/// application service evaluated that permission through <see cref="IAuthorizationContext"/>, a success result is
/// replaced by 403 <c>auth.forbidden</c> and the transaction is rolled back. Idempotent replays are exempt: the stored
/// response was produced by a request that passed the check.
/// </para>
/// </summary>
internal static partial class UnitOfWorkFilter
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Endpoint {Endpoint} passed the gate on record-level grant(s) {Permissions} that no service evaluated; denied (fail-closed)")]
    private static partial void LogUncheckedGrant(ILogger logger, string? endpoint, string permissions);

    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var unitOfWork = services.GetRequiredService<UnitOfWork>();
        if (!unitOfWork.IsActive)
        {
            throw new InvalidOperationException("The request unit of work was not started.");
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch (EntitlementException denial)
        {
            // A service found the plan limit reached / feature missing (fail-closed): 403 and roll everything back.
            context.HttpContext.Response.Headers.Remove("ETag");
            context.HttpContext.Response.Headers.Remove("Location");
            result = TypedResults.Problem(denial.ToProblem());
        }

        // Not tied to RequestAborted: once the handler finished, completing the transaction must not be half-cancelled.
        var cancellationToken = CancellationToken.None;

        if (EndpointResults.StatusCodeOf(result) < 400
            && result is not StoredHttpResult { Replayed: true }
            && services.GetService<IAuthorizationContext>() is { UncheckedRestrictedGrants: { Count: > 0 } pending })
        {
            LogUncheckedGrant(
                services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UnitOfWorkFilter)),
                context.HttpContext.GetEndpoint()?.DisplayName,
                string.Join(", ", pending));
            context.HttpContext.Response.Headers.Remove("ETag");
            context.HttpContext.Response.Headers.Remove("Location");
            result = TypedResults.Problem(ApiProblems.Create(StatusCodes.Status403Forbidden, "auth.forbidden", "Not allowed"));
        }

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

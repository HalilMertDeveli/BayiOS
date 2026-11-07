using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Diagnostics;

namespace BayiOS.Api.Platform;

/// <summary>
/// Minimal-API binding failures (a query/route value that does not parse, an unreadable JSON body) answer the coded
/// <c>400 request.malformed</c> Problem Details in every environment (security audit 2026-11 L-4). Without this, a
/// Development host turned them into 500 (<see cref="RouteHandlerOptions.ThrowOnBadRequest"/> is on there) and a
/// Production host into a bare 400 without the <c>code</c> extension. The value is not echoed back.
/// </summary>
internal sealed class BadRequestExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        // Keep the framework's status (e.g. 415 for a wrong Content-Type, 413 for an oversized body); only the body changes.
        var problem = badRequest.StatusCode switch
        {
            StatusCodes.Status413PayloadTooLarge => ApiProblems.Create(413, "request.too_large", "Request body too large"),
            StatusCodes.Status415UnsupportedMediaType => ApiProblems.Create(415, "request.unsupported_media_type", "Unsupported content type"),
            >= 400 and < 500 => ApiProblems.Create(
                badRequest.StatusCode,
                "request.malformed",
                "Malformed request",
                "A route, query or body value could not be read as the expected type."),
            _ => null,
        };
        if (problem is null)
        {
            return false;
        }

        await ApiProblems.WriteAsync(httpContext, problem);
        return true;
    }
}

internal static class BadRequestHandling
{
    /// <summary>Stable code for a framework-produced request error without one (no code for other statuses).</summary>
    public static string? CodeFor(int? status) => status switch
    {
        StatusCodes.Status400BadRequest => "request.malformed",
        StatusCodes.Status413PayloadTooLarge => "request.too_large",
        StatusCodes.Status415UnsupportedMediaType => "request.unsupported_media_type",
        _ => null,
    };

    /// <summary>Registered before the error reporter: a client's malformed input is not an unhandled server error.</summary>
    public static IServiceCollection AddBadRequestHandling(this IServiceCollection services)
    {
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<BadRequestExceptionHandler>();
        return services;
    }
}

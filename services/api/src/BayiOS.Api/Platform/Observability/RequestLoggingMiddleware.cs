using System.Diagnostics;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Diagnostics;

namespace BayiOS.Api.Platform.Observability;

/// <summary>
/// One structured line per request (#55, data-policy §8.1 allowlist): method, <b>route template</b> (never the raw
/// path or query string), status code, duration, tenant id and user id (UUIDs). The correlation id comes from the
/// enclosing scope of <see cref="CorrelationIdMiddleware"/>. Bodies and headers are never read. 5xx responses also
/// increment <c>bayios.http.server_errors</c>; tenant and user are added to the request span as ids.
/// </summary>
internal sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public const string UnmatchedRoute = "(unmatched)";

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            Complete(context, failed, Stopwatch.GetElapsedTime(started));
        }
    }

    private void Complete(HttpContext context, bool failed, TimeSpan elapsed)
    {
        var status = failed && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
        var route = RouteOf(context);
        var request = context.RequestServices?.GetService<RequestContext>();
        var tenantId = request?.TenantId;
        var userId = request?.UserId;

        if (Activity.Current is { } activity)
        {
            if (tenantId is not null)
            {
                activity.SetTag("bayios.tenant_id", tenantId.Value.ToString());
            }

            if (userId is not null)
            {
                activity.SetTag("enduser.id", userId.Value.ToString());
            }
        }

        // Probes: a 503 readiness answer is a state report, not a server error — it must not page as "5xx rate".
        var probe = route.StartsWith("/health", StringComparison.Ordinal);
        if (status >= 500 && !probe)
        {
            BayiOsMetrics.HttpServerErrors.Add(
                1,
                new KeyValuePair<string, object?>("http.route", route),
                new KeyValuePair<string, object?>("http.response.status_code", status));
        }

        var level = probe ? (status >= 500 ? LogLevel.Warning : LogLevel.Debug)
            : status >= 500 ? LogLevel.Error
            : LogLevel.Information;
        var method = MethodOf(context.Request.Method);
        var durationMs = Math.Round(elapsed.TotalMilliseconds, 1);
        LogRequest(logger, level, method, route, status, durationMs, tenantId, userId);
    }

    private static readonly HashSet<string> KnownMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE", "CONNECT",
    };

    /// <summary>Standard methods only; anything else is caller-chosen text and is logged as OTHER.</summary>
    internal static string MethodOf(string method) => KnownMethods.Contains(method) ? method.ToUpperInvariant() : "OTHER";

    /// <summary>The endpoint's route template; for re-executed error responses the original endpoint's template.</summary>
    internal static string RouteOf(HttpContext context)
    {
        var endpoint = context.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? context.GetEndpoint();
        return endpoint is RouteEndpoint { RoutePattern.RawText: { } template }
            ? (template.StartsWith('/') ? template : "/" + template)
            : UnmatchedRoute;
    }

    [LoggerMessage(EventId = 1, Message = "HTTP {Method} {Route} responded {StatusCode} in {DurationMs} ms (tenant {TenantId}, user {UserId})")]
    private static partial void LogRequest(
        ILogger logger, LogLevel level, string method, string route, int statusCode, double durationMs, Guid? tenantId, Guid? userId);
}

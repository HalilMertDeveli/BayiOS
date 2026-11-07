using System.Diagnostics;
using BayiOS.SharedKernel.Observability;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Diagnostics;

namespace BayiOS.Api.Platform.Observability;

/// <summary>
/// Error-tracking hook (#55, observability.md §5). Every unhandled HTTP exception and every dead-lettered job is
/// reported here. The default sends nothing anywhere (it only counts <c>bayios.errors.unhandled</c>); a provider is
/// chosen by <c>ErrorTracking:Provider</c>. A vendor SDK (e.g. Sentry) is added as another implementation of this
/// interface — with <c>SendDefaultPii = false</c> and no request body capture (data-policy §8.4).
/// </summary>
internal interface IErrorReporter
{
    void Report(Exception exception, ErrorContext context);
}

/// <summary>Only allowlisted fields: ids are UUIDs, <paramref name="Operation"/> is a route template or job type.</summary>
internal sealed record ErrorContext(string Source, string? Operation, Guid? TenantId, Guid? UserId, string? CorrelationId);

internal static class ErrorSources
{
    public const string Http = "http";
    public const string Job = "job";
}

internal sealed class ErrorTrackingOptions
{
    public const string Section = "ErrorTracking";

    /// <summary><c>none</c> (default): count only. <c>otel</c>: also record a redacted exception event on the current trace span.</summary>
    public string Provider { get; set; } = Providers.None;

    public static class Providers
    {
        public const string None = "none";
        public const string OpenTelemetry = "otel";

        public static readonly IReadOnlyList<string> All = [None, OpenTelemetry];
    }
}

/// <summary>Default: no external call, only the <c>bayios.errors.unhandled</c> counter (exported only when OTLP is configured).</summary>
internal sealed class CountingErrorReporter : IErrorReporter
{
    public void Report(Exception exception, ErrorContext context) =>
        BayiOsMetrics.UnhandledErrors.Add(1, new KeyValuePair<string, object?>("source", context.Source));
}

/// <summary>
/// Vendor-neutral provider: the exception becomes an OpenTelemetry span event (type + redacted message and stack),
/// so any OTLP backend with error tracking (incl. Sentry's OTLP ingestion) groups it. Nothing leaves the process
/// unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
/// </summary>
internal sealed class OpenTelemetryErrorReporter : IErrorReporter
{
    public void Report(Exception exception, ErrorContext context)
    {
        BayiOsMetrics.UnhandledErrors.Add(1, new KeyValuePair<string, object?>("source", context.Source));
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = LogRedactor.Redact(exception.Message),
            ["exception.stacktrace"] = LogRedactor.Redact(exception.StackTrace),
            ["bayios.error.source"] = context.Source,
            ["bayios.error.operation"] = context.Operation,
        }));
    }
}

/// <summary>Reports unhandled request exceptions, then lets the default problem-details handler answer 500.</summary>
internal sealed class ReportingExceptionHandler(IErrorReporter reporter) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var request = httpContext.RequestServices.GetService<RequestContext>();
        var route = (httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint as RouteEndpoint)?.RoutePattern.RawText;
        reporter.Report(exception, new ErrorContext(ErrorSources.Http, route, request?.TenantId, request?.UserId, httpContext.TraceIdentifier));
        return ValueTask.FromResult(false);
    }
}

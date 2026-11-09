namespace BayiOS.Api.Platform;

/// <summary>
/// Accepts a caller-supplied correlation id only when it is a GUID and re-formats it server-side,
/// so no caller-controlled text reaches logs or response headers (log injection).
/// </summary>
internal static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    public static string Resolve(string? incoming) =>
        (Guid.TryParse(incoming, out var parsed) ? parsed : Guid.NewGuid()).ToString("N");
}

internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = CorrelationId.Resolve(context.Request.Headers[CorrelationId.HeaderName]);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationId.HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

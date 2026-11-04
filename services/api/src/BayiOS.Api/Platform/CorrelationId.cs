namespace BayiOS.Api.Platform;

/// <summary>
/// Accepts a caller-supplied correlation id only when it is short and made of safe characters,
/// so it can be echoed into logs and response headers without log injection.
/// </summary>
internal static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    public static string Resolve(string? incoming)
    {
        if (!string.IsNullOrEmpty(incoming) && incoming.Length <= MaxLength && incoming.All(IsAllowed))
        {
            return incoming;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
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

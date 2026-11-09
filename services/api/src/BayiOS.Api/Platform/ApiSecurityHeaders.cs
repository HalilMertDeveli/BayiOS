using System.Globalization;
using Microsoft.Extensions.Options;

namespace BayiOS.Api.Platform;

/// <summary>Response security headers of the API (security audit 2026-11 L-1). Bound from <c>SecurityHeaders</c>.</summary>
internal sealed class ApiSecurityHeaderSettings
{
    /// <summary>
    /// <c>Strict-Transport-Security</c> max-age in seconds; 0 = not sent. Default: one year in Production, off elsewhere.
    /// TLS ends at the host's edge (Fly <c>force_https</c>), so the app cannot see the scheme and sends the header on every
    /// response when enabled; browsers ignore it on plain http (RFC 6797 §8.1), so a local http stack is unaffected.
    /// </summary>
    public int? HstsMaxAgeSeconds { get; set; }
}

internal static class ApiSecurityHeaders
{
    public const int DefaultProductionHstsSeconds = 31_536_000;

    public static IServiceCollection AddApiSecurityHeaders(this IServiceCollection services)
    {
        services.AddOptions<ApiSecurityHeaderSettings>().BindConfiguration("SecurityHeaders");
        return services;
    }

    /// <summary>
    /// <c>X-Content-Type-Options: nosniff</c> on every response; <c>Cache-Control: no-store</c> on authenticated responses
    /// that did not choose a cache policy (tenant data must not sit in shared or browser caches); HSTS when configured.
    /// </summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this WebApplication app)
    {
        var configured = app.Services.GetRequiredService<IOptions<ApiSecurityHeaderSettings>>().Value.HstsMaxAgeSeconds;
        var hstsSeconds = configured ?? (app.Environment.IsProduction() ? DefaultProductionHstsSeconds : 0);
        var hsts = hstsSeconds > 0 ? $"max-age={hstsSeconds.ToString(CultureInfo.InvariantCulture)}" : null;

        return app.Use((context, next) =>
        {
            var authenticated = context.Request.Headers.Authorization.Count > 0;
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                if (hsts is not null)
                {
                    headers.StrictTransportSecurity = hsts;
                }

                if (authenticated && headers.CacheControl.Count == 0)
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
    }
}

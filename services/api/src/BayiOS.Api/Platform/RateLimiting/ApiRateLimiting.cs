using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using BayiOS.Api.Platform.Auth;
using BayiOS.Api.Platform.Tenancy;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BayiOS.Api.Platform.RateLimiting;

/// <summary>General API rate limit (threat-model §6.5, security audit 2026-11 M-3). Bound from <c>RateLimiting</c>.</summary>
internal sealed class RateLimitSettings
{
    /// <summary>Off only for a deliberate reason (never in production); limits are raised instead in test harnesses.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>GET/HEAD requests per minute, per user and tenant.</summary>
    [Range(1, 1_000_000)]
    public int ReadsPerMinute { get; set; } = 300;

    /// <summary>All other methods per minute, per user and tenant.</summary>
    [Range(1, 1_000_000)]
    public int WritesPerMinute { get; set; } = 60;

    /// <summary>
    /// Cap per user across all tenants = this × the per-tenant limit. Stops a caller from minting fresh buckets by
    /// rotating <c>X-Tenant-Id</c> (each such request would still cost a membership lookup before its 403).
    /// </summary>
    [Range(1, 1_000)]
    public int UserAcrossTenantsFactor { get; set; } = 5;
}

/// <summary>
/// Token buckets, checked <b>before any database work</b> (right after authentication, before
/// <see cref="TenantContextMiddleware"/> takes a pooled connection), so a flood is rejected without touching the
/// database the limit protects:
/// <list type="number">
/// <item><c>(user, X-Tenant-Id, read|write)</c> — the §6.5 limit (300 reads / 60 writes per minute).</item>
/// <item><c>(user, read|write)</c> over every tenant header — <see cref="RateLimitSettings.UserAcrossTenantsFactor"/> × the
/// limit, so rotating tenant ids buys nothing: a header the caller is not a member of still ends in 403.</item>
/// </list>
/// A bucket holds one minute's budget and refills continuously (<see cref="BucketFor"/>): a client may burst up to the
/// limit, then sustains exactly that rate. Over the limit → <c>429</c> Problem Details <c>rate_limit.exceeded</c> +
/// <c>Retry-After</c>; nothing is queued. Only authenticated calls to unit-of-work (database) endpoints are limited;
/// health/readiness/meta and unauthenticated requests (401 without a database round trip) are not.
/// <para>
/// In-memory per API instance (ASP.NET Core <see cref="PartitionedRateLimiter"/>). Production runs one always-on
/// instance (infra/deploy/fly.production.toml); with N instances behind a non-sticky balancer a caller can get up to
/// N × the limit — still bounded. Scaling out must divide the limits by the instance count or move the counters to a
/// shared store (docs/security/threat-model.md §6.5). A Postgres counter was rejected: it would add a write to every
/// request on the very database the limit protects.
/// </para>
/// </summary>
internal static class ApiRateLimiting
{
    public const string ExceededCode = "rate_limit.exceeded";

    public static IServiceCollection AddBayiOsRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitSettings>().BindConfiguration("RateLimiting").ValidateDataAnnotations().ValidateOnStart();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, perTenant: true)),
                PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, perTenant: false)));
            options.OnRejected = OnRejectedAsync;
        });
        return services;
    }

    /// <summary>The bucket of a request (per tenant header, or the user's cap over all tenants).</summary>
    internal static RateLimitPartition<string> Partition(HttpContext context, bool perTenant)
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value;
        if (!settings.Enabled
            || context.GetEndpoint()?.Metadata.GetMetadata<UnitOfWorkMetadata>() is null
            || context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.User.FindFirst(AuthenticationSetup.SubjectClaim)?.Value, out var userId)
            || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("exempt");
        }

        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var perMinute = read ? settings.ReadsPerMinute : settings.WritesPerMinute;
        var kind = read ? 'r' : 'w';
        if (!perTenant)
        {
            var cap = perMinute * settings.UserAcrossTenantsFactor;
            return RateLimitPartition.GetTokenBucketLimiter(
                string.Create(CultureInfo.InvariantCulture, $"{userId:N}:*:{kind}:{cap}"), _ => BucketFor(cap));
        }

        // The header is not validated yet (that needs the database): a GUID is normalised, anything else shares one
        // bucket with "no tenant" — TenantContextMiddleware answers it with 400.
        var tenant = Guid.TryParse(context.Request.Headers[TenantContextMiddleware.TenantHeader], out var tenantId)
            ? tenantId.ToString("N")
            : "-";
        return RateLimitPartition.GetTokenBucketLimiter(
            string.Create(CultureInfo.InvariantCulture, $"{userId:N}:{tenant}:{kind}:{perMinute}"), _ => BucketFor(perMinute));
    }

    /// <summary>
    /// Exactly <paramref name="perMinute"/> tokens per minute, refilled in the smallest whole steps: with
    /// g = gcd(limit, 60), g refills of limit/g tokens every 60/g s (300 → 5 every 1 s; 60 → 1 every 1 s; 5 → 1 every 12 s).
    /// </summary>
    internal static TokenBucketRateLimiterOptions BucketFor(int perMinute)
    {
        var steps = Gcd(perMinute, 60);
        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = perMinute,
            TokensPerPeriod = perMinute / steps,
            ReplenishmentPeriod = TimeSpan.FromSeconds(60d / steps),
            AutoReplenishment = true,
            QueueLimit = 0,
        };
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static async ValueTask OnRejectedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var seconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 1;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await ApiProblems.WriteAsync(context, ApiProblems.Create(
            StatusCodes.Status429TooManyRequests, ExceededCode, "Too many requests", $"Try again in {seconds} s."));
    }
}

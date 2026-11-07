using System.ComponentModel.DataAnnotations;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BayiOS.Api.Platform.Auth;

/// <summary>Supabase Auth settings (ADR 0003). Bound from the <c>Auth</c> section.</summary>
internal sealed class AuthSettings
{
    /// <summary>Token issuer, e.g. <c>https://&lt;ref&gt;.supabase.co/auth/v1</c>.</summary>
    [Required]
    public string Authority { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>Defaults to <c>{Authority}/.well-known/jwks.json</c>.</summary>
    public string? JwksUrl { get; set; }

    /// <summary>Only a local Supabase CLI stack (plain http) may turn this off.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public string ResolvedJwksUrl => JwksUrl ?? Authority.TrimEnd('/') + "/.well-known/jwks.json";
}

internal static class AuthenticationSetup
{
    public const string SubjectClaim = "sub";

    // Asymmetric only: HS256 / shared secrets are never accepted (threat-model §5).
    private static readonly string[] AllowedAlgorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256];

    public static IServiceCollection AddBayiOsAuthentication(this IServiceCollection services)
    {
        services.AddOptions<AuthSettings>()
            .BindConfiguration("Auth")
            .ValidateDataAnnotations()
            .Validate(s => Uri.TryCreate(s.Authority, UriKind.Absolute, out _), "Auth:Authority must be an absolute URI.");

        // No ValidateOnStart (see DatabaseSetup): missing auth settings make /health/ready 503 and every
        // authenticated request fail closed instead of preventing the process from starting.

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthSettings>>((options, settings) =>
            {
                var auth = TryGet(settings);
                options.MapInboundClaims = false;
                options.RefreshOnIssuerKeyNotFound = true; // unknown kid => refresh JWKS once (key rotation)
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = auth?.Authority, // null when unconfigured => issuer check fails closed
                    ValidateAudience = true,
                    ValidAudience = auth?.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ValidAlgorithms = AllowedAlgorithms,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = SubjectClaim,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // The user id is the Supabase auth user id; anything else is not a usable identity.
                        if (!Guid.TryParse(context.Principal?.FindFirst(SubjectClaim)?.Value, out _))
                        {
                            context.Fail("Token has no valid 'sub' claim.");
                        }
                        else if (IsAnonymousSession(context.Principal))
                        {
                            // Supabase anonymous sign-in (security audit 2026-11 L-2): no e-mail, no verification — not a
                            // BayiOS identity. Rejected on every endpoint, whatever the project's Auth setting says.
                            context.Fail("Anonymous sessions are not accepted.");
                        }

                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null
                            ? "Bearer"
                            : "Bearer error=\"invalid_token\"";
                        await ApiProblems.WriteAsync(context.HttpContext, ApiProblems.Create(
                            StatusCodes.Status401Unauthorized, "auth.invalid_token", "Authentication required"));
                    },
                    OnForbidden = context => ApiProblems.WriteAsync(context.HttpContext, ApiProblems.Create(
                        StatusCodes.Status403Forbidden, "auth.forbidden", "Not allowed")),
                };
            });

        // PostConfigure so tests (and only tests) can swap BackchannelHttpHandler before the JWKS client is built.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure<IOptions<AuthSettings>>((options, settings) =>
            {
                if (TryGet(settings) is not { } auth)
                {
                    // Unconfigured: no keys, no issuer => every token is rejected (401) without network calls.
                    options.Configuration = new OpenIdConnectConfiguration();
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                    return;
                }

                var httpClient = new HttpClient(options.BackchannelHttpHandler ?? new HttpClientHandler())
                {
                    Timeout = options.BackchannelTimeout,
                    MaxResponseContentBufferSize = 1024 * 1024,
                };
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    auth.ResolvedJwksUrl,
                    new JwksRetriever(auth.Authority),
                    new HttpDocumentRetriever(httpClient) { RequireHttps = auth.RequireHttpsMetadata });
            });

        return services;
    }

    public const string AnonymousClaim = "is_anonymous";

    /// <summary>Supabase marks anonymous users with <c>is_anonymous: true</c>; a malformed value is treated as anonymous.</summary>
    internal static bool IsAnonymousSession(System.Security.Claims.ClaimsPrincipal? principal) =>
        principal?.FindFirst(AnonymousClaim)?.Value is { } value
        && !(bool.TryParse(value, out var anonymous) && !anonymous);

    private static AuthSettings? TryGet(IOptions<AuthSettings> settings)
    {
        try
        {
            return settings.Value;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads a bare JWKS document (Supabase <c>/auth/v1/.well-known/jwks.json</c>) instead of OIDC discovery,
/// as decided in system-architecture §6. The ConfigurationManager caches and refreshes it.
/// </summary>
internal sealed class JwksRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var document = await retriever.GetDocumentAsync(address, cancel);
        var keys = new JsonWebKeySet(document);
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer, JwksUri = address, JsonWebKeySet = keys };
        foreach (var key in keys.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }
}

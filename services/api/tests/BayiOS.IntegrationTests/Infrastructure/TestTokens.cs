using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BayiOS.IntegrationTests.Infrastructure;

/// <summary>Test-only signing material. Generated per run; never a real Supabase key.</summary>
public static class TestTokens
{
    public const string Issuer = "https://auth.bayios.test/auth/v1";
    public const string Audience = "authenticated";
    public const string KeyId = "test-key-1";

    private static readonly RSA SigningRsa = RSA.Create(2048);

    public static SecurityKey SigningKey { get; } = new RsaSecurityKey(SigningRsa) { KeyId = KeyId };

    /// <summary>A different private key that claims the same <c>kid</c> (forged signature).</summary>
    public static SecurityKey ForgedKey { get; } = new RsaSecurityKey(RSA.Create(2048)) { KeyId = KeyId };

    /// <summary>A key whose <c>kid</c> is not published in the JWKS.</summary>
    public static SecurityKey UnknownKidKey { get; } = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "unknown-kid" };

    public static string JwksJson { get; } = BuildJwks();

    public static string Create(
        Guid? subject,
        string? email = null,
        string issuer = Issuer,
        string audience = Audience,
        DateTime? notBefore = null,
        DateTime? expires = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["role"] = "authenticated" };
        if (subject is { } sub)
        {
            claims["sub"] = sub.ToString();
        }

        if (email is not null)
        {
            claims["email"] = email;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = notBefore ?? now,
            NotBefore = notBefore ?? now.AddSeconds(-5),
            Expires = expires ?? now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, algorithm),
        });
    }

    public static string CreateHs256(Guid subject) => Create(
        subject,
        key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = KeyId },
        algorithm: SecurityAlgorithms.HmacSha256);

    private static string BuildJwks()
    {
        var parameters = SigningRsa.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }
}

/// <summary>Serves the test JWKS at the issuer's well-known URL; everything else is 404 (no real network).</summary>
internal sealed class TestJwksHandler : HttpMessageHandler
{
    public static int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        var response = request.RequestUri?.ToString() == TestTokens.Issuer + "/.well-known/jwks.json"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TestTokens.JwksJson, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}

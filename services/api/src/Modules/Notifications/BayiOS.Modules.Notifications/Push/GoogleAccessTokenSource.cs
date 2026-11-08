using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BayiOS.Modules.Notifications.Push;

/// <summary>
/// The fields of a Google service-account JSON key that the FCM sender needs. Parsed once at startup from the secret
/// <c>Push:ServiceAccountJson</c>; the private key never leaves this process and is never logged.
/// </summary>
internal sealed record ServiceAccountKey(string ProjectId, string ClientEmail, string PrivateKeyId, string PrivateKeyPem, Uri TokenUri)
{
    /// <summary>The only token endpoint accepted: a tampered key cannot redirect the signed assertion elsewhere.</summary>
    public static readonly Uri GoogleTokenUri = new("https://oauth2.googleapis.com/token");

    public static ServiceAccountKey Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Required(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException($"The service-account key has no '{name}'.");

        if (Required("type") != "service_account")
        {
            throw new InvalidOperationException("The Firebase key must be a service-account key (type = service_account).");
        }

        var tokenUri = root.TryGetProperty("token_uri", out var uri) && uri.GetString() is { Length: > 0 } text ? new Uri(text) : GoogleTokenUri;
        if (tokenUri != GoogleTokenUri)
        {
            throw new InvalidOperationException("The service-account key names an unexpected token_uri.");
        }

        var key = new ServiceAccountKey(Required("project_id"), Required("client_email"), Required("private_key_id"), Required("private_key"), tokenUri);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key.PrivateKeyPem); // fail at startup, not on the first assignment
            _ = rsa.ExportParameters(includePrivateParameters: true); // a public key alone cannot sign
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // No inner exception: its message could quote key material.
            throw new InvalidOperationException("The service-account private_key is not a PEM RSA key.");
        }

        return key;
    }

    /// <summary>Never print the private key (records print every member by default).</summary>
    public override string ToString() => $"ServiceAccountKey {{ ProjectId = {ProjectId}, ClientEmail = {ClientEmail} }}";
}

/// <summary>
/// OAuth 2.0 access tokens for the FCM HTTP v1 API from a service-account key (RFC 7523 JWT bearer grant, scope
/// <c>firebase.messaging</c>): a JWT signed with the key's RS256 private key is exchanged at Google's token endpoint.
/// Tokens are cached until five minutes before they expire; <see cref="Invalidate"/> drops one FCM refused (401).
/// </summary>
internal sealed class GoogleAccessTokenSource(ServiceAccountKey key, Func<HttpClient> httpClient, TimeProvider clock) : IDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && clock.GetUtcNow() < _expiresAt - RefreshMargin)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (_token is { } fresh && now < _expiresAt - RefreshMargin)
            {
                return fresh;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, key.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = CreateAssertion(now),
                }),
            };
            using var response = await httpClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The body may echo request details; only the status is surfaced.
                throw new HttpRequestException($"Google OAuth token request failed ({(int)response.StatusCode}).", null, response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken)
                ?? throw new HttpRequestException("Google OAuth token response was empty.");
            if (string.IsNullOrEmpty(body.AccessToken) || body.ExpiresIn <= 0)
            {
                throw new HttpRequestException("Google OAuth token response had no access token.");
            }

            _token = body.AccessToken;
            _expiresAt = now + TimeSpan.FromSeconds(body.ExpiresIn);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _token = null;

    /// <summary>The signed JWT assertion (header.claims.signature), valid for one hour from <paramref name="now"/>.</summary>
    internal string CreateAssertion(DateTimeOffset now)
    {
        var header = new Dictionary<string, string> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = key.PrivateKeyId };
        var issued = now.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = key.TokenUri.ToString(),
            ["iat"] = issued,
            ["exp"] = issued + 3600,
        };
        var signingInput = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public void Dispose() => _gate.Dispose();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

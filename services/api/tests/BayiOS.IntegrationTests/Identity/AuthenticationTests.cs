using System.Net;
using System.Net.Http.Headers;
using BayiOS.IntegrationTests.Infrastructure;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>JWT validation (#11, threat-model §5): only correctly signed, unexpired, right-issuer/audience tokens pass.</summary>
public class AuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Valid_token_returns_200_and_uses_the_jwks()
    {
        var response = await SendMe(TestTokens.Create(Guid.NewGuid(), "valid@example.test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(TestJwksHandler.Requests > 0, "Signing keys must come from the JWKS endpoint.");
    }

    [Fact]
    public async Task Missing_token_returns_401_problem()
    {
        var response = await fixture.CreateClient().GetAsync("/api/v1/me", TestContext.Current.CancellationToken);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_token");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    public static TheoryData<string, string> InvalidTokens()
    {
        var user = Guid.NewGuid();
        var now = DateTime.UtcNow;
        return new TheoryData<string, string>
        {
            { "expired", TestTokens.Create(user, notBefore: now.AddHours(-2), expires: now.AddHours(-1)) },
            { "expired beyond 30s skew", TestTokens.Create(user, notBefore: now.AddMinutes(-10), expires: now.AddSeconds(-45)) },
            { "not yet valid", TestTokens.Create(user, notBefore: now.AddMinutes(5), expires: now.AddMinutes(30)) },
            { "wrong signature (same kid)", TestTokens.Create(user, key: TestTokens.ForgedKey) },
            { "unknown kid", TestTokens.Create(user, key: TestTokens.UnknownKidKey) },
            { "wrong audience", TestTokens.Create(user, audience: "anon") },
            { "wrong issuer", TestTokens.Create(user, issuer: "https://evil.example/auth/v1") },
            { "HS256 shared secret", TestTokens.CreateHs256(user) },
            { "missing sub", TestTokens.Create(subject: null) },
            { "garbage", "not-a-jwt" },
            { "alg none", UnsignedToken(user) },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_token_returns_401_problem(string reason, string token)
    {
        var response = await SendMe(token);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, reason);
        await HttpAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_token");
    }

    [Theory]
    [InlineData("GET", "/api/v1/tenant")]
    [InlineData("GET", "/api/v1/tenant/settings")]
    [InlineData("PATCH", "/api/v1/tenant/settings")]
    [InlineData("POST", "/api/v1/tenants")]
    public async Task Business_endpoints_reject_anonymous_calls(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var response = await fixture.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        await HttpAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_token");
    }

    private async Task<HttpResponseMessage> SendMe(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fixture.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string UnsignedToken(Guid user)
    {
        static string Encode(string json) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(json);
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        return Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "."
            + Encode($"{{\"sub\":\"{user}\",\"iss\":\"{TestTokens.Issuer}\",\"aud\":\"{TestTokens.Audience}\",\"exp\":{exp}}}") + ".";
    }
}

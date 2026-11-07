using System.Security.Claims;
using BayiOS.Api.Platform.Auth;
using BayiOS.Api.Platform.RateLimiting;

namespace BayiOS.UnitTests.Platform;

public class RateLimitingTests
{
    /// <summary>The bucket refills exactly the configured number of tokens per minute (no rounding up for small limits).</summary>
    [Theory]
    [InlineData(300, 5, 1)]
    [InlineData(60, 1, 1)]
    [InlineData(5, 1, 12)]
    [InlineData(100, 5, 3)]
    [InlineData(7, 7, 60)]
    [InlineData(1_000_000, 50_000, 3)]
    public void Bucket_refills_exactly_the_per_minute_limit(int perMinute, int tokensPerPeriod, int periodSeconds)
    {
        var bucket = ApiRateLimiting.BucketFor(perMinute);
        Assert.Equal(perMinute, bucket.TokenLimit);
        Assert.Equal(tokensPerPeriod, bucket.TokensPerPeriod);
        Assert.Equal(TimeSpan.FromSeconds(periodSeconds), bucket.ReplenishmentPeriod);
        Assert.Equal(perMinute, bucket.TokensPerPeriod * (60 / bucket.ReplenishmentPeriod.TotalSeconds));
        Assert.Equal(0, bucket.QueueLimit);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)] // malformed: fail closed
    [InlineData("", true)]
    public void Anonymous_session_flag_fails_closed(string? value, bool anonymous)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
        if (value is not null)
        {
            claims.Add(new Claim(AuthenticationSetup.AnonymousClaim, value));
        }

        Assert.Equal(anonymous, AuthenticationSetup.IsAnonymousSession(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
    }
}

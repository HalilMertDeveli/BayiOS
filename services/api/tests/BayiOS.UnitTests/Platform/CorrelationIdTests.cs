using BayiOS.Api.Platform;

namespace BayiOS.UnitTests.Platform;

public class CorrelationIdTests
{
    [Theory]
    [InlineData("abc-123")]
    [InlineData("req_42.retry")]
    public void Resolve_keeps_safe_incoming_id(string incoming)
    {
        Assert.Equal(incoming, CorrelationId.Resolve(incoming));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    public void Resolve_replaces_missing_or_unsafe_id(string? incoming)
    {
        var resolved = CorrelationId.Resolve(incoming);

        Assert.NotEqual(incoming, resolved);
        Assert.Equal(32, resolved.Length);
    }

    [Fact]
    public void Resolve_replaces_overlong_id()
    {
        var resolved = CorrelationId.Resolve(new string('a', 65));

        Assert.Equal(32, resolved.Length);
    }
}

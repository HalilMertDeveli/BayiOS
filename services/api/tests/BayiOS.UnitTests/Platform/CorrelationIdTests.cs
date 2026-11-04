using BayiOS.Api.Platform;

namespace BayiOS.UnitTests.Platform;

public class CorrelationIdTests
{
    [Theory]
    [InlineData("0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("0F8FAD5B-D9CB-469F-A165-70867728950E")]
    public void Resolve_keeps_guid_incoming_id_in_normalized_form(string incoming)
    {
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", CorrelationId.Resolve(incoming));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc-123")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    public void Resolve_replaces_missing_or_non_guid_id(string? incoming)
    {
        var resolved = CorrelationId.Resolve(incoming);

        Assert.NotEqual(incoming, resolved);
        Assert.Equal(32, resolved.Length);
        Assert.True(Guid.TryParseExact(resolved, "N", out _));
    }
}

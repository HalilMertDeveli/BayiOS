using BayiOS.Modules.Identity.Application;
using BayiOS.Modules.Identity.Contracts;

namespace BayiOS.UnitTests.Identity;

/// <summary>#86: the single display-name rule (full name, else e-mail local part, else none) and the member cursor.</summary>
public class UserDisplayNameTests
{
    [Theory]
    [InlineData("Ayşe Yılmaz", "ayse@example.com", "Ayşe Yılmaz")]
    [InlineData("  Depo Ali  ", null, "Depo Ali")]
    [InlineData(null, "satis.mehmet@example.com", "satis.mehmet")]
    [InlineData("   ", " mudur@example.com ", "mudur")]
    [InlineData("", "no-at-sign", "no-at-sign")]
    [InlineData(null, "@example.com", null)]
    [InlineData(null, "  ", null)]
    [InlineData(null, null, null)]
    public void Full_name_wins_then_the_email_local_part(string? fullName, string? email, string? expected) =>
        Assert.Equal(expected, UserDisplayName.From(fullName, email));

    [Fact]
    public void Member_cursor_round_trips_and_rejects_garbage()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal(id, MemberCursor.TryDecode(MemberCursor.Encode(id)));
        Assert.Null(MemberCursor.TryDecode("not-a-cursor"));
        Assert.Null(MemberCursor.TryDecode("%%%"));
        Assert.Null(MemberCursor.TryDecode(string.Empty));
    }
}

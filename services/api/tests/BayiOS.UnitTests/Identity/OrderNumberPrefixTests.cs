using System.Text.RegularExpressions;
using BayiOS.Modules.Identity.Domain;

namespace BayiOS.UnitTests.Identity;

/// <summary>#92: the order number prefix rule (application check and database CHECK pattern agree).</summary>
public class OrderNumberPrefixTests
{
    [Theory]
    [InlineData("SIP", true)]
    [InlineData("S", true)]
    [InlineData("LED2026", true)]
    [InlineData("ABCDEFGHIJ", true)]
    [InlineData("ABCDEFGHIJK", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("sip", false)]
    [InlineData("1SIP", false)]
    [InlineData("SİP", false)]
    [InlineData("S-P", false)]
    [InlineData("S P", false)]
    public void Prefix_is_one_to_ten_ascii_letters_or_digits_starting_with_a_letter(string? prefix, bool valid)
    {
        Assert.Equal(valid, TenantSettings.IsValidOrderNumberPrefix(prefix));
        Assert.Equal(valid, prefix is not null && Regex.IsMatch(prefix, TenantSettings.OrderNumberPrefixPattern.Replace("$", "\\z", StringComparison.Ordinal)));
    }

    [Fact]
    public void Default_prefix_is_valid() => Assert.True(TenantSettings.IsValidOrderNumberPrefix(TenantSettings.DefaultOrderNumberPrefix));
}

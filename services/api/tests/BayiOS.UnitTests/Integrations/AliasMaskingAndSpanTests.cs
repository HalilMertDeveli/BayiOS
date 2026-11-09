using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Integrations.Parsing;

namespace BayiOS.UnitTests.Integrations;

/// <summary>#121: learned aliases in the matcher (WAI-07), person-name masking (best effort) and line offsets.</summary>
public sealed class AliasMaskingAndSpanTests
{
    private static readonly CatalogEntry Cg = new(Guid.NewGuid(), Guid.NewGuid(), "MTR-1001", null, "Debriyaj Balatası CG 125", null, "takim");
    private static readonly CatalogEntry Kr = new(Guid.NewGuid(), Guid.NewGuid(), "MTR-1002", null, "Debriyaj Balatası KR 150", null, "takim");
    private static readonly CatalogEntry[] Catalog = [Cg, Kr];

    private static ParsedLine Line(string text, string? unit = "takim") => new($"3 takım {text}", null, text, 3, unit, null, 0.9m, null);

    [Fact]
    public void Without_an_alias_the_look_alike_pads_are_ambiguous()
    {
        var match = new CatalogMatcher(Catalog).Match(Line("debriyaj balatası"));
        Assert.Equal(MatchStatuses.Ambiguous, match.Status);
        Assert.Null(match.ProposedVariantId);
    }

    [Fact]
    public void A_learned_alias_proposes_its_variant_and_customer_aliases_win_over_tenant_ones()
    {
        var line = Line("Debriyaj  BALATASI");
        var key = CatalogMatcher.AliasKey(line);
        Assert.Equal("debriyaj balatasi", key); // folded, spacing and case normalised

        var tenantOnly = new CatalogMatcher(Catalog, [new AliasEntry(key, Cg.VariantId, CustomerSpecific: false)]).Match(line);
        Assert.Equal((MatchStatuses.Matched, Cg.VariantId), (tenantOnly.Status, tenantOnly.ProposedVariantId));
        Assert.Equal(MatchMethods.Alias, Assert.Single(tenantOnly.Candidates).Method);

        var both = new CatalogMatcher(Catalog, [new AliasEntry(key, Kr.VariantId, CustomerSpecific: true), new AliasEntry(key, Cg.VariantId, CustomerSpecific: false)]).Match(line);
        Assert.Equal(Kr.VariantId, both.ProposedVariantId);
    }

    [Fact]
    public void An_alias_never_introduces_a_variant_that_is_not_in_the_catalog_and_never_beats_an_exact_code()
    {
        var key = CatalogMatcher.AliasKey(Line("debriyaj balatası"));
        var unknown = new CatalogMatcher(Catalog, [new AliasEntry(key, Guid.NewGuid(), CustomerSpecific: false)]).Match(Line("debriyaj balatası"));
        Assert.Equal(MatchStatuses.Ambiguous, unknown.Status);

        var coded = new ParsedLine("3 takım MTR-1002 debriyaj balatası", "MTR-1002", "debriyaj balatası", 3, "takim", null, 0.9m, null);
        var match = new CatalogMatcher(Catalog, [new AliasEntry(key, Cg.VariantId, CustomerSpecific: true)]).Match(coded);
        Assert.Equal((Kr.VariantId, MatchMethods.Sku), (match.ProposedVariantId, match.Candidates[0].Method));
    }

    [Theory]
    [InlineData("Ben Ahmet, 3 takım balata", "Ben [ad], 3 takım balata")]
    [InlineData("Adım Ayşe Yılmaz\n10 adet h4 ampul", "Adım [ad]\n10 adet h4 ampul")]
    [InlineData("İsim: Mehmet Kaya", "İsim: [ad]")]
    [InlineData("Alıcı: Fatma Demir", "Alıcı: [ad]")]
    [InlineData("Merhaba Ahmet,\n5 adet buji", "Merhaba [ad],\n5 adet buji")]
    [InlineData("Sayın Ali Can 2 adet akü", "Sayın [ad] 2 adet akü")]
    [InlineData("Mehmet abi 2 tk balata", "[ad] abi 2 tk balata")]
    [InlineData("Selim Bey için 4 adet zincir", "[ad] Bey için 4 adet zincir")]
    public void Person_names_are_masked_in_context(string text, string expected)
    {
        Assert.Equal(expected, TurkishText.MaskPersonalData(text, out var masked));
        Assert.True(masked);
    }

    [Theory]
    [InlineData("Selam abi kolay gelsin\n3 takım cg125 debriyaj balatası")]
    [InlineData("Merhaba, CG 125 için: debriyaj balatası 2 takım")]
    [InlineData("Akü 12V9Ah - 2 adet")]
    [InlineData("ben 3 adet istiyorum")]
    public void Order_text_without_a_name_in_context_is_left_alone(string text)
    {
        Assert.Equal(text, TurkishText.MaskPersonalData(text, out var masked));
        Assert.False(masked);
    }

    [Fact]
    public void Line_offsets_point_into_the_message_regardless_of_spacing_and_case()
    {
        const string Message = "Selam\n10 adet  H4 ampul\n3 takım debriyaj balatası\n10 adet h4 ampul";
        var first = OrderParseReview.Locate(Message, "10 adet h4 ampul");
        Assert.Equal("10 adet  H4 ampul", Message.Substring(first!.Value.Start, first.Value.Length));

        // Searching on from the previous line finds the second occurrence.
        var again = OrderParseReview.Locate(Message, "10 adet h4 ampul", first.Value.Start + first.Value.Length);
        Assert.Equal(Message.LastIndexOf("10 adet h4", StringComparison.Ordinal), again!.Value.Start);

        var turkish = OrderParseReview.Locate(Message, "3 TAKIM DEBRIYAJ BALATASI");
        Assert.Equal("3 takım debriyaj balatası", Message.Substring(turkish!.Value.Start, turkish.Value.Length));
        Assert.Null(OrderParseReview.Locate(Message, "yağ filtresi"));
    }
}

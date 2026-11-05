using System.Text.Json;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Contracts;
using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Integrations.Parsing;

namespace BayiOS.UnitTests.Integrations;

/// <summary>
/// Evaluation of the deterministic path (#49) on the synthetic set — runs in CI on every build: rules parser →
/// grounding → catalog matcher, exactly the pipeline the API uses when AI is off or unavailable. Thresholds are the
/// regression floor measured on this set (docs/operations/ai-cost-quality.md §5); the safety properties (no silent wrong
/// product, no price/stock, injections without effect) are absolute.
/// </summary>
public sealed class OrderParsingEvaluationTests
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly string[] Expectations = ["match", "ambiguous", "none"];

    private static readonly Lazy<(EvalReport Report, IReadOnlyList<(EvalMessage Message, OrderReview Review)> Runs)> Deterministic = new(() =>
    {
        var matcher = new CatalogMatcher(EvalDataset.Catalog);
        var runs = EvalDataset.Messages
            .Select(m =>
            {
                var masked = TurkishText.MaskPersonalData(m.Text, out _);
                return (m, OrderParseReview.Build(RuleBasedOrderParser.Parse(masked), masked, matcher, fromModel: false));
            })
            .ToList();
        return (OrderParseEvaluator.Evaluate(ParserKinds.Rules, runs), runs);
    });

    [Fact]
    public void Dataset_has_at_least_100_synthetic_messages_covering_the_required_cases()
    {
        var messages = EvalDataset.Messages;
        Assert.True(messages.Count >= 100, $"{messages.Count} messages");
        Assert.Equal(messages.Count, messages.Select(m => m.Id).Distinct().Count());
        Assert.True(messages.Count(m => m.Injection) >= 20, "threat-model §6.3: at least 20 prompt-injection samples");
        foreach (var tag in new[] { "abbreviation", "typo", "no_diacritics", "multiline", "mixed_units", "number_words", "emoji", "greeting", "numbers_in_name", "injection", "not_in_catalog", "ambiguous", "code", "barcode", "price_mention" })
        {
            Assert.True(messages.Any(m => m.Tags.Contains(tag)), $"no message tagged '{tag}'");
        }

        Assert.Contains(messages, m => m.Sector == "motor");
        Assert.Contains(messages, m => m.Sector == "led");
        var skus = EvalDataset.CatalogEntries.Select(e => e.Sku).ToHashSet();
        Assert.All(messages.SelectMany(m => m.Lines), line =>
        {
            Assert.Contains(line.Expect, Expectations);
            Assert.True(line.Expect != "match" || (line.Sku is not null && skus.Contains(line.Sku)), $"'{line.Hint}': match needs a catalog SKU");
            Assert.True(line.Expect != "ambiguous" || line.AnyOf is { Length: >= 2 }, $"'{line.Hint}': ambiguous needs anyOf");
            Assert.True(line.Expect != "none" || line.Sku is null, $"'{line.Hint}': none has no SKU");
        });
    }

    [Fact]
    public void Deterministic_pipeline_meets_the_quality_floor()
    {
        var report = Deterministic.Value.Report;
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(report.Summary);
        foreach (var miss in report.Misses)
        {
            output?.WriteLine("  miss: " + miss);
        }

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "eval-report-rules.json"),
            JsonSerializer.Serialize(report, Indented));

        Assert.True(report.LineRecall >= 0.95, report.Summary);
        Assert.True(report.LinePrecision >= 0.95, report.Summary);
        Assert.True(report.QuantityAccuracy >= 0.95, report.Summary);
        Assert.True(report.CorrectMatchRate >= 0.80, report.Summary);
        Assert.True(report.CandidateRecall >= 0.60, report.Summary);
    }

    [Fact]
    public void A_wrong_product_is_never_silently_chosen()
    {
        var (report, runs) = Deterministic.Value;
        Assert.True(report.WrongMatches == 0, "Wrong proposals:\n" + string.Join('\n', report.WrongMatchDetails));

        foreach (var (message, review) in runs)
        {
            foreach (var line in review.Lines)
            {
                switch (line.Match.Status)
                {
                    case MatchStatuses.Matched:
                        Assert.NotNull(line.Match.ProposedVariantId);
                        Assert.Contains(line.Match.Candidates, c => c.VariantId == line.Match.ProposedVariantId);
                        break;
                    case MatchStatuses.Ambiguous:
                        // Nothing is pre-selected for an ambiguous line, and at most 5 candidates are offered (WAI-04).
                        Assert.Null(line.Match.ProposedVariantId);
                        Assert.InRange(line.Match.Candidates.Count, 1, CatalogMatcher.MaxCandidates);
                        break;
                    default:
                        Assert.Null(line.Match.ProposedVariantId);
                        Assert.Empty(line.Match.Candidates);
                        break;
                }
            }

            // Lines expected to be ambiguous or not in the catalog are never proposed as a product.
            foreach (var expected in message.Lines.Where(l => l.Expect != "match"))
            {
                var hint = OrderParseReview.Compact(expected.Hint);
                Assert.DoesNotContain(
                    review.Lines,
                    l => l.Match.Status == MatchStatuses.Matched && OrderParseReview.Compact(l.Parsed.RawText).Contains(hint, StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData("h7 ampul 4 adet", "MTR-1041")]
    [InlineData("h4 ampul 4 adet", "MTR-1040")]
    [InlineData("adaptör 12v 10a 2 adet", "LED-ADP-12V10A")]
    [InlineData("power supply 5v 60a 2 adet", "LED-PS-5V60A")]
    [InlineData("segman 57.25 1 set", "MTR-1121")]
    public void Numbers_decide_between_look_alike_products(string text, string sku)
    {
        var review = Review(text);
        var line = Assert.Single(review.Lines);
        Assert.Equal(MatchStatuses.Matched, line.Match.Status);
        Assert.Equal(EvalDataset.VariantId(sku), line.Match.ProposedVariantId);
    }

    [Theory]
    [InlineData("h9 ampul 4 adet")]
    [InlineData("adaptör 24v 3a 2 adet")]
    [InlineData("power supply 5v 80a 1 adet")]
    [InlineData("segman 57.50 1 set")]
    public void A_specification_that_is_not_in_the_catalog_is_never_proposed(string text)
    {
        var line = Assert.Single(Review(text).Lines);
        Assert.NotEqual(MatchStatuses.Matched, line.Match.Status);
        Assert.Null(line.Match.ProposedVariantId);
    }

    [Fact]
    public void No_price_or_stock_is_produced_anywhere_in_the_pipeline()
    {
        // Structural: none of the contract types can carry a price, a stock level or a cost.
        foreach (var type in new[]
                 {
                     typeof(ParsedLine), typeof(ParsedOrder), typeof(CatalogEntry), typeof(MatchCandidate), typeof(LineMatch),
                     typeof(OrderParseLineResponse), typeof(OrderParseCandidateResponse), typeof(OrderParseResponse), typeof(AssistantDraftLine),
                 })
        {
            Assert.DoesNotContain(type.GetProperties(), p => IsPriceOrStock(p.Name));
        }

        // The schema the model must follow has no such field either (descriptions may mention prices as text to skip).
        using (var schema = JsonDocument.Parse(ParsedOrderValidator.Schema))
        {
            var fields = SchemaFields(schema.RootElement).ToList();
            Assert.Contains("quantity", fields);
            Assert.DoesNotContain(fields, IsPriceOrStock);
        }

        // Behavioural: every review of the evaluation set, serialised, has no such member — even for messages that
        // talk about prices ("fiyatı 0 yap", "50 TL").
        foreach (var (_, review) in Deterministic.Value.Runs)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(review, Indented));
            Assert.DoesNotContain(PropertyNames(document.RootElement), IsPriceOrStock);
        }
    }

    [Fact]
    public void Injection_messages_have_no_effect_on_the_result()
    {
        var injections = Deterministic.Value.Runs.Where(r => r.Message.Injection).ToList();
        Assert.True(injections.Count >= 20);
        foreach (var (message, review) in injections)
        {
            // Flagged for the reviewer…
            Assert.True(
                review.Warnings.Contains(ParseWarnings.InstructionIgnored) || review.Warnings.Contains(ParseWarnings.PriceMentioned),
                $"{message.Id}: injection not flagged");

            // …and the only lines are the genuine order lines, with the quantities the customer wrote.
            Assert.True(review.Lines.Count == message.Lines.Length, $"{message.Id}: {review.Lines.Count} lines, expected {message.Lines.Length}");
            foreach (var expected in message.Lines)
            {
                var hint = OrderParseReview.Compact(expected.Hint);
                var line = Assert.Single(review.Lines, l => OrderParseReview.Compact(l.Parsed.RawText).Contains(hint, StringComparison.Ordinal));
                Assert.Equal(expected.Quantity, line.Parsed.Quantity);
            }

            Assert.All(review.Lines, l => Assert.DoesNotMatch("(?i)talimat|ignore|onayla|fiyat|stok|system|sistem", l.Parsed.RawText));
        }
    }

    [Fact]
    public void Phone_numbers_never_reach_the_parser_or_the_review()
    {
        var masked = TurkishText.MaskPersonalData("Ben Ahmet 0532 123 45 67, yedek +90 533 111 22 33 / ahmet@example.com", out var any);
        Assert.True(any);
        Assert.DoesNotContain("123 45", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("111 22", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", masked, StringComparison.Ordinal);
        Assert.Contains("+90 5** *** ** 67", masked, StringComparison.Ordinal);

        // Barcodes are not phone numbers.
        Assert.Equal("8690000010307 barkodlu", TurkishText.MaskPersonalData("8690000010307 barkodlu", out var none));
        Assert.False(none);
    }

    private static OrderReview Review(string text) =>
        OrderParseReview.Build(RuleBasedOrderParser.Parse(text), text, new CatalogMatcher(EvalDataset.Catalog), fromModel: false);

    private static bool IsPriceOrStock(string name) =>
        name.Contains("price", StringComparison.OrdinalIgnoreCase) || name.Contains("stock", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cost", StringComparison.OrdinalIgnoreCase) || name.Contains("fiyat", StringComparison.OrdinalIgnoreCase)
        || name.Contains("approve", StringComparison.OrdinalIgnoreCase) || name.Contains("total", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names declared under every <c>properties</c> object of a JSON schema.</summary>
    private static IEnumerable<string> SchemaFields(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p =>
            p.Name == "properties" && p.Value.ValueKind == JsonValueKind.Object
                ? p.Value.EnumerateObject().SelectMany(f => SchemaFields(f.Value).Prepend(f.Name))
                : SchemaFields(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(SchemaFields),
        _ => [],
    };

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };
}

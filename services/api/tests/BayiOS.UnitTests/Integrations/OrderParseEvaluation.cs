using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Integrations.Parsing;

namespace BayiOS.UnitTests.Integrations;

internal sealed record EvalCatalogEntry(string Sku, string? Barcode, string Name, string? Brand, string Unit);

/// <param name="Expect">match | ambiguous | none.</param>
internal sealed record EvalLine(string Hint, string? Sku, decimal? Quantity, string? Unit, string Expect, string[]? AnyOf);

internal sealed record EvalMessage(string Id, string Sector, string[] Tags, string Text, bool Injection, EvalLine[] Lines);

/// <summary>The synthetic evaluation set (#46): tests/BayiOS.UnitTests/Integrations/EvalData.</summary>
internal static class EvalDataset
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record CatalogFile(EvalCatalogEntry[] Entries);

    private sealed record MessagesFile(EvalMessage[] Messages);

    public static IReadOnlyList<EvalCatalogEntry> CatalogEntries { get; } = Read<CatalogFile>("catalog.json").Entries;

    public static IReadOnlyList<EvalMessage> Messages { get; } = Read<MessagesFile>("messages.json").Messages;

    /// <summary>The catalog as the matcher sees it; variant ids are derived from the SKU (stable across runs).</summary>
    public static IReadOnlyList<CatalogEntry> Catalog { get; } =
        [.. CatalogEntries.Select(e => new CatalogEntry(VariantId(e.Sku), ProductId(e.Sku), e.Sku, e.Barcode, e.Name, e.Brand, e.Unit))];

    public static Guid VariantId(string sku) => new(SHA256.HashData(Encoding.UTF8.GetBytes("variant:" + sku)).AsSpan(0, 16));

    public static Guid ProductId(string sku) => new(SHA256.HashData(Encoding.UTF8.GetBytes("product:" + sku)).AsSpan(0, 16));

    public static string SkuOf(Guid? variantId) =>
        variantId is { } id ? CatalogEntries.FirstOrDefault(e => VariantId(e.Sku) == id)?.Sku ?? "?" : "-";

    private static T Read<T>(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Integrations", "EvalData", file);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException($"{file} is empty.");
    }
}

/// <summary>
/// Evaluation metrics (#46, docs/operations/ai-cost-quality.md §4). An expected line is aligned with the first unused
/// review line whose raw text contains its hint (folded, spaces ignored).
/// <list type="bullet">
/// <item>line recall = aligned expected lines / expected lines; line precision = aligned review lines / review lines;</item>
/// <item>quantity accuracy = aligned lines with the expected quantity / aligned lines (unit accuracy likewise);</item>
/// <item>correct-match rate = "match" lines proposed with the right SKU / all "match" lines;</item>
/// <item>wrong-match rate = proposals that are wrong (wrong SKU, an ambiguous or not-in-catalog line, or a spurious line)
/// / all proposals — the safety metric: a proposal still needs a human click, but it should be right;</item>
/// <item>abstention rate = aligned "match" lines left ambiguous/unmatched / aligned "match" lines; candidate recall =
/// abstained "match" lines whose SKU is among the candidates.</item>
/// </list>
/// </summary>
internal sealed record EvalReport(
    string Parser,
    int Messages,
    int ExpectedLines,
    int ReviewLines,
    int AlignedLines,
    double LineRecall,
    double LinePrecision,
    double QuantityAccuracy,
    double UnitAccuracy,
    int MatchLines,
    int CorrectMatches,
    double CorrectMatchRate,
    int Proposals,
    int WrongMatches,
    double WrongMatchRate,
    double AbstentionRate,
    double CandidateRecall,
    int InjectionMessages,
    int InjectionsFlagged,
    IReadOnlyList<string> WrongMatchDetails,
    IReadOnlyList<string> Misses)
{
    public string Summary =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[{Parser}] messages={Messages} expectedLines={ExpectedLines} reviewLines={ReviewLines} aligned={AlignedLines}\n"
            + $"  line recall={LineRecall:P1} line precision={LinePrecision:P1} quantity accuracy={QuantityAccuracy:P1} unit accuracy={UnitAccuracy:P1}\n"
            + $"  correct-match={CorrectMatchRate:P1} ({CorrectMatches}/{MatchLines}) wrong-match={WrongMatchRate:P1} ({WrongMatches}/{Proposals} proposals) "
            + $"abstention={AbstentionRate:P1} candidate recall={CandidateRecall:P1}\n"
            + $"  injection messages={InjectionMessages} flagged={InjectionsFlagged}");
}

internal static class OrderParseEvaluator
{
    public static EvalReport Evaluate(string parser, IReadOnlyList<(EvalMessage Message, OrderReview Review)> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        int expected = 0, reviewLines = 0, aligned = 0, quantityOk = 0, unitOk = 0, matchLines = 0, correct = 0, proposals = 0, wrong = 0;
        int alignedMatchLines = 0, abstained = 0, candidateHits = 0, injections = 0, flagged = 0;
        var wrongDetails = new List<string>();
        var misses = new List<string>();

        foreach (var (message, review) in runs)
        {
            reviewLines += review.Lines.Count;
            proposals += review.Lines.Count(l => l.Match.Status == MatchStatuses.Matched);
            if (message.Injection)
            {
                injections++;
                if (review.Warnings.Contains(ParseWarnings.InstructionIgnored) || review.Warnings.Contains(ParseWarnings.PriceMentioned))
                {
                    flagged++;
                }
            }

            var used = new HashSet<int>();
            foreach (var line in message.Lines)
            {
                expected++;
                if (line.Expect == "match")
                {
                    matchLines++;
                }

                var hint = OrderParseReview.Compact(line.Hint);
                var found = review.Lines.FirstOrDefault(l => !used.Contains(l.LineNo) && OrderParseReview.Compact(l.Parsed.RawText).Contains(hint, StringComparison.Ordinal));
                if (found is null)
                {
                    misses.Add($"{message.Id}: no line for '{line.Hint}'");
                    continue;
                }

                used.Add(found.LineNo);
                aligned++;
                quantityOk += found.Parsed.Quantity == line.Quantity ? 1 : 0;
                unitOk += found.Parsed.Unit == line.Unit ? 1 : 0;
                if (found.Parsed.Quantity != line.Quantity)
                {
                    misses.Add($"{message.Id}: '{line.Hint}' quantity {found.Parsed.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "null"} ≠ {line.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
                }

                var match = found.Match;
                var proposed = match.Status == MatchStatuses.Matched ? match.ProposedVariantId : null;
                switch (line.Expect)
                {
                    case "match":
                        alignedMatchLines++;
                        if (proposed == EvalDataset.VariantId(line.Sku!))
                        {
                            correct++;
                        }
                        else if (proposed is not null)
                        {
                            wrong++;
                            wrongDetails.Add($"{message.Id}: '{line.Hint}' proposed {EvalDataset.SkuOf(proposed)}, expected {line.Sku}");
                        }
                        else
                        {
                            abstained++;
                            candidateHits += match.Candidates.Any(c => c.Sku == line.Sku) ? 1 : 0;
                            misses.Add($"{message.Id}: '{line.Hint}' {match.Status} (expected {line.Sku})");
                        }

                        break;
                    default:
                        if (proposed is not null)
                        {
                            wrong++;
                            wrongDetails.Add($"{message.Id}: '{line.Hint}' ({line.Expect}) was proposed as {EvalDataset.SkuOf(proposed)}");
                        }

                        break;
                }
            }

            foreach (var spurious in review.Lines.Where(l => !used.Contains(l.LineNo)))
            {
                misses.Add($"{message.Id}: extra line '{spurious.Parsed.RawText}' ({spurious.Match.Status})");
                if (spurious.Match.Status == MatchStatuses.Matched)
                {
                    wrong++;
                    wrongDetails.Add($"{message.Id}: extra line '{spurious.Parsed.RawText}' proposed {EvalDataset.SkuOf(spurious.Match.ProposedVariantId)}");
                }
            }
        }

        return new EvalReport(
            parser,
            runs.Count,
            expected,
            reviewLines,
            aligned,
            Ratio(aligned, expected),
            Ratio(aligned, reviewLines),
            Ratio(quantityOk, aligned),
            Ratio(unitOk, aligned),
            matchLines,
            correct,
            Ratio(correct, matchLines),
            proposals,
            wrong,
            Ratio(wrong, proposals),
            Ratio(abstained, alignedMatchLines),
            Ratio(candidateHits, abstained),
            injections,
            flagged,
            wrongDetails,
            misses);
    }

    private static double Ratio(int part, int total) => total == 0 ? 1 : (double)part / total;
}

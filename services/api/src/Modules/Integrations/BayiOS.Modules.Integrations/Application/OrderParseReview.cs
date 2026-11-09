using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Integrations.Parsing;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>One line of the review: what was read and what the deterministic matcher found.</summary>
public sealed record ReviewLine(int LineNo, ParsedLine Parsed, LineMatch Match);

/// <summary>The review document: lines, text that was not read as lines, and warning codes.</summary>
public sealed record OrderReview(IReadOnlyList<ReviewLine> Lines, IReadOnlyList<string> UnparsedFragments, IReadOnlyList<string> Warnings)
{
    public int Count(string status) => Lines.Count(l => l.Match.Status == status);

    /// <summary>Mean match confidence over the lines (0 without lines).</summary>
    public decimal OverallConfidence =>
        Lines.Count == 0 ? 0 : decimal.Round(Lines.Average(l => l.Match.Confidence), 3);
}

/// <summary>
/// From a parse to a review (#47/#48) — the single pipeline used by the API and by the evaluation harness:
/// <list type="number">
/// <item>Grounding (hallucination guard, model output only): a line whose <c>rawText</c> does not occur in the message
/// sent to the parser is dropped; a <c>productCode</c> that does not occur in the message is removed. The model can
/// therefore not introduce products or codes the customer did not write.</item>
/// <item>Matching every line against the tenant's catalog with <see cref="CatalogMatcher"/>.</item>
/// <item>Rules-parser lines with neither a quantity nor a code that match nothing are moved to the unparsed
/// fragments (they are usually names or remarks, not products).</item>
/// </list>
/// </summary>
public static class OrderParseReview
{
    public static OrderReview Build(ParsedOrder parse, string parsedText, CatalogMatcher matcher, bool fromModel, IEnumerable<string>? serverWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(parsedText);
        ArgumentNullException.ThrowIfNull(matcher);

        var warnings = new SortedSet<string>(parse.Warnings, StringComparer.Ordinal);
        foreach (var warning in serverWarnings ?? [])
        {
            warnings.Add(warning);
        }

        var fragments = new List<string>(parse.UnparsedFragments);
        var haystack = Compact(parsedText);
        var lines = new List<ReviewLine>();
        foreach (var parsed in parse.Lines)
        {
            var line = parsed;
            if (fromModel)
            {
                if (!haystack.Contains(Compact(line.RawText), StringComparison.Ordinal))
                {
                    warnings.Add(ParseWarnings.LineNotInMessage);
                    continue;
                }

                if (line.ProductCode is { } code && !haystack.Contains(Compact(code), StringComparison.Ordinal))
                {
                    warnings.Add(ParseWarnings.CodeNotInMessage);
                    line = line with { ProductCode = null };
                }
            }

            var match = matcher.Match(line);
            if (!fromModel && match.Status == MatchStatuses.Unmatched && line.Quantity is null && line.ProductCode is null)
            {
                fragments.Add(line.RawText);
                continue;
            }

            lines.Add(new ReviewLine(lines.Count + 1, line, match));
        }

        if (!lines.Any(l => l.Parsed.Quantity is null))
        {
            warnings.Remove(ParseWarnings.QuantityMissing);
        }

        return new OrderReview(lines, [.. fragments.Distinct(StringComparer.Ordinal)], [.. warnings]);
    }

    /// <summary>
    /// Where <paramref name="fragment"/> occurs in <paramref name="text"/>, compared like the grounding check
    /// (case/Turkish-letter folding, whitespace ignored): start offset and length in <paramref name="text"/> (UTF-16
    /// code units), searching from <paramref name="from"/> first and then from the start; null when not found.
    /// Folding maps one character to one character, so the offsets are exact positions in the original text.
    /// </summary>
    public static (int Start, int Length)? Locate(string text, string fragment, int from = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fragment);
        var needle = Compact(fragment);
        if (needle.Length == 0)
        {
            return null;
        }

        var folded = TurkishText.Fold(text);
        var positions = new List<int>(text.Length);
        var haystack = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < folded.Length; i++)
        {
            if (!char.IsWhiteSpace(folded[i]))
            {
                positions.Add(i);
                haystack.Append(folded[i]);
            }
        }

        var compact = haystack.ToString();
        var start = positions.FindIndex(p => p >= Math.Clamp(from, 0, text.Length));
        var found = start < 0 ? -1 : compact.IndexOf(needle, start, StringComparison.Ordinal);
        if (found < 0)
        {
            found = compact.IndexOf(needle, StringComparison.Ordinal);
        }

        if (found < 0)
        {
            return null;
        }

        var first = positions[found];
        var last = positions[found + needle.Length - 1];
        return (first, last - first + 1);
    }

    /// <summary>Folded text with all whitespace removed (spans are compared regardless of spacing and case).</summary>
    public static string Compact(string text) =>
        new([.. TurkishText.Fold(text).Where(c => !char.IsWhiteSpace(c))]);
}

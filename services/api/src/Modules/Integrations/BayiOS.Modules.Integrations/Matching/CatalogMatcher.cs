using BayiOS.Modules.Integrations.Parsing;

namespace BayiOS.Modules.Integrations.Matching;

/// <summary>An active variant of the tenant's catalog, as the matcher sees it. No price, no stock.</summary>
/// <param name="Name">Product name plus the variant name when it has one.</param>
/// <param name="Unit">Unit code of the variant.</param>
public sealed record CatalogEntry(Guid VariantId, Guid ProductId, string Sku, string? Barcode, string Name, string? Brand, string Unit);

public static class MatchStatuses
{
    /// <summary>Exactly one convincing candidate: proposed to the reviewer, who still confirms it.</summary>
    public const string Matched = "matched";

    /// <summary>Several plausible candidates (≤ 5) or one weak one: nothing is proposed; the reviewer must choose.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>Nothing in the tenant's catalog fits.</summary>
    public const string Unmatched = "unmatched";

    public static readonly IReadOnlyList<string> All = [Matched, Ambiguous, Unmatched];
}

public static class MatchMethods
{
    public const string Sku = "sku";
    public const string Barcode = "barcode";
    public const string Name = "name";
    public const string Similarity = "similarity";

    /// <summary>A phrase a reviewer of the tenant confirmed for this variant before (learned alias, WAI-07).</summary>
    public const string Alias = "alias";
}

/// <summary>A learned alias as the matcher sees it: normalised phrase → variant (<see cref="CatalogMatcher.AliasKey"/>).</summary>
public sealed record AliasEntry(string Phrase, Guid VariantId, bool CustomerSpecific);

/// <param name="Score">0–1 similarity of the candidate to the line (1 for an exact code).</param>
public sealed record MatchCandidate(Guid VariantId, Guid ProductId, string Sku, string Name, string Unit, string Method, decimal Score);

/// <param name="Confidence">Method weight × similarity × unit compatibility (ADR 0010 §2.3); 0 when unmatched.</param>
/// <param name="ProposedVariantId">Only for <see cref="MatchStatuses.Matched"/>; never set for ambiguous lines.</param>
/// <param name="UnitMismatch">The unit written in the message differs from the variant's unit (no conversion is applied).</param>
public sealed record LineMatch(
    string Status,
    decimal Confidence,
    Guid? ProposedVariantId,
    bool UnitMismatch,
    IReadOnlyList<MatchCandidate> Candidates);

/// <summary>
/// Deterministic matching of parsed lines against the tenant's own catalog (#48, ADR 0010 §2.3) — no AI involved, so
/// it is fully testable: (1) a product code written in the line equal to a SKU or barcode; (1b) a learned alias of the
/// tenant (customer-specific first) whose variant is in the catalog; (2) the normalised name;
/// (3) token similarity (Turkish folding, light stemming, trigram similarity for typos, exact comparison for any token
/// with digits so "H4" never matches "H7"). A line is <see cref="MatchStatuses.Matched"/> only when the best candidate
/// covers the line almost completely, has no conflicting number and clearly beats the runner-up; otherwise it is
/// ambiguous (candidates, nothing selected) or unmatched. The matcher never selects for the user.
/// </summary>
public sealed class CatalogMatcher
{
    public const int MaxCandidates = 5;

    /// <summary>Line coverage the best candidate needs to be proposed.</summary>
    public const double MatchCoverage = 0.85;

    /// <summary>Minimum lead of the best candidate over the next one to be proposed.</summary>
    public const double MatchMargin = 0.10;

    /// <summary>Lowest score shown as a candidate.</summary>
    public const double CandidateFloor = 0.45;

    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal)
    {
        "ve", "ile", "icin", "olan", "adet", "tane", "ad", "li", "lu", "lik", "luk", "model", "marka", "urun", "parca", "malzeme",
        "abi", "lutfen", "de", "da", "bir", "bu", "su", "o",

        // Unit words inside a description ("12 volt", "50 w", "2 metrelik"): the number carries the meaning.
        "volt", "vlt", "amper", "watt", "w", "v", "a", "metre", "metrelik", "mt", "m",
    };

    // Symmetric phrase canonicalisation (applied to the line and to catalog names alike, after folding).
    private static readonly (string From, string To)[] Synonyms =
    [
        ("guc kaynagi", "power supply"), ("guc kaynak", "power supply"), ("trafo", "adaptor"), ("far lambasi", "far ampulu"),
        ("lambasi", "ampulu"), ("lamba", "ampul"), ("serit isik", "serit led"), ("projektoru", "projektor"),
    ];

    private readonly List<IndexedEntry> _entries;
    private readonly Dictionary<string, IndexedEntry> _bySku;
    private readonly Dictionary<string, IndexedEntry> _byBarcode;
    private readonly Dictionary<string, List<IndexedEntry>> _byName;
    private readonly Dictionary<string, List<IndexedEntry>> _byToken;

    /// <summary>Alias phrase → catalog entry (only aliases whose variant is in the given catalog).</summary>
    private readonly Dictionary<string, IndexedEntry> _aliases = new(StringComparer.Ordinal);

    /// <param name="aliases">
    /// Learned aliases of the tenant (WAI-07). Customer-specific ones win over tenant-wide ones for the same phrase.
    /// An alias whose variant is not in <paramref name="catalog"/> (inactive, deleted) is ignored: aliases can only point
    /// to existing variants.
    /// </param>
    public CatalogMatcher(IEnumerable<CatalogEntry> catalog, IEnumerable<AliasEntry>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _entries = [.. catalog.Select(e => new IndexedEntry(e, EntryTokens(e)))];
        _bySku = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);
        _byBarcode = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);
        _byName = new Dictionary<string, List<IndexedEntry>>(StringComparer.Ordinal);
        _byToken = new Dictionary<string, List<IndexedEntry>>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            _bySku.TryAdd(NormaliseCode(entry.Entry.Sku), entry);
            if (entry.Entry.Barcode is { Length: > 0 } barcode)
            {
                _byBarcode.TryAdd(NormaliseCode(barcode), entry);
            }

            Add(_byName, NameKey(entry.Entry.Name), entry);
            foreach (var token in entry.Tokens.Select(t => t.Text).Distinct(StringComparer.Ordinal))
            {
                Add(_byToken, token, entry);
            }
        }

        _digitWords = [.. _byToken.Keys.Where(TurkishText.HasDigit)];
        _alphaWords = [.. _byToken.Keys.Where(w => !TurkishText.HasDigit(w))];
        _byTrigram = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var word in _alphaWords)
        {
            foreach (var trigram in Trigrams(TurkishText.Stem(word)))
            {
                if (!_byTrigram.TryGetValue(trigram, out var words))
                {
                    _byTrigram[trigram] = words = [];
                }

                words.Add(word);
            }
        }

        var byVariant = _entries.GroupBy(e => e.Entry.VariantId).ToDictionary(g => g.Key, g => g.First());
        foreach (var alias in (aliases ?? []).OrderBy(a => a.CustomerSpecific))
        {
            if (alias.Phrase.Length > 0 && byVariant.TryGetValue(alias.VariantId, out var entry))
            {
                _aliases[alias.Phrase] = entry; // customer-specific ones come last and override
            }
        }
    }

    /// <summary>
    /// The normalised phrase of a line as learned aliases store it: product text (+ brand/model when not already in
    /// it), folded, canonicalised, without stop words — exactly what the matcher compares.
    /// </summary>
    public static string AliasKey(ParsedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return string.Join(' ', PhraseTokens(line).Select(t => t.Text));
    }

    private readonly List<string> _digitWords;
    private readonly List<string> _alphaWords;

    /// <summary>Trigram → catalog words containing it (fuzzy lookup without scanning the whole vocabulary).</summary>
    private readonly Dictionary<string, List<string>> _byTrigram;

    public int Count => _entries.Count;

    /// <summary>Matches one parsed line. Pure function of the line and the catalog given to the constructor.</summary>
    public LineMatch Match(ParsedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        // (1) Exact code written in the message: the line's code, else any code-shaped word of the line.
        foreach (var code in CodesOf(line))
        {
            if (_bySku.TryGetValue(code, out var bySku))
            {
                return Exact(bySku, MatchMethods.Sku, line.Unit);
            }

            if (_byBarcode.TryGetValue(code, out var byBarcode))
            {
                return Exact(byBarcode, MatchMethods.Barcode, line.Unit);
            }
        }

        var phrase = PhraseTokens(line);
        if (phrase.Count == 0)
        {
            return new LineMatch(MatchStatuses.Unmatched, 0, null, false, []);
        }

        // (b/c) A phrase a reviewer of this tenant confirmed before (customer's own first); proposed, still confirmed.
        if (_aliases.TryGetValue(string.Join(' ', phrase.Select(t => t.Text)), out var byAlias))
        {
            return Exact(byAlias, MatchMethods.Alias, line.Unit, weight: 0.98m);
        }

        // (2) The whole normalised name.
        if (_byName.TryGetValue(string.Join(' ', phrase.Select(t => t.Text)), out var byName) && byName.Count == 1)
        {
            return Exact(byName[0], MatchMethods.Name, line.Unit, weight: 0.97m);
        }

        // (3) Token similarity over the entries that share (or nearly share) a token with the line.
        var scored = new List<Scored>();
        foreach (var entry in CandidatesFor(phrase))
        {
            scored.Add(Score(phrase, entry));
        }

        var ranked = scored
            .Where(s => s.Value >= CandidateFloor)
            .OrderByDescending(s => s.Value)
            .ThenBy(s => s.Entry.Entry.Sku, StringComparer.Ordinal)
            .ToList();
        if (ranked.Count == 0)
        {
            return new LineMatch(MatchStatuses.Unmatched, 0, null, false, []);
        }

        var best = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1].Value : 0;
        var candidates = ranked
            .Where(s => s.Value >= best.Value - 0.25)
            .Take(MaxCandidates)
            .Select(s => Candidate(s.Entry, MatchMethods.Similarity, (decimal)s.Value))
            .ToList();

        if (best.Coverage >= MatchCoverage && !best.NumberConflict && best.Value - runnerUp >= MatchMargin)
        {
            var mismatch = UnitMismatch(line.Unit, best.Entry.Entry.Unit);
            var confidence = 0.9m * (decimal)best.Value * (mismatch ? 0.9m : 1m);
            return new LineMatch(MatchStatuses.Matched, Round(confidence), best.Entry.Entry.VariantId, mismatch, candidates);
        }

        return new LineMatch(MatchStatuses.Ambiguous, Round(0.9m * (decimal)best.Value * 0.5m), null, false, candidates);
    }

    // ---------------------------------------------------------------- scoring

    private sealed record Token(string Text, double Weight, string[]? Parts = null);

    private sealed record IndexedEntry(CatalogEntry Entry, IReadOnlyList<Token> Tokens);

    private sealed record Scored(IndexedEntry Entry, double Value, double Coverage, bool NumberConflict);

    private static Scored Score(IReadOnlyList<Token> phrase, IndexedEntry entry)
    {
        double covered = 0, total = 0;
        var conflict = false;
        var usedEntryTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in phrase)
        {
            var best = 0.0;
            Token? bestEntryToken = null;
            foreach (var candidate in entry.Tokens)
            {
                var similarity = TokenSimilarity(token.Text, candidate.Text);
                if (similarity > best)
                {
                    best = similarity;
                    bestEntryToken = candidate;
                }
            }

            if (TurkishText.HasDigit(token.Text) && best < 0.85)
            {
                conflict = true;
            }

            if (bestEntryToken is not null && best >= 0.5)
            {
                usedEntryTokens.Add(bestEntryToken.Text);
                usedEntryTokens.UnionWith(bestEntryToken.Parts ?? []);
            }

            covered += token.Weight * best;
            total += token.Weight;
        }

        var coverage = total == 0 ? 0 : covered / total;
        var entryTotal = entry.Tokens.Sum(t => t.Weight);
        var entryCovered = entry.Tokens.Where(t => usedEntryTokens.Contains(t.Text)).Sum(t => t.Weight);
        var specificity = entryTotal == 0 ? 0 : Math.Min(1, entryCovered / entryTotal);
        return new Scored(entry, (0.85 * coverage) + (0.15 * specificity), coverage, conflict);
    }

    /// <summary>1 = same word. Tokens with digits compare exactly (a trailing unit letter may differ: "60/55" ~ "60/55w").</summary>
    internal static double TokenSimilarity(string phrase, string entry)
    {
        if (string.Equals(phrase, entry, StringComparison.Ordinal))
        {
            return 1;
        }

        if (TurkishText.HasDigit(phrase) || TurkishText.HasDigit(entry))
        {
            if (!TurkishText.HasDigit(phrase) || !TurkishText.HasDigit(entry))
            {
                return 0;
            }

            var (longer, shorter) = phrase.Length > entry.Length ? (phrase, entry) : (entry, phrase);
            return longer.StartsWith(shorter, StringComparison.Ordinal) && char.IsAsciiDigit(shorter[^1])
                && longer.Length - shorter.Length <= 2 && longer[shorter.Length..].All(char.IsAsciiLetter)
                ? 0.9
                : 0;
        }

        var a = TurkishText.Stem(phrase);
        var b = TurkishText.Stem(entry);
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        if (phrase.Length >= 3 && entry.Length - phrase.Length >= 2 && entry.StartsWith(phrase, StringComparison.Ordinal))
        {
            return phrase.Length >= 4 ? 0.85 : 0.7; // abbreviation: "amp" → "ampul", "debr" → "debriyaj"
        }

        if (a.Length < 4 || b.Length < 4)
        {
            return 0;
        }

        var similarity = TurkishText.TrigramSimilarity(a, b);
        return similarity >= 0.4 ? Math.Min(0.9, similarity + 0.15) : 0;
    }

    private HashSet<IndexedEntry> CandidatesFor(IReadOnlyList<Token> phrase)
    {
        var found = new HashSet<IndexedEntry>(ReferenceEqualityComparer.Instance);
        foreach (var token in phrase)
        {
            if (_byToken.TryGetValue(token.Text, out var exact))
            {
                found.UnionWith(exact);
            }

            if (TurkishText.HasDigit(token.Text))
            {
                foreach (var word in _digitWords.Where(w => TokenSimilarity(token.Text, w) > 0))
                {
                    found.UnionWith(_byToken[word]);
                }

                continue;
            }

            var near = new HashSet<string>(StringComparer.Ordinal);
            if (token.Text.Length >= 3)
            {
                near.UnionWith(_alphaWords.Where(w => w.StartsWith(token.Text, StringComparison.Ordinal)));
            }

            foreach (var trigram in Trigrams(TurkishText.Stem(token.Text)))
            {
                if (_byTrigram.TryGetValue(trigram, out var words))
                {
                    near.UnionWith(words);
                }
            }

            foreach (var word in near.Where(w => TokenSimilarity(token.Text, w) >= 0.5))
            {
                found.UnionWith(_byToken[word]);
            }
        }

        return found;
    }

    private static IEnumerable<string> Trigrams(string word)
    {
        var padded = "  " + word + " ";
        for (var i = 0; i + 3 <= padded.Length; i++)
        {
            yield return padded.Substring(i, 3);
        }
    }

    // ---------------------------------------------------------------- tokens

    private static List<Token> PhraseTokens(ParsedLine line)
    {
        var text = line.ProductText;
        if (line.BrandModel is { Length: > 0 } model && !TurkishText.Fold(text).Contains(TurkishText.Fold(model), StringComparison.Ordinal))
        {
            text += " " + model;
        }

        return Weighted(Canonical(TurkishText.Fold(text)), joinPairs: false);
    }

    private static List<Token> EntryTokens(CatalogEntry entry)
    {
        var text = entry.Brand is { Length: > 0 } brand ? entry.Name + " " + brand : entry.Name;
        return Weighted(Canonical(TurkishText.Fold(text)), joinPairs: true);
    }

    /// <summary>
    /// Folded tokens without stop words; tokens with digits weigh 2 (they discriminate: H4/H7, 40A/60A), single letters
    /// 0.5, other words 1 (short words such as "iç"/"dış" or "ön" matter). Catalog entries also index letter+number pairs joined ("cg 125" → "cg125") and split ("cg125" → "cg", "125").
    /// </summary>
    private static List<Token> Weighted(string folded, bool joinPairs)
    {
        var raw = TurkishText.Tokens(folded).Where(t => !StopTokens.Contains(t)).ToList();
        var tokens = new List<Token>();
        for (var i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            tokens.Add(new Token(t, TurkishText.HasDigit(t) ? 2 : t.Length <= 1 ? 0.5 : 1));
            if (!joinPairs)
            {
                continue;
            }

            if (i + 1 < raw.Count && TurkishText.IsAlpha(t) && t.Length <= 4 && raw[i + 1].All(char.IsAsciiDigit))
            {
                tokens.Add(new Token(t + raw[i + 1], 0, [t, raw[i + 1]])); // searchable; specificity counts its parts
            }

            var split = SplitLetterDigits(t);
            if (split is not null)
            {
                tokens.Add(new Token(split.Value.Letters, 0, [t]));
                tokens.Add(new Token(split.Value.Digits, 0, [t]));
            }
        }

        return tokens;
    }

    private static (string Letters, string Digits)? SplitLetterDigits(string token)
    {
        var letters = token.TakeWhile(char.IsAsciiLetter).Count();
        return letters is >= 1 and <= 4 && letters < token.Length && token[letters..].All(char.IsAsciiDigit)
            ? (token[..letters], token[letters..])
            : null;
    }

    private static string Canonical(string folded)
    {
        var text = " " + folded + " ";
        foreach (var (from, to) in Synonyms)
        {
            text = text.Replace(" " + from + " ", " " + to + " ", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    private static string NameKey(string name) =>
        string.Join(' ', TurkishText.Tokens(Canonical(TurkishText.Fold(name))).Where(t => !StopTokens.Contains(t)));

    // ---------------------------------------------------------------- codes and results

    private static IEnumerable<string> CodesOf(ParsedLine line)
    {
        if (line.ProductCode is { Length: > 0 } code)
        {
            yield return NormaliseCode(code);
        }

        foreach (var word in line.RawText.Split([' ', ',', ';', ':', '(', ')', '\t', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = NormaliseCode(word.Trim('.', '-', '!', '?', '*', '\'', '"'));
            if (candidate.Length >= 3 && (candidate.Any(char.IsAsciiDigit) || candidate.Contains('-', StringComparison.Ordinal)))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Catalog SKU normalisation (trim, upper-case invariant).</summary>
    public static string NormaliseCode(string code) => code.Trim().ToUpperInvariant();

    private static LineMatch Exact(IndexedEntry entry, string method, string? unit, decimal weight = 1m)
    {
        var mismatch = UnitMismatch(unit, entry.Entry.Unit);
        return new LineMatch(
            MatchStatuses.Matched,
            Round(weight * (mismatch ? 0.9m : 1m)),
            entry.Entry.VariantId,
            mismatch,
            [Candidate(entry, method, weight)]);
    }

    private static MatchCandidate Candidate(IndexedEntry entry, string method, decimal score) =>
        new(entry.Entry.VariantId, entry.Entry.ProductId, entry.Entry.Sku, entry.Entry.Name, entry.Entry.Unit, method, Round(score));

    /// <summary>True when the message names a unit and the variant's unit is a different one.</summary>
    public static bool UnitMismatch(string? parsedUnit, string variantUnit)
    {
        if (parsedUnit is null)
        {
            return false;
        }

        var unit = TurkishText.Fold(variantUnit).Trim();
        return unit != parsedUnit && !(parsedUnit == ParseUnits.Takim && unit == ParseUnits.Set) && !(parsedUnit == ParseUnits.Set && unit == ParseUnits.Takim);
    }

    private static decimal Round(decimal value) => decimal.Round(Math.Clamp(value, 0, 1), 3);

    private static void Add(Dictionary<string, List<IndexedEntry>> index, string key, IndexedEntry entry)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(entry);
    }
}

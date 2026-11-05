using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BayiOS.Modules.Integrations.Parsing;

/// <summary>
/// Deterministic, offline parser (#47): the fallback when the AI parser is disabled, unconfigured, over quota or
/// failing, and the baseline the evaluation set measures in CI. It splits the message into pieces (lines, commas,
/// "ve 3 adet", "bir de"), drops greetings, keeps instructions / price talk / questions out of the order lines
/// (unparsed fragments + warnings), and reads quantity, unit and product code with fixed rules. It never invents a
/// code, a price or a quantity: anything it cannot read stays null and the reviewer decides.
/// </summary>
public sealed partial class RuleBasedOrderParser : IOrderTextParser
{
    public const string Version = "rules-v1";

    public string Kind => ParserKinds.Rules;

    public Task<OrderTextParseResult> ParseAsync(string maskedText, CancellationToken cancellationToken) =>
        Task.FromResult(new OrderTextParseResult(Parse(maskedText), ParserKinds.Rules, null, Version, AiTokenUsage.None));

    /// <summary>Synchronous core (used by the evaluation harness).</summary>
    public static ParsedOrder Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<ParsedLine>();
        var fragments = new List<string>();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);

        var withoutPlaceholders = MaskedPersonalData().Replace(text, m => new string(' ', m.Length));
        foreach (var rawLine in withoutPlaceholders.Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
        {
            foreach (var piece in SplitPieces(rawLine))
            {
                ReadPiece(piece, lines, fragments, warnings);
            }
        }

        return new ParsedOrder(
            [.. lines.Take(ParsedOrderValidator.MaxLines)],
            [.. fragments.Take(ParsedOrderValidator.MaxFragments).Select(f => f.Length > ParsedOrderValidator.MaxFragment ? f[..ParsedOrderValidator.MaxFragment] : f)],
            [.. warnings]);
    }

    // ---------------------------------------------------------------- splitting

    [GeneratedRegex(@"\s*;\s*|(?<!\d),|,(?!\d)|(?<=\p{L})[.!?]+\s+|\s+\+\s+|\s+/\s+|\s+(?:bir\s*de|ayrıca|ayrica|artı|arti)\s+|\s+ve\s+(?=(?:\d|bir\b|iki\b|üç\b|uc\b|dört\b|dort\b|beş\b|bes\b|altı\b|alti\b|yedi\b|sekiz\b|dokuz\b|on\b|yirmi\b|otuz\b|elli\b|yüz\b|yuz\b))", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    private static List<string> SplitPieces(string line)
    {
        var lower = TurkishText.Lower(line);
        var pieces = new List<string>();
        var start = 0;
        foreach (Match match in Separator().Matches(lower))
        {
            pieces.Add(line[start..match.Index]);
            start = match.Index + match.Length;
        }

        pieces.Add(line[start..]);

        // A piece without letters ("5", "x2") belongs to the previous one ("h4 ampul, 5").
        var merged = new List<string>();
        foreach (var piece in pieces.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (merged.Count > 0 && !piece.Any(char.IsLetter))
            {
                merged[^1] = merged[^1] + " " + piece;
            }
            else
            {
                merged.Add(piece);
            }
        }

        return merged;
    }

    // ---------------------------------------------------------------- one piece

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "selam", "selamlar", "slm", "merhaba", "merhabalar", "mrb", "mrhb", "sa", "selamun", "aleykum", "aleykumselam", "as",
        "gunaydin", "iyi", "gunler", "aksamlar", "calismalar", "kolay", "gelsin", "kolaygelsin", "hayirli", "isler", "bereket",
        "versin", "tesekkur", "tesekkurler", "tesekkurlerr", "tsk", "tskler", "sagol", "saol", "sagolun", "eyvallah", "abi", "abim",
        "abla", "hocam", "usta", "ustam", "kardesim", "kardes", "reis", "patron", "bey", "beyefendi", "hanim", "efendim",
        "lutfen", "lutfn", "rica", "ederim", "ederiz", "siparis", "sip", "siparisim", "siparisimiz", "liste", "listesi", "asagidaki",
        "asagida", "sunlar", "sunlari", "bunlar", "bunlari", "lazim", "lazimdi", "lazimdir", "istiyorum", "istiyoruz", "isterim",
        "gonder", "gonderin", "gonderir", "gonderirmisin", "gonderebilir", "gonderebilirmisin", "yolla", "yollar", "yollayin",
        "yollarmisin", "atar", "atarmisin", "hazirla", "hazirlar", "hazirlayin", "hazirlarmisin", "misin", "misiniz", "musun", "bize",
        "bana", "bizim", "icin", "de", "da", "ile", "olsun", "olacak", "ama", "yalniz", "hepsi", "tamam", "ok", "okey", "evet",
        "nasilsin", "nasilsiniz", "naber", "iyiyim", "saglik", "kolaylik", "gelsn", "gune", "haftalar", "selametle", "dostum",
        "arkadaslar", "ekteki", "ek", "sunu", "bunu", "bi", "bide", "acele", "kib", "kendine", "bak",

        // Context words around a line that are not part of the product.
        "gecen", "seferki", "sefer", "gibi", "her", "zamanki", "ayni", "aynisi", "aynisindan", "onceki", "yarin", "sabah", "ogle",
        "aksam", "bugun", "hemen", "acil", "kargoya", "kargo", "kargoyla", "verin", "ver", "verir", "dunku", "siparise", "daha",
        "ekle", "ekleyin", "ekler", "numaradan", "yaziyorum", "ben", "biz", "tum", "toplam", "et", "edin", "eder", "edermisin",
    };

    private static readonly string[] InstructionStems =
    [
        "talimat", "unut", "yoksay", "ignore", "instruction", "previous", "sistem", "system", "prompt", "asistan", "assistant",
        "yapayzeka", "onay", "otomatik", "stok", "admin", "yetki", "bedava", "ucretsiz", "json", "iptal", "sifirla", "kural", "rol",
        "developer", "gelistirici", "override", "approve", "price",
    ];

    private static readonly string[] PriceStems = ["fiyat", "tl", "lira", "iskonto", "indirim", "kdv", "kurus", "dolar", "euro", "pesin", "vade", "odeme"];

    private static readonly string[] DeliveryStems = ["kargo", "teslim", "yarin", "adres", "acil", "sevk", "ambar", "nakliye", "kurye", "gonderim"];

    private static void ReadPiece(string piece, List<ParsedLine> lines, List<string> fragments, SortedSet<string> warnings)
    {
        var clean = CleanSymbols(piece);
        var folded = TurkishText.Fold(clean);
        var tokens = TurkishText.Tokens(folded);
        if (tokens.Count == 0 || tokens.All(t => Fillers.Contains(t)))
        {
            return; // greeting / thanks / emoji only
        }

        // Something that looks like markup or a JSON document is never an order line (a forged "answer").
        var markupAt = piece.IndexOfAny(['{', '}', '[', ']', '<', '>']);
        var instructionAt = FirstIndex(
            FirstTokenIndex(folded, InstructionStems), IndexOf(folded, "yok say"), IndexOf(folded, "yapay zeka"), markupAt);
        var priceAt = FirstIndex(FirstTokenIndex(folded, PriceStems), piece.IndexOfAny(['₺', '$', '€']));
        var previous = HasPreviousOrderReference(folded);
        var delivery = FirstTokenIndex(folded, DeliveryStems) >= 0;
        if (previous)
        {
            warnings.Add(ParseWarnings.ReferenceToPreviousOrder);
        }

        // Instructions and price talk are never part of an order line: cut the piece there. What precedes is a line
        // only when it carries a quantity or a code ("10 adet h4 ampul fiyatı 0 olsun" → line "10 adet h4 ampul" +
        // fragment "fiyatı 0 olsun"; "önceki talimatları unut" → fragment only).
        var cut = FirstIndex(instructionAt, priceAt);
        string? note = null;
        if (cut >= 0)
        {
            if (instructionAt >= 0)
            {
                warnings.Add(ParseWarnings.InstructionIgnored);
            }

            if (priceAt >= 0)
            {
                warnings.Add(ParseWarnings.PriceMentioned);
                note = "Mesajdaki fiyat ifadesi yalnızca not olarak saklandı; fiyat fiyat listesinden gelir.";
            }

            fragments.Add(piece[cut..].Trim());
            var prefix = ReadLine(clean[..cut]);
            if (prefix is { } kept && (kept.Quantity is not null || kept.ProductCode is not null))
            {
                lines.Add(kept with { RawText = Limit(piece[..cut].Trim(), ParsedOrderValidator.MaxRawText), Notes = note });
            }

            return;
        }

        var line = ReadLine(clean);
        if (line is null)
        {
            if (cut < 0 && (delivery || piece.TrimEnd().EndsWith('?')))
            {
                warnings.Add(delivery ? ParseWarnings.DeliveryNote : ParseWarnings.UnreadableText);
                fragments.Add(piece.Trim());
            }

            return;
        }

        if (line.Quantity is null && (piece.TrimEnd().EndsWith('?') || delivery))
        {
            // A question or delivery remark without a quantity is not an order line.
            warnings.Add(delivery ? ParseWarnings.DeliveryNote : ParseWarnings.UnreadableText);
            fragments.Add(piece.Trim());
            return;
        }

        if (delivery)
        {
            warnings.Add(ParseWarnings.DeliveryNote);
        }

        if (line.Quantity is null)
        {
            warnings.Add(ParseWarnings.QuantityMissing);
        }

        if (previous)
        {
            note = note is null ? "Önceki siparişe atıf var." : note + " Önceki siparişe atıf var.";
        }

        lines.Add(line with { RawText = Limit(piece.Trim(), ParsedOrderValidator.MaxRawText), Notes = note });
    }

    private static bool HasPreviousOrderReference(string folded) =>
        folded.Contains("gecen sefer", StringComparison.Ordinal) || folded.Contains("gecen hafta", StringComparison.Ordinal)
        || folded.Contains("gecenki", StringComparison.Ordinal) || folded.Contains("her zamanki", StringComparison.Ordinal)
        || folded.Contains("oncekinin ayni", StringComparison.Ordinal) || folded.Contains("ayni siparis", StringComparison.Ordinal);

    // ---------------------------------------------------------------- quantity, unit, code

    // Full unit words may carry a suffix ("kolilik", "takımdan", "tanesi"); abbreviations must stand alone, so
    // "5 adaptör" is not read as "5 ad".
    private const string CountUnits =
        "(?:adet|tane|koli|kutu|paket|takım|takim|rulo|kangal|çift|cift)[a-zçğıöşü]*|(?:adt|ade|ad|tn|pcs|pc|kl|kt|pkt|pk|tkm|tk|set|top)";

    private const string MeasureUnits = "(?:metre|litre)[a-zçğıöşü]*|(?:mtr|mt|m|kg|kilo|lt|l)";

    private const string Distributive = @"(?:['’]?(?:ar|er|şar|şer|sar|ser))?";

    // A number glued to a code ("MTR-1040", "2.75-17") is never a quantity: '-' is excluded before every quantity.
    [GeneratedRegex(@"(?<![\p{L}\p{N}.,/-])(?<q>\d+(?:[.,]\d{1,3})?)\s*" + Distributive + @"\s*(?<u>" + CountUnits + @")(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex CountQuantity();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.,/-])(?<q>\d+(?:[.,]\d{1,3})?)\s*(?<u>" + MeasureUnits + @")(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex MeasureQuantity();

    // "x10", "x 10", "10x" — but not "2x0.75" / "32x16" (dimensions). '*' is not used: masked phones contain it.
    [GeneratedRegex(@"(?<![\p{L}\p{N}.-])x\s?(?<q>\d{1,4})(?![\p{L}\p{N}.,/])|(?<![\p{L}\p{N}.,/-])(?<q>\d{1,4})\s?x(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex TimesQuantity();

    [GeneratedRegex(@"^\s*(?<q>\d{1,3})(?:\s*['’]?(?:ar|er|şar|şer|sar|ser))?\s+(?=[a-zçğıöşü])(?!(?:w|v|a|ah|mah|mm|cm|watt|volt|amper|li|lı|lu|lü|lik|lık|luk|lük)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingQuantity();

    [GeneratedRegex(@"(?:^|\s)(?:[-:=x]\s*)?(?<q>\d{1,3})(?:\s*['’]?(?:ar|er|şar|şer|sar|ser))?\s*[.!]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingQuantity();

    // Codes: letters-dash-segments ("MTR-1040", "LED-DIM-RGB"), or a barcode (8 or 12–14 digits).
    [GeneratedRegex(@"(?<![\p{L}\p{N}-])[A-Za-z]{2,6}(?:-[A-Za-z0-9]+)+(?![\p{L}\p{N}-])|(?<![\p{N}])(?:\d{8}|\d{12,14})(?![\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCode();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[a-z]{2,4}\s?-?\d{2,3}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ModelCode();

    private static readonly Dictionary<string, decimal> NumberWords = new(StringComparer.Ordinal)
    {
        ["bir"] = 1,
        ["iki"] = 2,
        ["üç"] = 3,
        ["uc"] = 3,
        ["üc"] = 3,
        ["uç"] = 3,
        ["dört"] = 4,
        ["dort"] = 4,
        ["beş"] = 5,
        ["bes"] = 5,
        ["altı"] = 6,
        ["alti"] = 6,
        ["yedi"] = 7,
        ["sekiz"] = 8,
        ["dokuz"] = 9,
        ["on"] = 10,
        ["yirmi"] = 20,
        ["otuz"] = 30,
        ["kırk"] = 40,
        ["kirk"] = 40,
        ["elli"] = 50,
        ["altmış"] = 60,
        ["altmis"] = 60,
        ["yetmiş"] = 70,
        ["yetmis"] = 70,
        ["seksen"] = 80,
        ["doksan"] = 90,
        ["yüz"] = 100,
        ["yuz"] = 100,
        ["düzine"] = 12,
        ["duzine"] = 12,
        ["yarım"] = 0.5m,
        ["yarim"] = 0.5m,
    };

    private sealed record Span(int Start, int Length);

    private static ParsedLine? ReadLine(string clean)
    {
        var lower = TurkishText.Lower(clean);
        var removed = new List<Span>();
        decimal? quantity = null;
        string? unit = null;

        var counts = CountQuantity().Matches(lower);
        var measures = MeasureQuantity().Matches(lower);
        Match? chosen = counts.Count > 0 ? counts[0] : measures.Count > 0 ? measures[0] : null;
        if (chosen is not null)
        {
            quantity = ParseNumber(chosen.Groups["q"].Value);
            unit = NormaliseUnit(chosen.Groups["u"].Value);
            removed.Add(new Span(chosen.Index, chosen.Length));
        }
        else if (NumberWordQuantity(lower) is { } words)
        {
            quantity = words.Value;
            unit = words.Unit;
            removed.Add(words.Span);
        }
        else if (TimesQuantity().Match(lower) is { Success: true } times)
        {
            quantity = ParseNumber(times.Groups["q"].Value);
            removed.Add(new Span(times.Index, times.Length));
        }
        else if (LeadingQuantity().Match(lower) is { Success: true } leading)
        {
            quantity = ParseNumber(leading.Groups["q"].Value);
            removed.Add(new Span(leading.Index, leading.Length));
        }
        else if (TrailingQuantity().Match(lower) is { Success: true } trailing && HasLetters(lower[..trailing.Index]) && !EndsWithModelPrefix(lower[..trailing.Index]))
        {
            quantity = ParseNumber(trailing.Groups["q"].Value);
            removed.Add(new Span(trailing.Index, trailing.Length));
        }

        if (quantity is <= 0 or > ParsedOrderValidator.MaxQuantity)
        {
            quantity = null;
        }

        string? code = null;
        if (ProductCode().Match(clean) is { Success: true } codeMatch
            && (codeMatch.Value.Any(char.IsAsciiDigit) || codeMatch.Value.All(c => c == '-' || char.IsAsciiLetterUpper(c))))
        {
            code = codeMatch.Value.ToUpperInvariant();
        }

        var productText = ProductTextWithout(clean, removed);
        if (productText.Length == 0)
        {
            if (code is null)
            {
                return null;
            }

            productText = code;
        }

        var model = ModelCode().Match(TurkishText.Fold(productText)) is { Success: true } m ? productText.Substring(m.Index, m.Length).ToUpperInvariant() : null;
        var confidence = 0.5m + (quantity is null ? 0 : 0.25m) + (unit is null ? 0 : 0.1m) + (code is null ? 0 : 0.1m);
        return new ParsedLine(
            clean.Trim(),
            code,
            Limit(productText, ParsedOrderValidator.MaxProductText),
            quantity,
            unit,
            model,
            Math.Min(confidence, 0.95m),
            null);
    }

    private sealed record WordQuantity(decimal Value, string? Unit, Span Span);

    /// <summary>"iki koli", "on iki adet", "yirmi beş metre", "üç takım"; a bare number word only at the start (not "on").</summary>
    private static WordQuantity? NumberWordQuantity(string lower)
    {
        var words = WordSpans(lower);
        for (var i = 0; i < words.Count; i++)
        {
            if (!NumberWords.ContainsKey(words[i].Text))
            {
                continue;
            }

            var value = 0m;
            var j = i;
            while (j < words.Count && NumberWords.TryGetValue(words[j].Text, out var part))
            {
                // "yüz" and "düzine" multiply what precedes them ("iki yüz" = 200, "bir düzine" = 12).
                value = part is 100 or 12 ? Math.Max(value, 1) * part : value + part;
                j++;
            }

            var end = words[j - 1].Start + words[j - 1].Text.Length;
            if (j < words.Count && NormaliseUnit(words[j].Text) is { } unit)
            {
                end = words[j].Start + words[j].Text.Length;
                return new WordQuantity(value, unit, new Span(words[i].Start, end - words[i].Start));
            }

            // Without a unit, a number word counts only at the start of the line and when followed by more text —
            // never a lone "on" (also "ön" typed without dots) and never "bir de" ("also").
            var bare = words[i].Text;
            var followedByText = j < words.Count && words[j].Text != "de";
            if (i == 0 && followedByText && (j - i > 1 || bare is not "on"))
            {
                return new WordQuantity(value, null, new Span(words[i].Start, end - words[i].Start));
            }

            i = j - 1;
        }

        return null;
    }

    private sealed record Word(string Text, int Start);

    private static List<Word> WordSpans(string lower)
    {
        var words = new List<Word>();
        var start = -1;
        for (var i = 0; i <= lower.Length; i++)
        {
            var letter = i < lower.Length && char.IsLetter(lower[i]);
            if (letter && start < 0)
            {
                start = i;
            }
            else if (!letter && start >= 0)
            {
                words.Add(new Word(lower[start..i], start));
                start = -1;
            }
        }

        return words;
    }

    private static string? NormaliseUnit(string unit)
    {
        var u = TurkishText.Fold(unit);
        foreach (var (prefix, code) in UnitWords)
        {
            if (u == prefix || (prefix.Length >= 4 && prefix != "kilo" && u.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return code;
            }
        }

        return null;
    }

    private static readonly (string Word, string Code)[] UnitWords =
    [
        ("adet", ParseUnits.Adet), ("adt", ParseUnits.Adet), ("ade", ParseUnits.Adet), ("ad", ParseUnits.Adet), ("tane", ParseUnits.Adet),
        ("tn", ParseUnits.Adet), ("pcs", ParseUnits.Adet), ("pc", ParseUnits.Adet),
        ("koli", ParseUnits.Koli), ("kl", ParseUnits.Koli), ("kutu", ParseUnits.Kutu), ("kt", ParseUnits.Kutu),
        ("paket", ParseUnits.Paket), ("pkt", ParseUnits.Paket), ("pk", ParseUnits.Paket),
        ("takim", ParseUnits.Takim), ("tkm", ParseUnits.Takim), ("tk", ParseUnits.Takim), ("set", ParseUnits.Set),
        ("rulo", ParseUnits.Rulo), ("top", ParseUnits.Rulo), ("kangal", ParseUnits.Rulo), ("cift", ParseUnits.Cift),
        ("metre", ParseUnits.Metre), ("mtr", ParseUnits.Metre), ("mt", ParseUnits.Metre), ("m", ParseUnits.Metre),
        ("kg", ParseUnits.Kg), ("kilo", ParseUnits.Kg), ("litre", ParseUnits.Litre), ("lt", ParseUnits.Litre), ("l", ParseUnits.Litre),
    ];

    private static decimal? ParseNumber(string text) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool HasLetters(string text) => text.Any(char.IsLetter);

    /// <summary>The piece without the quantity spans and filler words, whitespace collapsed, edge punctuation trimmed.</summary>
    private static string ProductTextWithout(string clean, List<Span> removed)
    {
        var chars = clean.ToCharArray();
        foreach (var span in removed)
        {
            for (var i = span.Start; i < span.Start + span.Length && i < chars.Length; i++)
            {
                chars[i] = ' ';
            }
        }

        var kept = new List<string>();
        var original = new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in original)
        {
            var key = new string([.. TurkishText.Fold(word).Where(char.IsAsciiLetterOrDigit)]);
            if (key.Length > 0 && Fillers.Contains(key))
            {
                continue;
            }

            kept.Add(word);
        }

        return string.Join(' ', kept).Trim(' ', '-', ':', '=', '.', ',', '*', '!', '?', '(', ')', '\'', '’', '"', '+', '/');
    }

    [GeneratedRegex(@"^\s*(?:\d{1,2}[.)]|[-*•–])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();

    /// <summary>The placeholders <see cref="TurkishText.MaskPersonalData"/> writes: never a quantity, code or product.</summary>
    [GeneratedRegex(@"\+90 5\*\* \*\*\* \*\* \d{2}|\[e-posta\]", RegexOptions.CultureInvariant)]
    private static partial Regex MaskedPersonalData();

    /// <summary>
    /// Replaces emoji and other symbols with spaces, and a leading list marker ("1)", "2.", "-", "•") too — same length,
    /// so offsets stay valid.
    /// </summary>
    private static string CleanSymbols(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || ".,/-:'’+%()*=#&?!".Contains(c, StringComparison.Ordinal) ? c : ' ');
        }

        if (ListMarker().Match(builder.ToString()) is { Success: true } marker)
        {
            for (var i = marker.Index; i < marker.Index + marker.Length; i++)
            {
                builder[i] = ' ';
            }
        }

        return builder.ToString();
    }

    /// <summary>The smallest non-negative index, or -1.</summary>
    private static int FirstIndex(params int[] indexes) => indexes.Where(i => i >= 0).DefaultIfEmpty(-1).Min();

    private static int IndexOf(string folded, string phrase) => folded.IndexOf(phrase, StringComparison.Ordinal);

    /// <summary>Start of the first word (folded) that starts with one of <paramref name="stems"/> (short stems: equal); -1 when none.</summary>
    private static int FirstTokenIndex(string folded, string[] stems)
    {
        var offset = 0;
        foreach (var word in folded.Split([' ', '\t', ',', '.', ':', ';', '!', '?', '(', ')', '\'', '’', '"', '-', '/'], StringSplitOptions.None))
        {
            if (word.Length > 0 && stems.Any(s => s.Length <= 3 ? word == s : word.StartsWith(s, StringComparison.Ordinal)))
            {
                return offset;
            }

            offset += word.Length + 1;
        }

        return -1;
    }

    private static readonly HashSet<string> CommonShortWords = new(StringComparer.Ordinal) { "led", "rgb", "set", "top", "far", "yag", "aku", "tel", "kol" };

    /// <summary>"... cg 125": the number after a short model prefix is part of the model, not a quantity.</summary>
    private static bool EndsWithModelPrefix(string beforeNumber)
    {
        var words = TurkishText.Fold(beforeNumber).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words[^1].Length is >= 2 and <= 3 && TurkishText.IsAlpha(words[^1]) && !CommonShortWords.Contains(words[^1]);
    }

    private static string Limit(string text, int max) => text.Length > max ? text[..max].TrimEnd() : text;
}

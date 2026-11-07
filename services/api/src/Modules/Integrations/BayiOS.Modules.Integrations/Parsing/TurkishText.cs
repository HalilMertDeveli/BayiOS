using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BayiOS.Modules.Integrations.Parsing;

/// <summary>
/// Turkish text normalisation shared by the rules parser and the catalog matcher, so both sides of a comparison are
/// folded the same way. Every mapping is one character to one character, so offsets in a folded string are offsets in
/// the original.
/// </summary>
public static partial class TurkishText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Turkish lower case (I → ı, İ → i), diacritics kept. Same length as the input.</summary>
    public static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var lower = c switch
            {
                'I' => 'ı',
                'İ' => 'i',
                _ => char.ToLower(c, Turkish),
            };
            builder.Append(char.IsSurrogate(c) ? c : lower);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Search key: lower case with Turkish letters folded to ASCII (ç→c, ğ→g, ı/İ/I→i, ö→o, ş→s, ü→u, â→a, î→i, û→u).
    /// "ön" and "on" become equal — callers that must tell them apart use <see cref="Lower"/>.
    /// </summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                'ç' or 'Ç' => 'c',
                'ğ' or 'Ğ' => 'g',
                'ı' or 'I' or 'İ' or 'î' or 'Î' => 'i',
                'ö' or 'Ö' => 'o',
                'ş' or 'Ş' => 's',
                'ü' or 'Ü' or 'û' or 'Û' => 'u',
                'â' or 'Â' => 'a',
                _ when char.IsSurrogate(c) => c,
                _ => char.ToLowerInvariant(c),
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// Tokens of a folded text: runs of letters/digits; '.', ',' and '/' are kept inside a token when they join two
    /// digits ("2.75", "0,75" → "0.75", "60/55w"); 'x' between digits too ("2x0.75", "32x16").
    /// </summary>
    public static IReadOnlyList<string> Tokens(string folded)
    {
        ArgumentNullException.ThrowIfNull(folded);
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < folded.Length; i++)
        {
            var c = folded[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            var joinsDigits = current.Length > 0 && char.IsAsciiDigit(current[^1]) && i + 1 < folded.Length && char.IsAsciiDigit(folded[i + 1]);
            if (joinsDigits && c is '.' or ',' or '/')
            {
                current.Append(c == ',' ? '.' : c);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static bool HasDigit(string token) => token.Any(char.IsAsciiDigit);

    public static bool IsAlpha(string token) => token.All(char.IsAsciiLetter);

    private static readonly string[] Suffixes = ["lerini", "larini", "leri", "lari", "ler", "lar", "nin", "nun", "si", "su", "yi", "yu", "i", "u"];

    /// <summary>
    /// Light Turkish stemmer for folded alphabetic tokens of 5+ letters: strips one plural / possessive / accusative
    /// ending ("balatasi" → "balata", "ampulu" → "ampul", "filtreleri" → "filtre"). Applied to both sides.
    /// </summary>
    public static string Stem(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length < 5 || !IsAlpha(token))
        {
            return token;
        }

        foreach (var suffix in Suffixes)
        {
            if (token.EndsWith(suffix, StringComparison.Ordinal) && token.Length - suffix.Length >= 4)
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    /// <summary>pg_trgm-style similarity of two words: |shared trigrams| / |all trigrams| over "  word " padding.</summary>
    public static double TrigramSimilarity(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        var left = Trigrams(a);
        var right = Trigrams(b);
        var shared = left.Count(right.Contains);
        return (double)shared / (left.Count + right.Count - shared);
    }

    private static HashSet<string> Trigrams(string word)
    {
        var padded = "  " + word + " ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= padded.Length; i++)
        {
            set.Add(padded.Substring(i, 3));
        }

        return set;
    }

    // ---- Personal data masking (data-policy §3: "+90 5** *** ** 12") ----

    [GeneratedRegex(@"(?<![\d+])(?:\+?90[\s.-]?|0[\s.-]?)?\(?5\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    /// <summary>Replacement of a person name.</summary>
    public const string NameMask = "[ad]";

    // A capitalised word (Turkish letters included) that is not a greeting or a form of address itself ("Selam abi" is
    // not a name), and up to three of them: "Ahmet", "Ayşe Nur Çelik".
    private const string NameWord =
        @"(?!(?:Selam(?:lar)?|Merhaba|Günaydın|İyi|Kolay|Tamam|Teşekkür\p{Ll}*|Sağol\p{Ll}*|Abi|Abla|Bey|Hanım|Usta)\b)\p{Lu}\p{Ll}{1,20}";

    private const string Names = NameWord + @"(?:[ \t]+" + NameWord + "){0,2}";

    // "Ben Ahmet", "adım Ayşe Yılmaz", "İsim: Mehmet Kaya", "alıcı: Fatma Demir", "teslim alacak kişi Ali Can".
    [GeneratedRegex(
        @"(?<lead>\b(?:[Bb]en|[Bb]enim[ \t]+ad[ıi]m|[Aa]d[ıi]m|[İIi]smim|[İIi]sim|[Aa]d[ \t]*[Ss]oyad[ıi]?|[Aa]l[ıi]c[ıi]|[Tt]eslim[ \t]+alacak(?:[ \t]+ki[şs]i)?)\b[ \t]*[:,-]?[ \t]*)(?<name>" + Names + ")",
        RegexOptions.CultureInvariant)]
    private static partial Regex IntroducedNamePattern();

    // "Ahmet Bey", "Ayşe Hanım", "Mehmet abi", "Fatma abla" — a capitalised name before a form of address.
    [GeneratedRegex(
        @"(?<name>" + Names + @")(?<tail>[ \t]+(?:[Bb]ey|[Hh]an[ıi]m|[Aa]bi|[Aa]bla|[Aa]mca|[Tt]eyze|[Kk]arde[şs]im)\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddressedNamePattern();

    // "Sayın Ahmet Yılmaz", "Sn. Ayşe Kaya".
    [GeneratedRegex(@"(?<lead>\b(?:[Ss]ay[ıi]n|[Ss]n\.)[ \t]+)(?<name>" + Names + ")", RegexOptions.CultureInvariant)]
    private static partial Regex TitledNamePattern();

    // "Merhaba Ahmet," / "Selam Ayşe!" — a greeting followed by a capitalised name that ends the clause.
    [GeneratedRegex(
        @"(?<lead>\b(?:[Mm]erhaba|[Ss]elam(?:lar)?|[Gg]ünaydın|[İIi]yi[ \t]+g[üu]nler)[ \t]*,?[ \t]+)(?<name>" + Names + @")(?=[ \t]*(?:[,!.;\r\n]|$))",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GreetedNamePattern();

    /// <summary>
    /// Masks Turkish mobile numbers (keeping the last two digits), e-mail addresses and — best effort — person names
    /// before text leaves the server. Barcodes (8–14 digits not shaped like a 5xx mobile number) are left alone.
    /// <para>
    /// Names are recognised only in context (data-policy §3, limits documented there): after a self-introduction
    /// ("ben", "adım", "isim:", "alıcı:"), before a form of address ("Bey", "Hanım", "abi", "abla"), after a title
    /// ("Sayın", "Sn.") or right after a greeting ("Merhaba Ahmet,"). A name written without such context, in lower
    /// case, or a company name is not recognised; a capitalised product word in such a position may be masked too
    /// (over-masking only costs a reviewer a look at the original message).
    /// </para>
    /// </summary>
    public static string MaskPersonalData(string text, out bool masked)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = false;
        var result = PhonePattern().Replace(text, m =>
        {
            found = true;
            var digits = new string([.. m.Value.Where(char.IsAsciiDigit)]);
            return "+90 5** *** ** " + digits[^2..];
        });
        result = EmailPattern().Replace(result, _ =>
        {
            found = true;
            return "[e-posta]";
        });

        string Lead(Match m)
        {
            found = true;
            return m.Groups["lead"].Value + NameMask + m.Groups["tail"].Value;
        }

        result = IntroducedNamePattern().Replace(result, Lead);
        result = TitledNamePattern().Replace(result, Lead);
        result = GreetedNamePattern().Replace(result, Lead);
        result = AddressedNamePattern().Replace(result, Lead);
        masked = found;
        return result;
    }
}

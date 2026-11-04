using System.Globalization;
using System.Text;

namespace BayiOS.Modules.Imports.Parsing;

/// <summary>Cell value interpretation (PRD IMP-05): decimal comma or dot, percentages, barcodes, statuses.</summary>
internal static class ImportValues
{
    private static readonly string[] CurrencyMarks = ["₺", "TL", "TRY", "€", "EUR", "$", "USD"];

    /// <summary>
    /// Parses <c>1234,56</c>, <c>1.234,56</c>, <c>1,234.56</c>, <c>1234.56</c>, <c>%20</c>, <c>20 TL</c>. With both
    /// separators the last one is the decimal mark; a lone comma is decimal (Turkish); a lone dot is decimal unless it
    /// occurs more than once (thousands). Spaces (incl. NBSP) are digit grouping.
    /// </summary>
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        s = s.Trim('%');
        foreach (var mark in CurrencyMarks)
        {
            if (s.EndsWith(mark, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^mark.Length];
            }
            else if (s.StartsWith(mark, StringComparison.OrdinalIgnoreCase))
            {
                s = s[mark.Length..];
            }
        }

        if (s.Length is 0 or > 40)
        {
            return false;
        }

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        string invariant;
        if (lastComma >= 0 && lastDot >= 0)
        {
            invariant = lastComma > lastDot
                ? s.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
                : s.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (lastComma >= 0)
        {
            invariant = s.Count(c => c == ',') == 1 ? s.Replace(',', '.') : s.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (lastDot >= 0 && s.Count(c => c == '.') > 1)
        {
            invariant = s.Replace(".", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            invariant = s;
        }

        return decimal.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static bool HasScale(decimal value, int decimals) => decimal.Round(value, decimals) == value;

    /// <summary>
    /// A barcode that is all digits with a GTIN length (8, 12, 13, 14) must carry a valid check digit (EAN-8, UPC-A,
    /// EAN-13, GTIN-14). Other values are internal codes: 1–64 characters without whitespace or control characters.
    /// </summary>
    public static bool IsValidBarcode(string value)
    {
        if (value.Length is 0 or > 64 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        if (!value.All(char.IsAsciiDigit) || value.Length is not (8 or 12 or 13 or 14))
        {
            return true;
        }

        var sum = 0;
        for (var i = value.Length - 2; i >= 0; i--)
        {
            var digit = value[i] - '0';
            sum += (value.Length - 2 - i) % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - (sum % 10)) % 10 == value[^1] - '0';
    }

    /// <summary>Product status words in Turkish or English; null when not recognised.</summary>
    public static string? ParseStatus(string text) => Fold(text) switch
    {
        "active" or "aktif" or "1" or "evet" or "yes" or "true" or "acik" => "active",
        "inactive" or "pasif" or "0" or "hayir" or "no" or "false" or "kapali" => "inactive",
        _ => null,
    };

    /// <summary>
    /// Header folding for matching: Turkish letters to ASCII (İ/ı→i, ğ→g, ü→u, ş→s, ö→o, ç→c), lower case, only
    /// letters and digits. "KDV Oranı (%)" → "kdvorani".
    /// </summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = raw switch
            {
                'İ' or 'I' or 'ı' => 'i',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' => 'u',
                'Ş' or 'ş' => 's',
                'Ö' or 'ö' => 'o',
                'Ç' or 'ç' => 'c',
                _ => char.ToLowerInvariant(raw),
            };
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Text that a spreadsheet would execute as a formula when re-opened. Import rejects such values in text fields
    /// (threat-model §6.1: formula text is refused, the cached value of real formula cells is what we read).
    /// </summary>
    public static bool LooksLikeFormula(string value) => value.Length > 0 && value[0] is '=' or '@';
}

/// <summary>
/// CSV/Excel formula-injection neutraliser for anything exported (threat-model §6.1, OWASP): values starting with
/// <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, TAB or CR get a leading apostrophe, then RFC 4180 quoting.
/// </summary>
internal static class CsvSanitizer
{
    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }

    public static string Field(string? value, char delimiter)
    {
        var safe = Neutralize(value);
        return safe.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    public static string Line(IEnumerable<string?> values, char delimiter) => string.Join(delimiter, values.Select(v => Field(v, delimiter)));
}

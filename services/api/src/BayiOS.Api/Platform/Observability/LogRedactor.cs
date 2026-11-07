using System.Text.RegularExpressions;

namespace BayiOS.Api.Platform.Observability;

/// <summary>
/// The log redaction rule (data-policy §8, #55). Applied by the production log formatter to every message, exception
/// text, scope and state value — so a mistake in a log call cannot put personal data or secrets into the log stream:
/// <list type="bullet">
/// <item>Fields whose <b>name</b> denotes a secret or personal data (Authorization, Cookie, password, token, e-mail,
/// phone, body, address…) are written as <c>[redacted]</c>; raw request paths and query strings are dropped.</item>
/// <item>Values are scrubbed for bearer tokens, JWTs, e-mail addresses, <c>key=value</c> secrets (connection string
/// passwords, API keys), URL query strings (signed URLs) and phone numbers.</item>
/// </list>
/// Allowed by design: method, route template, status code, duration, tenant/user ids (UUID), correlation/trace ids.
/// </summary>
internal static partial class LogRedactor
{
    public const string Redacted = "[redacted]";

    /// <summary>Fields never written at all: the raw path/query may carry personal data; the route template is logged instead.</summary>
    private static readonly HashSet<string> DroppedKeys = new(StringComparer.OrdinalIgnoreCase) { "RequestPath", "QueryString", "Query", "Path" };

    private static readonly string[] SensitiveFragments =
        ["authorization", "cookie", "password", "passwd", "pwd", "secret", "apikey", "email", "phone", "msisdn", "address", "body", "signedurl", "connectionstring", "credential", "iban", "tckn", "taxnumber"];

    public static bool IsDropped(string key) => DroppedKeys.Contains(key);

    /// <summary>True when the field name denotes a secret or personal data; its value is replaced by <see cref="Redacted"/>.</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var normalized = Normalize(key);
        if (normalized.EndsWith("token", StringComparison.Ordinal) || normalized is "jwt" or "otp" or "name" or "fullname" or "displayname")
        {
            // "*Token" (access/refresh/id/invite token) yes; token counts ("InputTokens") stay.
            return true;
        }

        foreach (var fragment in SensitiveFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Scrubs a free-text value (message, exception, string field).</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = Bearer().Replace(value, "Bearer " + Redacted);
        text = Jwt().Replace(text, "[jwt]");
        text = UrlQuery().Replace(text, "$1?" + Redacted);
        text = KeyValueSecret().Replace(text, "$1=" + Redacted);
        text = Email().Replace(text, "[email]");
        text = TurkishPhone().Replace(text, "[phone]");
        text = InternationalPhone().Replace(text, "[phone]");
        return text;
    }

    private static string Normalize(string key)
    {
        Span<char> buffer = stackalloc char[Math.Min(key.Length, 128)];
        var length = 0;
        foreach (var c in key)
        {
            if (length == buffer.Length)
            {
                break;
            }

            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"((?:https?|wss?)://[^\s?#""'<>]+)\?[^\s#""'<>]*")]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"(?i)\b(password|pwd|passwd|secret|client_secret|api[_-]?key|apikey|token|access_token|refresh_token|id_token|authorization|cookie|set-cookie|sig|signature)\s*[=:]\s*(""[^""]*""|'[^']*'|[^\s;&,]+)")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    /// <summary>Turkish mobile numbers: +90 5xx xxx xx xx, 05xx…, 5xxxxxxxxx (not inside identifiers or GUIDs).</summary>
    [GeneratedRegex(@"(?<![\w\-])(?:\+90[\s\-]?|0)?\(?5\d{2}\)?[\s\-]?\d{3}[\s\-]?\d{2}[\s\-]?\d{2}(?![\w\-])")]
    private static partial Regex TurkishPhone();

    [GeneratedRegex(@"(?<![\w\-])\+\d[\d\s\-]{8,}\d(?![\w\-])")]
    private static partial Regex InternationalPhone();
}

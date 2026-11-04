using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BayiOS.Modules.Customers.Application;

internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Conflict(string code, string title) => new(409, code, title);

    /// <summary>A field-level permission is missing (the endpoint permission alone is not enough).</summary>
    public static AppError Forbidden(string permission) =>
        new(403, "auth.forbidden", $"Missing permission: {permission}");
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Merge-patch view: the typed values plus which members were present (absent = unchanged, null = clear).</summary>
internal sealed record Patch<T>(T Values, IReadOnlySet<string> Present)
{
    public bool Has(string member) => Present.Contains(member);
}

internal static partial class CustomerText
{
    public const decimal MaxMoney = 100_000_000_000_000m;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Trimmed, upper-cased (invariant) code; null when blank or containing whitespace/control characters.</summary>
    public static string? NormalizeCode(string? code)
    {
        var trimmed = Clean(code);
        return trimmed is null || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : trimmed.ToUpperInvariant();
    }

    /// <summary>Digits of a tax number with spaces removed.</summary>
    public static string? NormalizeTaxNumber(string? value) => Clean(value)?.Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>E.164 with spaces, dashes and parentheses removed; null when not a valid E.164 number.</summary>
    public static string? NormalizeE164(string? value)
    {
        var compact = Clean(value)?.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray();
        return compact is null ? null : E164().IsMatch(new string(compact)) ? new string(compact) : null;
    }

    public static bool IsTaxNumber(string value) => TaxNumber().IsMatch(value);

    public static bool IsPhone(string value) => Phone().IsMatch(value);

    public static bool IsEmail(string value) => value.Length <= 254 && Email().IsMatch(value);

    public static bool IsMoney(decimal value) => value >= 0 && value < MaxMoney && decimal.Round(value, 4) == value;

    public static bool IsPercent(decimal value) => value is >= 0 and <= 100 && decimal.Round(value, 2) == value;

    /// <summary>11-digit tax numbers are personal (TCKN): only the last 4 digits are shown in lists (data-policy §2).</summary>
    public static string? MaskTaxNumber(string? taxNumber) =>
        taxNumber is { Length: 11 } ? new string('*', 7) + taxNumber[^4..] : taxNumber;

    /// <summary>Escapes LIKE wildcards with <c>\</c> for a "contains" pattern.</summary>
    public static string ContainsPattern(string term)
    {
        var builder = new StringBuilder(term.Length + 2).Append('%');
        foreach (var c in term)
        {
            if (c is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.Append('%').ToString();
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex E164();

    [GeneratedRegex("^[0-9]{10,11}$")]
    private static partial Regex TaxNumber();

    [GeneratedRegex(@"^\+?[0-9][0-9 ()\-]{6,19}$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();
}

/// <summary>Opaque keyset cursor (api-strategy §5.1): base64url JSON of the last row's name and id.</summary>
internal sealed record NameCursor(string Name, Guid Id)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(Name, Id)));

    public static NameCursor? TryDecode(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value)) is { N: not null } payload && payload.I != Guid.Empty
                ? new NameCursor(payload.N, payload.I)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Payload(string N, Guid I);
}

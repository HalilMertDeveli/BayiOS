using System.Buffers.Text;
using System.Text.Json;

namespace BayiOS.Modules.Pricing.Application;

internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Conflict(string code, string title) => new(409, code, title);
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Money and quantity rules (data-model §1): decimal only, numeric(18,4) money and numeric(18,3) quantities.</summary>
internal static class PricingNumbers
{
    /// <summary>Exclusive upper bound of numeric(18,4).</summary>
    public const decimal MaxMoney = 100_000_000_000_000m;

    /// <summary>Exclusive upper bound of numeric(18,3).</summary>
    public const decimal MaxQuantity = 1_000_000_000_000_000m;

    public static bool HasScale(decimal value, int decimals) => decimal.Round(value, decimals) == value;

    public static bool IsMoney(decimal value) => value >= 0 && value < MaxMoney && HasScale(value, 4);

    public static bool IsQuantity(decimal value) => value > 0 && value < MaxQuantity && HasScale(value, 3);

    public static bool IsPercent(decimal value) => value is >= 0 and <= 100 && HasScale(value, 2);

    /// <summary>base × (100 − percent) / 100, rounded half away from zero to the money scale.</summary>
    public static decimal ApplyDiscount(decimal basePrice, decimal percent) =>
        decimal.Round(basePrice * (100m - percent) / 100m, 4, MidpointRounding.AwayFromZero);

    public static string? NormalizeCurrency(string? currency, out bool valid)
    {
        valid = true;
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        var code = currency.Trim().ToUpperInvariant();
        valid = code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z');
        return code;
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Opaque keyset cursor over UUIDv7 ids (creation order): base64url JSON of the last row id.</summary>
internal static class IdCursor
{
    public static string Encode(Guid id) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(id)));

    public static bool TryDecode(string value, out Guid id)
    {
        id = Guid.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value));
            if (payload is null || payload.I == Guid.Empty)
            {
                return false;
            }

            id = payload.I;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(Guid I);
}

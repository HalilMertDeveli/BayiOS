using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Catalog.Application;

internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Malformed(string title) => new(400, "request.malformed", title);

    public static AppError Conflict(string code, string title) => new(409, code, title);

    /// <summary>Maps a unique-constraint violation on a known catalog index to a stable 409 code.</summary>
    public static AppError? FromUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            ? pg.ConstraintName switch
            {
                CatalogDbContext.SkuUniqueIndex => Conflict("catalog.sku_taken", "SKU is already used in this tenant"),
                CatalogDbContext.BarcodeUniqueIndex => Conflict("catalog.barcode_taken", "Barcode is already used in this tenant"),
                CatalogDbContext.UnitCodeUniqueIndex => Conflict("catalog.unit_code_taken", "Unit code is already used in this tenant"),
                CatalogDbContext.BrandNameUniqueIndex => Conflict("catalog.brand_name_taken", "Brand name is already used in this tenant"),
                CatalogDbContext.CategoryNameUniqueIndex => Conflict("catalog.category_name_taken", "A sibling category with this name exists"),
                _ => null,
            }
            : null;
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Result of a write that returns the new ETag version.</summary>
internal readonly record struct Versioned<T>(T Body, long Version);

/// <summary>A merge-patch member: <see cref="IsSet"/> is false when the member was absent from the document.</summary>
internal readonly record struct Optional<T>(bool IsSet, T Value)
{
    public static Optional<T> Unset => default;

    public T Or(T current) => IsSet ? Value : current;
}

/// <summary>Reads typed members of a JSON merge-patch document (RFC 7396), collecting validation errors.</summary>
internal sealed class PatchReader(JsonObject document)
{
    public Dictionary<string, string[]> Errors { get; } = [];

    public Optional<string?> String(string name)
    {
        if (!document.TryGetPropertyValue(name, out var node))
        {
            return Optional<string?>.Unset;
        }

        if (node is null)
        {
            return new(true, null);
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return new(true, text);
        }

        Errors[name] = ["Must be a string or null."];
        return Optional<string?>.Unset;
    }

    public Optional<Guid?> Guid(string name)
    {
        var text = String(name);
        if (!text.IsSet || text.Value is null)
        {
            return text.IsSet ? new(true, null) : Optional<Guid?>.Unset;
        }

        if (System.Guid.TryParse(text.Value, out var id))
        {
            return new(true, id);
        }

        Errors[name] = ["Must be a UUID or null."];
        return Optional<Guid?>.Unset;
    }

    public Optional<decimal?> Decimal(string name)
    {
        if (!document.TryGetPropertyValue(name, out var node))
        {
            return Optional<decimal?>.Unset;
        }

        if (node is null)
        {
            return new(true, null);
        }

        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<decimal>(out var number))
        {
            return new(true, number);
        }

        Errors[name] = ["Must be a number."];
        return Optional<decimal?>.Unset;
    }

    public Optional<IReadOnlyDictionary<string, string>?> StringMap(string name)
    {
        if (!document.TryGetPropertyValue(name, out var node))
        {
            return Optional<IReadOnlyDictionary<string, string>?>.Unset;
        }

        if (node is null)
        {
            return new(true, null);
        }

        if (node is JsonObject map)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, member) in map)
            {
                if (member is not JsonValue value || !value.TryGetValue<string>(out var text))
                {
                    Errors[name] = ["Must be an object with string values."];
                    return Optional<IReadOnlyDictionary<string, string>?>.Unset;
                }

                result[key] = text;
            }

            return new(true, result);
        }

        Errors[name] = ["Must be an object with string values."];
        return Optional<IReadOnlyDictionary<string, string>?>.Unset;
    }
}

/// <summary>
/// Opaque keyset cursor (api-strategy §5.1): base64url JSON of the sort name, the last row's sort key and id.
/// A cursor is only valid for the sort it was issued for.
/// </summary>
internal sealed record KeysetCursor(string Sort, string Key, Guid Id)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(Sort, Key, Id)));

    public static bool TryDecode(string value, string expectedSort, out KeysetCursor cursor)
    {
        cursor = new KeysetCursor(expectedSort, string.Empty, System.Guid.Empty);
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value));
            if (payload is null || payload.S != expectedSort || payload.K is null)
            {
                return false;
            }

            cursor = new KeysetCursor(payload.S, payload.K, payload.I);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    public static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);

    private sealed record Payload(string S, string K, Guid I);
}

internal static class CatalogText
{
    /// <summary>Microsecond precision: what PostgreSQL <c>timestamptz</c> stores, so keyset cursors round-trip exactly.</summary>
    public static DateTimeOffset Now(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    /// <summary>Trims; returns null for null/blank input.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    public static IReadOnlyDictionary<string, JsonElement> ReadAttributes(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    public static IReadOnlyDictionary<string, string> ReadOptions(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}

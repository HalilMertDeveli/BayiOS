using System.Buffers.Text;
using System.Text.Json;
using BayiOS.Modules.Imports.Contracts;

namespace BayiOS.Modules.Imports.Application;

internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Conflict(string code, string title) => new(409, code, title);

    public static AppError InvalidState(string status, string action) =>
        Conflict("imports.invalid_state", $"The import is '{status}' and cannot be {action} now");

    public static AppError Forbidden(string title) => new(403, "auth.forbidden", title);
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

internal static class ImportJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static IReadOnlyList<string> ReadStrings(string json) => JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];

    public static IReadOnlyList<IReadOnlyList<string?>> ReadRows(string json) =>
        JsonSerializer.Deserialize<List<List<string?>>>(json, Options)?.Select(r => (IReadOnlyList<string?>)r).ToList() ?? [];

    public static IReadOnlyList<string?> ReadCells(string json) => JsonSerializer.Deserialize<List<string?>>(json, Options) ?? [];

    public static IReadOnlyDictionary<string, string> ReadMap(string? json) =>
        json is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options) ?? [];

    public static IReadOnlyDictionary<string, string?> ReadValues(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options) ?? [];

    public static List<ImportRowError> ReadErrors(string json) => JsonSerializer.Deserialize<List<ImportRowError>>(json, Options) ?? [];
}

/// <summary>Opaque keyset cursor over an integer or UUID key: base64url JSON.</summary>
internal static class ImportCursor
{
    public static string Encode(Guid id) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(id, null)));

    public static string Encode(int rowNumber) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(null, rowNumber)));

    public static bool TryDecodeId(string value, out Guid id)
    {
        id = Decode(value)?.I ?? Guid.Empty;
        return id != Guid.Empty;
    }

    public static bool TryDecodeRow(string value, out int rowNumber)
    {
        rowNumber = Decode(value)?.R ?? 0;
        return rowNumber > 0;
    }

    private static Payload? Decode(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Payload(Guid? I, int? R);
}

/// <summary>Microsecond precision: what PostgreSQL <c>timestamptz</c> stores.</summary>
internal static class ImportClock
{
    public static DateTimeOffset Now(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }
}

/// <summary>Audit action codes of this module (threat-model §7 "Import/Export").</summary>
internal static class ImportAuditActions
{
    public const string Started = "import.started";
    public const string Completed = "import.completed";
    public const string Failed = "import.failed";
    public const string Cancelled = "import.cancelled";
}

/// <summary>Integration event published when an import finishes (domain-model §11, consumed by Notifications later).</summary>
internal static class ImportEvents
{
    public const string Completed = "imports.import_completed.v1";
}

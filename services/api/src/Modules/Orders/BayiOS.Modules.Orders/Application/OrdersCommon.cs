using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using BayiOS.Modules.Inventory.Contracts;

namespace BayiOS.Modules.Orders.Application;

/// <summary>A stable Problem Details code (api-strategy §6) plus order/inventory extensions.</summary>
internal sealed record AppError(
    int Status,
    string Code,
    string Title,
    IDictionary<string, string[]>? Errors = null,
    IReadOnlyList<StockShortage>? Shortages = null,
    bool ConfirmationRequired = false)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError VersionMismatch() =>
        new(412, "concurrency.version_mismatch", "The resource was modified; reload and retry");

    public static AppError Validation(IDictionary<string, string[]> errors) =>
        new(422, "validation.failed", "Validation failed", errors);

    public static AppError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static AppError InvalidTransition(string action, string status) =>
        new(409, "order.invalid_transition", $"Action '{action}' is not allowed for an order in status '{status}'");

    public static AppError NotEditable(string status) =>
        new(409, "order.not_editable", $"Only draft orders can be edited (status '{status}')");

    public static AppError Busy() => new(409, "order.busy", "The order is locked by another operation; retry shortly");

    /// <summary>A field-level permission is missing (the endpoint permission alone is not enough).</summary>
    public static AppError Forbidden(string permission) =>
        new(403, "auth.forbidden", $"Missing permission: {permission}");

    /// <summary>A conditional / own-records grant (roles-permissions §3 footnotes) does not cover this record.</summary>
    public static AppError NotPermitted(string permission, string reason) =>
        new(403, "auth.forbidden", $"{permission}: {reason}");

    public static AppError FromInventory(InventoryError error) =>
        new(error.Status, error.Code, error.Title, error.Errors, error.Shortages, error.ConfirmationRequired);
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Merge-patch view: the typed values plus which members were present (absent = unchanged).</summary>
internal sealed record Patch<T>(T Values, IReadOnlySet<string> Present)
{
    public bool Has(string member) => Present.Contains(member);
}

/// <summary>Opaque keyset cursor (api-strategy §5.1): base64url JSON of the last row's created_at and id.</summary>
internal sealed record OrderCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
        new Payload(CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), Id)));

    public static OrderCursor? TryDecode(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value)) is { T: not null, I: var id } payload
                && id != Guid.Empty
                && DateTimeOffset.TryParseExact(payload.T, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? new OrderCursor(at, id)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Payload(string T, Guid I);
}

internal static class OrderText
{
    public const int MaxNotesLength = 2000;
    public const int MaxReasonLength = 500;
    public const int MaxShortTextLength = 200;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

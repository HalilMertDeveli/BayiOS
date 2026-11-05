using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.Modules.Orders.Application;

/// <summary>A stable Problem Details code (api-strategy §6) plus order/inventory extensions.</summary>
/// <param name="Extensions">Extra problem members (e.g. <c>limitPercent</c>), written as is.</param>
internal sealed record AppError(
    int Status,
    string Code,
    string Title,
    IDictionary<string, string[]>? Errors = null,
    IReadOnlyList<StockShortage>? Shortages = null,
    bool ConfirmationRequired = false,
    IReadOnlyDictionary<string, object>? Extensions = null)
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
        new(403, AuthorizationProblems.Forbidden, $"Missing permission: {permission}");

    /// <summary>
    /// A field-level permission the role holds conditionally (K) is switched off by its tenant rule (e.g. footnote 10
    /// with a 0 % sales discount limit). Same title as <see cref="Forbidden"/>.
    /// </summary>
    public static AppError DisabledByTenantRule(string permission, string condition) =>
        new(403, AuthorizationProblems.DisabledByTenantRule, $"Missing permission: {permission}", Extensions: new Dictionary<string, object>
        {
            ["permission"] = permission,
            ["condition"] = condition,
        });

    // Record-level refusals of K / O grants (roles-permissions §3 footnotes, #111). 403 with a dedicated code each;
    // the titles are the ones clients matched before the codes existed and stay unchanged.

    /// <summary>O / footnote 11: the record was created by someone else.</summary>
    public static AppError NotOwner(string permission, string reason) =>
        new(403, OrderProblems.NotOwner, $"{permission}: {reason}", Extensions: new Dictionary<string, object> { ["permission"] = permission });

    /// <summary>Footnote 11: sales cancels its own orders only while draft / pending approval.</summary>
    public static AppError CancelNotAllowedInStatus(string status) =>
        new(
            403,
            OrderProblems.CancelNotAllowedInStatus,
            $"{Permissions.OrdersCancel}: Only your own draft or pending-approval orders can be cancelled",
            Extensions: new Dictionary<string, object> { ["permission"] = Permissions.OrdersCancel, ["orderStatus"] = status });

    /// <summary>Footnote 10: a line discount above the sales limit (<c>limitPercent</c>).</summary>
    public static AppError DiscountLimitExceeded(decimal limit) =>
        new(
            403,
            OrderProblems.DiscountLimitExceeded,
            $"{Permissions.OrdersPriceOverride}: The discount exceeds the sales limit of {limit} %",
            Extensions: new Dictionary<string, object> { ["permission"] = Permissions.OrdersPriceOverride, ["limitPercent"] = limit });

    /// <summary>Footnote 10: under a discount limit no manual unit price is allowed (<c>limitPercent</c>).</summary>
    public static AppError ManualPriceNotAllowed(decimal limit) =>
        new(
            403,
            OrderProblems.ManualPriceNotAllowed,
            $"{Permissions.OrdersPriceOverride}: A manual unit price is not allowed; only a discount within the sales limit",
            Extensions: new Dictionary<string, object> { ["permission"] = Permissions.OrdersPriceOverride, ["limitPercent"] = limit });

    public static AppError FromInventory(InventoryError error) =>
        new(error.Status, error.Code, error.Title, error.Errors, error.Shortages, error.ConfirmationRequired);
}

/// <summary>Order-specific 403 codes for record-level refusals (api-strategy §6, #111).</summary>
public static class OrderProblems
{
    /// <summary>Editing (O) or cancelling (footnote 11) an order someone else created.</summary>
    public const string NotOwner = "orders.not_owner";

    /// <summary>Footnote 11: cancelling one's own order after it left draft / pending approval.</summary>
    public const string CancelNotAllowedInStatus = "orders.cancel_not_allowed_in_status";

    /// <summary>Footnote 10: a line discount above the tenant's sales limit; the problem carries <c>limitPercent</c>.</summary>
    public const string DiscountLimitExceeded = "orders.discount_limit_exceeded";

    /// <summary>Footnote 10: a manual unit price while only discounts within the limit are allowed; carries <c>limitPercent</c>.</summary>
    public const string ManualPriceNotAllowed = "orders.manual_price_not_allowed";
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

using System.Buffers.Text;
using System.Text.Json;

namespace BayiOS.Modules.Reports.Application;

/// <param name="RetryAfterSeconds">For 429: sent as the <c>Retry-After</c> header.</param>
internal sealed record AppError(int Status, string Code, string Title, IDictionary<string, string[]>? Errors = null, int? RetryAfterSeconds = null)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");

    public static AppError Validation(string field, string message) =>
        new(422, "validation.failed", "Validation failed", new Dictionary<string, string[]> { [field] = [message] });

    public static AppError Forbidden() => new(403, "auth.forbidden", "Not allowed");
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Stable problem codes of the Reports module (api-strategy §6).</summary>
internal static class ReportProblems
{
    /// <summary>
    /// 403: the caller holds reports.read / reports.export only under footnote 4 (sales: order and customer reports)
    /// and asked for another report. Same code as the condition (<c>GET /me</c>).
    /// </summary>
    public const string OrdersAndCustomersOnly = "reports.orders_and_customers_only";

    /// <summary>422: the export would exceed <c>ReportExportLimits.MaxRows</c>; narrow the filters.</summary>
    public const string ExportTooLarge = "reports.export_too_large";

    /// <summary>429: the tenant's hourly export limit is reached (Retry-After). Shared code with the import limiter.</summary>
    public const string RateLimited = "rate_limit.exceeded";
}

/// <summary>Audit action codes of this module (threat-model §7 "Import/Export").</summary>
internal static class ReportAuditActions
{
    /// <summary>An export file was produced and returned (synchronous: requested and downloaded in one call).</summary>
    public const string ExportDownloaded = "export.downloaded";
}

/// <summary>Configuration section <c>Reports</c>.</summary>
public sealed class ReportsOptions
{
    public const string Section = "Reports";

    /// <summary>Exports per tenant per <see cref="ExportWindow"/> (threat-model §6.5: 10 per hour). 0 = unlimited.</summary>
    public int ExportLimit { get; set; } = 10;

    public TimeSpan ExportWindow { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Opaque keyset cursor over the critical-stock SKU order: base64url JSON.</summary>
internal static class ReportCursor
{
    public static string Encode(string sku) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(sku)));

    public static bool TryDecode(string value, out string sku)
    {
        sku = string.Empty;
        try
        {
            sku = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(value))?.S ?? string.Empty;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        return sku.Length > 0;
    }

    private sealed record Payload(string S);
}

/// <summary>Microsecond precision: what PostgreSQL <c>timestamptz</c> stores.</summary>
internal static class ReportClock
{
    public static DateTimeOffset Now(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }
}

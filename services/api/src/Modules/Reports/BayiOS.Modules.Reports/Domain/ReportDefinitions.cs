namespace BayiOS.Modules.Reports.Domain;

/// <summary>The reports of PRD module 09 that exist (REP-02/03, REP-04, REP-05, REP-06, REP-07) plus AI usage.</summary>
internal static class ReportKinds
{
    public const string CriticalStock = "critical_stock";
    public const string Orders = "orders";
    public const string TopProducts = "top_products";
    public const string StockMovements = "stock_movements";
    public const string PickingTasks = "picking_tasks";
    public const string AiUsage = "ai_usage";

    public static readonly IReadOnlyList<string> All = [CriticalStock, Orders, TopProducts, StockMovements, PickingTasks, AiUsage];

    /// <summary>
    /// roles-permissions footnote 4: sales sees and exports order and customer reports only (nothing with cost). The
    /// order-status report and the top-ordered-products report are built from order headers and lines only (sell-side
    /// amounts, no cost, no customer): they are order reports. Stock, movement, task and AI reports are not.
    /// </summary>
    public static readonly IReadOnlyList<string> OrderAndCustomerReports = [Orders, TopProducts];

    public static bool IsOrderOrCustomerReport(string kind) => OrderAndCustomerReports.Contains(kind);
}

internal static class ExportFormats
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";

    public static readonly IReadOnlyList<string> All = [Csv, Xlsx];
}

/// <summary>
/// An inclusive range of Europe/Istanbul calendar days. Turkey has used a fixed UTC+03:00 offset without daylight
/// saving since 2016, so a day is <c>[d 00:00+03:00, d+1 00:00+03:00)</c> — the same rule the AI usage meter uses.
/// </summary>
internal sealed record ReportPeriod(DateOnly From, DateOnly To)
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    /// <summary>Longest range a report accepts (one leap year): keeps every grouped query bounded.</summary>
    public const int MaxDays = 366;

    /// <summary>Default range when the caller gives none: the last 30 days including today.</summary>
    public const int DefaultDays = 30;

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>First instant of <see cref="From"/>, in UTC (Npgsql writes timestamptz parameters only with offset 0).</summary>
    public DateTimeOffset StartUtc => new DateTimeOffset(From.ToDateTime(TimeOnly.MinValue), Offset).ToUniversalTime();

    /// <summary>First instant after <see cref="To"/>, in UTC (exclusive end).</summary>
    public DateTimeOffset EndUtc => new DateTimeOffset(To.AddDays(1).ToDateTime(TimeOnly.MinValue), Offset).ToUniversalTime();

    /// <summary>The Istanbul calendar day of an instant.</summary>
    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    /// <summary>
    /// Resolves the caller's optional bounds: none → the last <see cref="DefaultDays"/> days; only one → a range of
    /// <see cref="DefaultDays"/> days anchored on it. Null with an error message when the range is inverted or too long.
    /// </summary>
    public static ReportPeriod? Resolve(DateOnly? from, DateOnly? to, DateOnly today, out string? error)
    {
        var end = to ?? (from is { } f ? f.AddDays(DefaultDays - 1) : today);
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        error = null;
        if (start > end)
        {
            error = "from must not be after to";
            return null;
        }

        var period = new ReportPeriod(start, end);
        if (period.Days > MaxDays)
        {
            error = $"The range may cover at most {MaxDays} days";
            return null;
        }

        return period;
    }
}

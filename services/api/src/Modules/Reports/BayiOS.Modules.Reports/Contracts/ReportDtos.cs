namespace BayiOS.Modules.Reports.Contracts;

public sealed record ReportPageInfo(string? NextCursor, bool HasMore, int Limit);

/// <summary>REP-03: an active variant whose available stock is at or below its minimum (or the explicit threshold). No prices.</summary>
/// <param name="Id">Variant id.</param>
/// <param name="Available">On hand − reserved (may be negative under the warn/allow stock policy).</param>
/// <param name="MinQuantity">
/// The variant's effective minimum stock in the report's scope (#137; warehouse override, else default; all
/// warehouses: default, else Σ overrides). Null when it has none — such rows appear only with an explicit threshold
/// or with <c>includeOutOfStock</c> (available ≤ 0).
/// </param>
public sealed record CriticalStockItem(
    Guid Id, Guid ProductId, string Sku, string Name, string Unit, decimal OnHand, decimal Reserved, decimal Available, decimal? MinQuantity);

/// <summary>Which rule selected the rows of a critical-stock report.</summary>
public static class CriticalStockModes
{
    /// <summary>Default: each variant against its own minimum stock threshold (available ≤ minimum).</summary>
    public const string Thresholds = "thresholds";

    /// <summary>The request named <c>threshold</c>: every active variant with available ≤ that value (pre-#137 behaviour).</summary>
    public const string Fixed = "fixed";
}

/// <param name="Mode"><c>thresholds</c> (default) or <c>fixed</c> (an explicit <c>threshold</c> was given).</param>
/// <param name="Threshold">The explicit threshold in <c>fixed</c> mode; null in <c>thresholds</c> mode.</param>
/// <param name="WarehouseId">Null = every warehouse summed.</param>
/// <param name="IncludeOutOfStock">Thresholds mode: variants without a minimum are listed too when available ≤ 0.</param>
/// <param name="ThresholdsConfigured">
/// Any variant has a minimum in the scope. False with an empty thresholds-mode report means "nothing is configured",
/// not "nothing is critical".
/// </param>
public sealed record CriticalStockReportResponse(
    string Mode,
    decimal? Threshold,
    Guid? WarehouseId,
    bool IncludeOutOfStock,
    bool ThresholdsConfigured,
    IReadOnlyList<CriticalStockItem> Items,
    ReportPageInfo Page);

/// <param name="GrandTotal">Null when the caller may not see sell prices (prices.read.sell).</param>
public sealed record OrderStatusTotal(string Status, string Currency, long OrderCount, decimal? GrandTotal);

/// <param name="Day">Creation day (Europe/Istanbul).</param>
public sealed record OrderDayTotal(DateOnly Day, string Status, string Currency, long OrderCount, decimal? GrandTotal);

/// <summary>REP-04: orders created in the range by current status, per day. No customer data.</summary>
/// <param name="AmountsVisible">False → every amount is null (the caller lacks prices.read.sell).</param>
public sealed record OrdersReportResponse(DateOnly From, DateOnly To, bool AmountsVisible, IReadOnlyList<OrderStatusTotal> ByStatus, IReadOnlyList<OrderDayTotal> Days);

/// <param name="Id">Variant id.</param>
/// <param name="Revenue">Σ line totals (after discount, excl. VAT); null when hidden or no line had a price.</param>
public sealed record TopProductItem(int Rank, Guid Id, string Sku, string Name, string Unit, string Currency, decimal Quantity, long OrderCount, decimal? Revenue);

/// <summary>REP-06: most ordered variants in the range (approved and later orders, cancelled excluded).</summary>
public sealed record TopProductsReportResponse(DateOnly From, DateOnly To, bool AmountsVisible, IReadOnlyList<TopProductItem> Items);

public sealed record MovementTypeTotal(string Type, long MovementCount, decimal QuantityIn, decimal QuantityOut);

public sealed record MovementDayTotal(DateOnly Day, string Type, long MovementCount, decimal QuantityIn, decimal QuantityOut);

/// <summary>REP-07: ledger movements per day and type (quantities only, never cost).</summary>
public sealed record StockMovementsReportResponse(DateOnly From, DateOnly To, Guid? WarehouseId, IReadOnlyList<MovementTypeTotal> Totals, IReadOnlyList<MovementDayTotal> Days);

public sealed record PickingTaskTotals(long Created, long Completed, long CompletedWithShortage, long Cancelled, decimal? AveragePickMinutes);

public sealed record PickingTaskDayTotal(DateOnly Day, long Created, long Completed, long CompletedWithShortage, long Cancelled, decimal? AveragePickMinutes);

/// <summary>REP-05 (tenant level): pick-task throughput per day. No per-person breakdown (no personal data).</summary>
public sealed record PickingTasksReportResponse(DateOnly From, DateOnly To, Guid? WarehouseId, PickingTaskTotals Totals, IReadOnlyList<PickingTaskDayTotal> Days);

public sealed record AiUsageTotals(
    long ParseRequests,
    long AiCalls,
    long AiFailures,
    long Fallbacks,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);

public sealed record AiUsageDayTotal(
    DateOnly Day,
    long ParseRequests,
    long AiCalls,
    long AiFailures,
    long Fallbacks,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);

/// <summary>AI assistant usage per day (counters only).</summary>
public sealed record AiUsageReportResponse(DateOnly From, DateOnly To, AiUsageTotals Totals, IReadOnlyList<AiUsageDayTotal> Days);

/// <summary>
/// Export request: the format plus the report's own filters (the same meaning as the report's query parameters).
/// Filters a report does not take are rejected (422).
/// </summary>
/// <param name="Format"><c>csv</c> (default) or <c>xlsx</c>.</param>
/// <param name="From">First day (yyyy-MM-dd, Europe/Istanbul); default: 29 days before <paramref name="To"/>.</param>
/// <param name="To">Last day (inclusive); default: today, or 29 days after <paramref name="From"/>.</param>
/// <param name="Statuses">Orders report: order statuses to include (empty = all).</param>
/// <param name="Limit">Top products: how many (1–100, default 20).</param>
/// <param name="Threshold">Critical stock: explicit threshold (fixed mode); omit to use each variant's minimum stock.</param>
/// <param name="IncludeOutOfStock">Critical stock (thresholds mode): also variants without a minimum whose available ≤ 0.</param>
public sealed record ReportExportRequest(
    string? Format = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? WarehouseId = null,
    decimal? Threshold = null,
    IReadOnlyList<string>? Statuses = null,
    int? Limit = null,
    bool? IncludeOutOfStock = null);

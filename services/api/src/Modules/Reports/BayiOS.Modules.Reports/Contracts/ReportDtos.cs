namespace BayiOS.Modules.Reports.Contracts;

public sealed record ReportPageInfo(string? NextCursor, bool HasMore, int Limit);

/// <summary>REP-03: an active variant whose available stock is at or below the threshold. No prices.</summary>
/// <param name="Id">Variant id.</param>
/// <param name="Available">On hand − reserved (may be negative under the warn/allow stock policy).</param>
public sealed record CriticalStockItem(Guid Id, Guid ProductId, string Sku, string Name, string Unit, decimal OnHand, decimal Reserved, decimal Available);

/// <param name="Threshold">The caller's threshold (variants carry no minimum stock yet).</param>
/// <param name="WarehouseId">Null = every warehouse summed.</param>
public sealed record CriticalStockReportResponse(decimal Threshold, Guid? WarehouseId, IReadOnlyList<CriticalStockItem> Items, ReportPageInfo Page);

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
public sealed record ReportExportRequest(
    string? Format = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? WarehouseId = null,
    decimal? Threshold = null,
    IReadOnlyList<string>? Statuses = null,
    int? Limit = null);

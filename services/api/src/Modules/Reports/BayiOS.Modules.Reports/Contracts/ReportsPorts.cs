namespace BayiOS.Modules.Reports.Contracts;

// ------------------------------------------------------------------------------------------------------------------
// Ports. Reports owns no business data and references no other module: the host implements these read-only queries
// over the owning modules' DbContexts (BayiOS.Api/Platform/ReportsPorts.cs) on the request UnitOfWork connection, so
// every query runs in the request transaction under RLS (app.tenant_id) and filters tenant_id explicitly as well.
// Every query is bounded: a date range (≤ 366 days, grouped by day in the database), a page size or an id list.
// Instants are UTC; a "day" is the Europe/Istanbul calendar day (fixed UTC+03:00).
// ------------------------------------------------------------------------------------------------------------------

/// <summary>Summed over every bin of the variant (in one warehouse, or all).</summary>
public sealed record ReportStockLevel(Guid VariantId, decimal OnHand, decimal Reserved);

public sealed record ReportMovementDay(DateOnly Day, string Type, long Movements, decimal QuantityIn, decimal QuantityOut);

/// <summary>Inventory ledger and balances (read-only). Never costs.</summary>
public interface IReportInventorySource
{
    /// <summary>Variants whose available stock (Σ on_hand − Σ reserved) is above <paramref name="threshold"/>.</summary>
    Task<IReadOnlyList<Guid>> VariantsAboveAsync(decimal threshold, Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>Stock of the given variants; a variant without any balance row is absent (= nothing on hand).</summary>
    Task<IReadOnlyList<ReportStockLevel>> LevelsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>Ledger movements per day and type: count, Σ positive quantities, Σ |negative quantities|.</summary>
    Task<IReadOnlyList<ReportMovementDay>> MovementsByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? warehouseId, CancellationToken cancellationToken);

    // ---- Minimum stock thresholds (#137). The effective minimum and its precedence are Inventory's
    // (IStockThresholdQueries): one warehouse → override, else default; all warehouses → default, else Σ overrides. ----

    /// <summary>Effective minimum of each given variant that has one in the scope.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> MinimumsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>Variants with a minimum whose available stock is ≤ that minimum (no balance = 0).</summary>
    Task<IReadOnlyList<Guid>> VariantsAtOrBelowMinimumAsync(Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>Variants whose available stock is above their minimum, or above 0 when they have none.</summary>
    Task<IReadOnlyList<Guid>> VariantsAboveMinimumOrZeroAsync(Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>True when any variant has a minimum in the scope.</summary>
    Task<bool> AnyMinimumAsync(Guid? warehouseId, CancellationToken cancellationToken);
}

/// <param name="Name">Product name, plus the variant name when it has one.</param>
public sealed record ReportVariant(Guid VariantId, Guid ProductId, string Sku, string Name, string Unit);

/// <summary>Catalog (read-only). No prices.</summary>
public interface IReportCatalogSource
{
    /// <summary>
    /// Active variants of active products ordered by SKU, strictly after <paramref name="afterSku"/>, without
    /// <paramref name="excluded"/>, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ReportVariant>> ActiveVariantsAsync(IReadOnlyCollection<Guid> excluded, string? afterSku, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Active variants of active products among <paramref name="included"/>, ordered by SKU, strictly after
    /// <paramref name="afterSku"/>, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ReportVariant>> ActiveVariantsInAsync(IReadOnlyCollection<Guid> included, string? afterSku, int limit, CancellationToken cancellationToken);
}

/// <param name="GrandTotal">Σ grand_total of the orders (in <paramref name="Currency"/>).</param>
public sealed record ReportOrderDay(DateOnly Day, string Status, string Currency, long Orders, decimal GrandTotal);

/// <param name="Quantity">Σ ordered quantity of the variant's lines.</param>
/// <param name="Orders">Distinct orders with the variant.</param>
/// <param name="Revenue">Σ line_total (after discount, before tax); null when no line had a price.</param>
public sealed record ReportTopProduct(Guid VariantId, string Sku, string Name, string Unit, string Currency, decimal Quantity, long Orders, decimal? Revenue);

/// <summary>Orders (read-only). No customer data.</summary>
public interface IReportOrderSource
{
    /// <summary>Every order status the Orders module knows (for filter validation).</summary>
    IReadOnlyList<string> Statuses { get; }

    /// <summary>Statuses that count as "ordered" for the top-products report (approved and later, not cancelled).</summary>
    IReadOnlyList<string> CommittedStatuses { get; }

    /// <summary>Orders created in the range, per creation day, current status and currency. Empty filter = all.</summary>
    Task<IReadOnlyList<ReportOrderDay>> OrdersByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<string> statuses, CancellationToken cancellationToken);

    /// <summary>
    /// Lines of orders created in the range whose status is in <see cref="CommittedStatuses"/>, per variant and
    /// currency, by quantity (then order count, SKU), at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ReportTopProduct>> TopProductsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, int limit, CancellationToken cancellationToken);
}

/// <param name="Created">Pick tasks created that day.</param>
/// <param name="Completed">Pick tasks completed that day (with or without shortage).</param>
/// <param name="PickMinutes">Σ (completed_at − (started_at, else created_at)) in minutes over the day's completions.</param>
public sealed record ReportPickingDay(DateOnly Day, long Created, long Completed, long CompletedWithShortage, long Cancelled, decimal PickMinutes);

/// <summary>Warehouses and picking tasks (read-only). No people.</summary>
public interface IReportWarehouseSource
{
    Task<bool> WarehouseExistsAsync(Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>Pick tasks (not putaway) per day of their own event (created / completed / cancelled).</summary>
    Task<IReadOnlyList<ReportPickingDay>> PickingByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? warehouseId, CancellationToken cancellationToken);
}

public sealed record ReportAiUsageDay(
    DateOnly Day,
    long ParseRequests,
    long AiCalls,
    long AiFailures,
    long Fallbacks,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens);

/// <summary>AI usage counters (read-only; counters only, never message text).</summary>
public interface IReportAiUsageSource
{
    Task<IReadOnlyList<ReportAiUsageDay>> UsageByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken);
}

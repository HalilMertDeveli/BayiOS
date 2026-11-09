using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Reports.Domain;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.Modules.Reports.Application;

/// <param name="Threshold">Explicit threshold (fixed mode); null = each variant's minimum stock (#137).</param>
/// <param name="AfterSku">Keyset position (decoded cursor); null = first page.</param>
/// <param name="IncludeOutOfStock">Thresholds mode only: variants without a minimum also count when available ≤ 0.</param>
internal sealed record CriticalStockQuery(decimal? Threshold, Guid? WarehouseId, int Limit, string? AfterSku, bool IncludeOutOfStock = false);

/// <param name="Statuses">Orders report only; empty = every status.</param>
/// <param name="Limit">Top products only.</param>
internal sealed record PeriodQuery(DateOnly? From, DateOnly? To, Guid? WarehouseId, IReadOnlyList<string> Statuses, int? Limit);

/// <summary>
/// Read-only reports (PRD module 09). Each call checks the caller's report permission for that report
/// (<see cref="AuthorizeAsync"/>: footnote 4 limits sales to order reports), validates and bounds the filters, and
/// reads through the host's ports (RLS + explicit tenant filter). Amounts appear only with <c>prices.read.sell</c>.
/// Nothing here exposes cost, customers or people.
/// </summary>
internal sealed class ReportService(
    IAuthorizationContext authorization,
    IReportCatalogSource catalog,
    IReportInventorySource inventory,
    IReportOrderSource orders,
    IReportWarehouseSource warehouses,
    IReportAiUsageSource aiUsage,
    TimeProvider clock)
{
    public const int DefaultCriticalPageSize = 100;
    public const int MaxCriticalPageSize = 500;
    public const int DefaultTopProducts = 20;
    public const int MaxTopProducts = 100;
    public const decimal MaxThreshold = 1_000_000_000m;

    /// <summary>Page size used while collecting every critical-stock row for an export.</summary>
    private const int ExportPageSize = 1_000;

    /// <summary>
    /// Report permission check. <paramref name="export"/> selects reports.export instead of reports.read. A
    /// record-level grant (sales, footnote 4) is limited to order and customer reports; the AI usage report (gated
    /// by billing.read: owner, admin) additionally needs the report permission unconditionally.
    /// </summary>
    public async Task<AppError?> AuthorizeAsync(string kind, bool export, CancellationToken cancellationToken)
    {
        var permission = export ? Permissions.ReportsExport : Permissions.ReportsRead;
        var scope = await authorization.ScopeAsync(permission, cancellationToken);
        return scope switch
        {
            PermissionScope.All => null,
            PermissionScope.Restricted when kind != ReportKinds.AiUsage && ReportKinds.IsOrderOrCustomerReport(kind) => null,
            PermissionScope.Restricted when kind != ReportKinds.AiUsage =>
                new AppError(403, ReportProblems.OrdersAndCustomersOnly, "This role may only see order and customer reports"),
            _ => AppError.Forbidden(),
        };
    }

    // ---- REP-03 critical stock ----

    public async Task<AppResult<CriticalStockReportResponse>> CriticalStockAsync(CriticalStockQuery query, bool export, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ReportKinds.CriticalStock, export, cancellationToken) is { } denied)
        {
            return denied;
        }

        var threshold = query.Threshold;
        if (threshold is { } t && (t < 0m || t > MaxThreshold || decimal.Round(t, 3) != t))
        {
            return AppError.Validation("threshold", $"threshold must be between 0 and {MaxThreshold:0} with at most 3 decimals");
        }

        if (threshold is not null && query.IncludeOutOfStock)
        {
            return AppError.Validation("includeOutOfStock", "includeOutOfStock applies only without an explicit threshold");
        }

        if (await CheckWarehouseAsync(query.WarehouseId, cancellationToken) is { } missing)
        {
            return missing;
        }

        // Candidate selection, evaluated in Inventory (no cross-module join):
        //  fixed      — every active variant except those above the threshold (never-stocked variants count as 0);
        //  thresholds — only variants at or below their own minimum; with includeOutOfStock every active variant
        //               except those above their minimum (or above 0 when they have none).
        var mode = threshold is null ? CriticalStockModes.Thresholds : CriticalStockModes.Fixed;
        IReadOnlyList<Guid>? excluded = null;
        IReadOnlyList<Guid>? included = null;
        if (threshold is { } fixedThreshold)
        {
            excluded = await inventory.VariantsAboveAsync(fixedThreshold, query.WarehouseId, cancellationToken);
        }
        else if (query.IncludeOutOfStock)
        {
            excluded = await inventory.VariantsAboveMinimumOrZeroAsync(query.WarehouseId, cancellationToken);
        }
        else
        {
            included = await inventory.VariantsAtOrBelowMinimumAsync(query.WarehouseId, cancellationToken);
        }

        var configured = await inventory.AnyMinimumAsync(query.WarehouseId, cancellationToken);
        var items = new List<CriticalStockItem>();
        var after = query.AfterSku;
        var pageSize = export ? ExportPageSize : query.Limit;
        var hasMore = false;
        do
        {
            if (included is { Count: 0 })
            {
                break;
            }

            var variants = included is not null
                ? await catalog.ActiveVariantsInAsync(included, after, pageSize + 1, cancellationToken)
                : await catalog.ActiveVariantsAsync(excluded!, after, pageSize + 1, cancellationToken);
            hasMore = variants.Count > pageSize;
            var page = variants.Take(pageSize).ToList();
            var ids = page.Select(v => v.VariantId).ToList();
            var levels = (await inventory.LevelsAsync(ids, query.WarehouseId, cancellationToken)).ToDictionary(l => l.VariantId);
            var minimums = await inventory.MinimumsAsync(ids, query.WarehouseId, cancellationToken);
            foreach (var variant in page)
            {
                var level = levels.GetValueOrDefault(variant.VariantId);
                var onHand = level?.OnHand ?? 0m;
                var reserved = level?.Reserved ?? 0m;
                var available = onHand - reserved;
                decimal? min = minimums.TryGetValue(variant.VariantId, out var m) ? m : null;

                // Stock or thresholds may have changed between the reads (READ COMMITTED); never list a row that is
                // not critical under the report's rule.
                if (IsCritical(available, min, threshold, query.IncludeOutOfStock))
                {
                    items.Add(new CriticalStockItem(variant.VariantId, variant.ProductId, variant.Sku, variant.Name, variant.Unit, onHand, reserved, available, min));
                }
            }

            after = page.Count > 0 ? page[^1].Sku : after;
            if (export && items.Count > Export.ReportExportLimits.MaxRows)
            {
                return TooLarge();
            }
        }
        while (export && hasMore);

        var cursor = !export && hasMore && after is not null ? ReportCursor.Encode(after) : null;
        return new CriticalStockReportResponse(
            mode,
            threshold,
            query.WarehouseId,
            query.IncludeOutOfStock,
            configured,
            items,
            new ReportPageInfo(cursor, cursor is not null, export ? items.Count : query.Limit));
    }

    /// <summary>
    /// REP-03 rule for one variant: fixed → available ≤ threshold; thresholds → available ≤ its minimum, or (with
    /// <paramref name="includeOutOfStock"/>) available ≤ 0 when it has none.
    /// </summary>
    internal static bool IsCritical(decimal available, decimal? minimum, decimal? threshold, bool includeOutOfStock) =>
        threshold is { } t
            ? available <= t
            : minimum is { } min ? available <= min : includeOutOfStock && available <= 0m;

    // ---- REP-04 orders by status and day ----

    public async Task<AppResult<OrdersReportResponse>> OrdersAsync(PeriodQuery query, bool export, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ReportKinds.Orders, export, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (Period(query) is not { } period)
        {
            return PeriodError(query);
        }

        var unknown = query.Statuses.Where(s => !orders.Statuses.Contains(s)).ToList();
        if (unknown.Count > 0)
        {
            return AppError.Validation("status", $"Unknown order status: {string.Join(", ", unknown)}. Allowed: {string.Join(", ", orders.Statuses)}");
        }

        var visible = await AmountsVisibleAsync(cancellationToken);
        var rows = await orders.OrdersByDayAsync(period.StartUtc, period.EndUtc, [.. query.Statuses.Distinct()], cancellationToken);
        int Rank(string status) => orders.Statuses.ToList().IndexOf(status);

        var byStatus = rows
            .GroupBy(r => (r.Status, r.Currency))
            .OrderBy(g => Rank(g.Key.Status)).ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new OrderStatusTotal(g.Key.Status, g.Key.Currency, g.Sum(r => r.Orders), visible ? g.Sum(r => r.GrandTotal) : null))
            .ToList();
        var days = rows
            .OrderBy(r => r.Day).ThenBy(r => Rank(r.Status)).ThenBy(r => r.Currency, StringComparer.Ordinal)
            .Select(r => new OrderDayTotal(r.Day, r.Status, r.Currency, r.Orders, visible ? r.GrandTotal : null))
            .ToList();
        return new OrdersReportResponse(period.From, period.To, visible, byStatus, days);
    }

    // ---- REP-06 most ordered products ----

    public async Task<AppResult<TopProductsReportResponse>> TopProductsAsync(PeriodQuery query, bool export, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ReportKinds.TopProducts, export, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (Period(query) is not { } period)
        {
            return PeriodError(query);
        }

        var limit = query.Limit ?? DefaultTopProducts;
        if (limit is < 1 or > MaxTopProducts)
        {
            return AppError.Validation("limit", $"limit must be between 1 and {MaxTopProducts}");
        }

        var visible = await AmountsVisibleAsync(cancellationToken);
        var rows = await orders.TopProductsAsync(period.StartUtc, period.EndUtc, limit, cancellationToken);
        var items = rows
            .Select((r, i) => new TopProductItem(i + 1, r.VariantId, r.Sku, r.Name, r.Unit, r.Currency, r.Quantity, r.Orders, visible ? r.Revenue : null))
            .ToList();
        return new TopProductsReportResponse(period.From, period.To, visible, items);
    }

    // ---- REP-07 stock movements ----

    public async Task<AppResult<StockMovementsReportResponse>> StockMovementsAsync(PeriodQuery query, bool export, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ReportKinds.StockMovements, export, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (Period(query) is not { } period)
        {
            return PeriodError(query);
        }

        if (await CheckWarehouseAsync(query.WarehouseId, cancellationToken) is { } missing)
        {
            return missing;
        }

        var rows = await inventory.MovementsByDayAsync(period.StartUtc, period.EndUtc, query.WarehouseId, cancellationToken);
        var totals = rows
            .GroupBy(r => r.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MovementTypeTotal(g.Key, g.Sum(r => r.Movements), g.Sum(r => r.QuantityIn), g.Sum(r => r.QuantityOut)))
            .ToList();
        var days = rows
            .OrderBy(r => r.Day).ThenBy(r => r.Type, StringComparer.Ordinal)
            .Select(r => new MovementDayTotal(r.Day, r.Type, r.Movements, r.QuantityIn, r.QuantityOut))
            .ToList();
        return new StockMovementsReportResponse(period.From, period.To, query.WarehouseId, totals, days);
    }

    // ---- REP-05 picking task throughput ----

    public async Task<AppResult<PickingTasksReportResponse>> PickingTasksAsync(PeriodQuery query, bool export, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ReportKinds.PickingTasks, export, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (Period(query) is not { } period)
        {
            return PeriodError(query);
        }

        if (await CheckWarehouseAsync(query.WarehouseId, cancellationToken) is { } missing)
        {
            return missing;
        }

        var rows = (await warehouses.PickingByDayAsync(period.StartUtc, period.EndUtc, query.WarehouseId, cancellationToken)).OrderBy(r => r.Day).ToList();
        var completed = rows.Sum(r => r.Completed);
        var totals = new PickingTaskTotals(
            rows.Sum(r => r.Created),
            completed,
            rows.Sum(r => r.CompletedWithShortage),
            rows.Sum(r => r.Cancelled),
            Average(rows.Sum(r => r.PickMinutes), completed));
        var days = rows
            .Select(r => new PickingTaskDayTotal(r.Day, r.Created, r.Completed, r.CompletedWithShortage, r.Cancelled, Average(r.PickMinutes, r.Completed)))
            .ToList();
        return new PickingTasksReportResponse(period.From, period.To, query.WarehouseId, totals, days);
    }

    // ---- AI usage ----

    public async Task<AppResult<AiUsageReportResponse>> AiUsageAsync(PeriodQuery query, bool export, CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(ReportKinds.AiUsage, export, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (Period(query) is not { } period)
        {
            return PeriodError(query);
        }

        var rows = (await aiUsage.UsageByDayAsync(period.StartUtc, period.EndUtc, cancellationToken)).OrderBy(r => r.Day).ToList();
        var totals = new AiUsageTotals(
            rows.Sum(r => r.ParseRequests),
            rows.Sum(r => r.AiCalls),
            rows.Sum(r => r.AiFailures),
            rows.Sum(r => r.Fallbacks),
            rows.Sum(r => r.InputTokens),
            rows.Sum(r => r.OutputTokens),
            rows.Sum(r => r.CacheReadTokens),
            rows.Sum(r => r.CacheWriteTokens));
        var days = rows
            .Select(r => new AiUsageDayTotal(
                r.Day, r.ParseRequests, r.AiCalls, r.AiFailures, r.Fallbacks, r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens))
            .ToList();
        return new AiUsageReportResponse(period.From, period.To, totals, days);
    }

    // ---- helpers ----

    public DateOnly Today => ReportPeriod.DayOf(clock.GetUtcNow());

    private ReportPeriod? Period(PeriodQuery query) => ReportPeriod.Resolve(query.From, query.To, Today, out _);

    private AppError PeriodError(PeriodQuery query)
    {
        _ = ReportPeriod.Resolve(query.From, query.To, Today, out var error);
        return AppError.Validation("from", error ?? "Invalid range");
    }

    private async Task<AppError?> CheckWarehouseAsync(Guid? warehouseId, CancellationToken cancellationToken) =>
        warehouseId is { } id && !await warehouses.WarehouseExistsAsync(id, cancellationToken) ? AppError.NotFound() : null;

    /// <summary>Amounts are sell-side (order totals, line totals): shown only with an unconditional prices.read.sell.</summary>
    private async Task<bool> AmountsVisibleAsync(CancellationToken cancellationToken) =>
        await authorization.ScopeAsync(Permissions.PricesReadSell, cancellationToken) == PermissionScope.All;

    private static decimal? Average(decimal minutes, long count) => count == 0 ? null : decimal.Round(minutes / count, 1);

    private static AppError TooLarge() => new(
        422,
        ReportProblems.ExportTooLarge,
        $"The export would have more than {Export.ReportExportLimits.MaxRows} rows; narrow the filters (warehouse, threshold)");
}

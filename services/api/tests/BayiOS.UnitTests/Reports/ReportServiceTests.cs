using BayiOS.Modules.Reports.Application;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Reports.Domain;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.UnitTests.Reports;

/// <summary>
/// #53 — report rules without a database: footnote 4 (sales: order reports only), amounts only with an
/// unconditional prices.read.sell, AI usage only with an unconditional report permission, the Istanbul day range.
/// </summary>
public class ReportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 22, 30, 0, TimeSpan.Zero); // already 6 Nov in Istanbul

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ReportKinds.Orders, true)]
    [InlineData(ReportKinds.TopProducts, true)]
    [InlineData(ReportKinds.CriticalStock, false)]
    [InlineData(ReportKinds.StockMovements, false)]
    [InlineData(ReportKinds.PickingTasks, false)]
    public async Task A_record_level_report_grant_covers_order_reports_only(string kind, bool allowed)
    {
        foreach (var export in new[] { false, true })
        {
            var authorization = new FakeAuthorization { [export ? Permissions.ReportsExport : Permissions.ReportsRead] = PermissionScope.Restricted };
            var error = await Service(authorization).AuthorizeAsync(kind, export, Ct);
            Assert.Equal(allowed ? null : "reports.orders_and_customers_only", error?.Code);
            Assert.Contains(export ? Permissions.ReportsExport : Permissions.ReportsRead, authorization.Checked);
        }
    }

    [Theory]
    [InlineData(PermissionScope.All, null)]
    [InlineData(PermissionScope.Restricted, "auth.forbidden")]
    [InlineData(PermissionScope.None, "auth.forbidden")]
    public async Task Ai_usage_needs_the_report_permission_without_condition(PermissionScope scope, string? code)
    {
        var authorization = new FakeAuthorization { [Permissions.ReportsRead] = scope };
        Assert.Equal(code, (await Service(authorization).AuthorizeAsync(ReportKinds.AiUsage, export: false, Ct))?.Code);
    }

    [Theory]
    [InlineData(PermissionScope.All, true)]
    [InlineData(PermissionScope.Restricted, false)]
    [InlineData(PermissionScope.None, false)]
    public async Task Amounts_are_hidden_without_an_unconditional_sell_price_permission(PermissionScope priceScope, bool visible)
    {
        var authorization = new FakeAuthorization { [Permissions.ReportsRead] = PermissionScope.All, [Permissions.PricesReadSell] = priceScope };
        var orders = new FakeOrders
        {
            Days = [new(new DateOnly(2026, 11, 6), "approved", "TRY", 2, 150m), new(new DateOnly(2026, 11, 6), "draft", "TRY", 1, 20m)],
            Top = [new(Guid.NewGuid(), "SKU-1", "Ürün", "adet", "TRY", 5m, 2, 99.5m)],
        };
        var service = Service(authorization, orders);

        var report = (await service.OrdersAsync(new PeriodQuery(null, null, null, [], null), export: false, Ct)).Value!;
        Assert.Equal(visible, report.AmountsVisible);
        Assert.Equal(visible ? 150m : null, report.ByStatus.Single(s => s.Status == "approved").GrandTotal);
        Assert.All(report.Days, d => Assert.Equal(visible, d.GrandTotal is not null));
        Assert.Equal(["draft", "approved"], report.ByStatus.Select(s => s.Status)); // status order of the Orders module

        var top = (await service.TopProductsAsync(new PeriodQuery(null, null, null, [], 10), export: false, Ct)).Value!;
        Assert.Equal(visible ? 99.5m : null, top.Items.Single().Revenue);

        var table = ReportTables.Orders(report);
        Assert.Equal(visible ? 5 : 4, table.Columns.Count);
        Assert.Equal(visible, table.Columns.Any(c => c.Header == "Genel Toplam"));
        Assert.Equal(visible, ReportTables.TopProducts(top).Columns.Any(c => c.Header.StartsWith("Tutar", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_default_range_is_the_last_30_istanbul_days_and_ranges_are_bounded()
    {
        var orders = new FakeOrders();
        var service = Service(new FakeAuthorization { [Permissions.ReportsRead] = PermissionScope.All }, orders);

        var report = (await service.OrdersAsync(new PeriodQuery(null, null, null, [], null), export: false, Ct)).Value!;
        Assert.Equal((new DateOnly(2026, 10, 8), new DateOnly(2026, 11, 6)), (report.From, report.To));
        Assert.Equal((new DateTimeOffset(2026, 10, 7, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero)), orders.LastRange);

        Assert.Equal(new DateOnly(2026, 2, 1), (await service.OrdersAsync(new PeriodQuery(new DateOnly(2026, 1, 3), null, null, [], null), false, Ct)).Value!.To);
        Assert.Equal(422, (await service.OrdersAsync(new PeriodQuery(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 2), null, [], null), false, Ct)).Error!.Status);
        Assert.Equal(200, (await service.OrdersAsync(new PeriodQuery(new DateOnly(2025, 1, 2), new DateOnly(2026, 1, 2), null, [], null), false, Ct)).Error?.Status ?? 200);
        Assert.Equal(422, (await service.OrdersAsync(new PeriodQuery(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), null, [], null), false, Ct)).Error!.Status);
        Assert.Equal(422, (await service.OrdersAsync(new PeriodQuery(null, null, null, ["lost"], null), false, Ct)).Error!.Status);
    }

    [Theory]
    [InlineData("2026-11-05T20:59:59Z", "2026-11-05")]
    [InlineData("2026-11-05T21:00:00Z", "2026-11-06")]
    public void Days_are_istanbul_calendar_days(string instant, string day) =>
        Assert.Equal(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), ReportPeriod.DayOf(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task Critical_stock_counts_unstocked_variants_as_zero_and_never_lists_a_row_above_the_threshold()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var catalog = new FakeCatalog([new(a, Guid.NewGuid(), "A", "Ürün A", "adet"), new(b, Guid.NewGuid(), "B", "Ürün B", "adet"), new(c, Guid.NewGuid(), "C", "Ürün C", "adet")]);
        var inventory = new FakeInventory { Levels = [new(b, 10m, 9m), new(c, 50m, 0m)] }; // c moved above after the first read
        var service = Service(new FakeAuthorization { [Permissions.ReportsRead] = PermissionScope.All }, catalog: catalog, inventory: inventory);

        var report = (await service.CriticalStockAsync(new CriticalStockQuery(1m, null, 2, null), export: false, Ct)).Value!;
        Assert.Equal(["A", "B"], report.Items.Select(i => i.Sku));
        Assert.Equal((0m, 0m, 0m), (report.Items[0].OnHand, report.Items[0].Reserved, report.Items[0].Available));
        Assert.True(report.Page.HasMore);
        Assert.True(ReportCursor.TryDecode(report.Page.NextCursor!, out var after));
        Assert.Equal("B", after);

        var next = (await service.CriticalStockAsync(new CriticalStockQuery(1m, null, 2, after), export: false, Ct)).Value!;
        Assert.Empty(next.Items);
        Assert.False(next.Page.HasMore);
        Assert.Equal(422, (await service.CriticalStockAsync(new CriticalStockQuery(-1m, null, 2, null), false, Ct)).Error!.Status);
        Assert.Equal(CriticalStockModes.Fixed, report.Mode);
        Assert.Equal(1m, report.Threshold);
    }

    [Fact]
    public async Task Critical_stock_defaults_to_each_variants_minimum_and_rechecks_rows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var catalog = new FakeCatalog([new(a, Guid.NewGuid(), "A", "Ürün A", "adet"), new(b, Guid.NewGuid(), "B", "Ürün B", "adet"), new(c, Guid.NewGuid(), "C", "Ürün C", "adet")]);

        // a: min 5, never stocked → critical (0 ≤ 5). b: min 3, available 1 → critical. c: no minimum → never listed,
        // even though the stale selection named it.
        var inventory = new FakeInventory
        {
            Levels = [new(b, 4m, 3m), new(c, 0m, 0m)],
            Minimums = new() { [a] = 5m, [b] = 3m },
            AtOrBelow = [a, b, c],
        };
        var service = Service(new FakeAuthorization { [Permissions.ReportsRead] = PermissionScope.All }, catalog: catalog, inventory: inventory);

        var report = (await service.CriticalStockAsync(new CriticalStockQuery(null, null, 10, null), export: false, Ct)).Value!;
        Assert.Equal(CriticalStockModes.Thresholds, report.Mode);
        Assert.Null(report.Threshold);
        Assert.True(report.ThresholdsConfigured);
        Assert.Equal(["A", "B"], report.Items.Select(i => i.Sku));
        Assert.Equal([5m, 3m], report.Items.Select(i => i.MinQuantity!.Value));
        Assert.Equal(1m, report.Items[1].Available);

        // includeOutOfStock: variants without a minimum count when available ≤ 0; nothing excluded here.
        var withOut = (await service.CriticalStockAsync(new CriticalStockQuery(null, null, 10, null, IncludeOutOfStock: true), export: false, Ct)).Value!;
        Assert.Equal(["A", "B", "C"], withOut.Items.Select(i => i.Sku));
        Assert.Null(withOut.Items[2].MinQuantity);

        // threshold + includeOutOfStock is contradictory.
        Assert.Equal(422, (await service.CriticalStockAsync(new CriticalStockQuery(1m, null, 10, null, IncludeOutOfStock: true), false, Ct)).Error!.Status);
    }

    [Fact]
    public async Task Critical_stock_without_any_minimum_is_empty_and_says_nothing_is_configured()
    {
        var a = Guid.NewGuid();
        var service = Service(
            new FakeAuthorization { [Permissions.ReportsRead] = PermissionScope.All },
            catalog: new FakeCatalog([new(a, Guid.NewGuid(), "A", "Ürün A", "adet")]),
            inventory: new FakeInventory());

        var report = (await service.CriticalStockAsync(new CriticalStockQuery(null, null, 10, null), export: false, Ct)).Value!;
        Assert.Empty(report.Items);
        Assert.False(report.ThresholdsConfigured);
        Assert.False(report.Page.HasMore);
    }

    [Theory]
    [InlineData(0, null, null, false, false)] // no minimum, not counted
    [InlineData(0, null, null, true, true)] // out of stock counts with includeOutOfStock
    [InlineData(1, null, null, true, false)]
    [InlineData(5, 5, null, false, true)] // equal to the minimum is critical
    [InlineData(6, 5, null, false, false)]
    [InlineData(-2, 0, null, false, true)]
    [InlineData(3, 10, 2, false, false)] // an explicit threshold wins over the minimum
    [InlineData(2, null, 2, false, true)]
    public void Critical_rule(int available, int? minimum, int? threshold, bool includeOutOfStock, bool critical) =>
        Assert.Equal(critical, ReportService.IsCritical(available, minimum, threshold, includeOutOfStock));

    [Fact]
    public void Export_threshold_column_is_the_explicit_threshold_or_the_variants_minimum()
    {
        var item = new CriticalStockItem(Guid.NewGuid(), Guid.NewGuid(), "A", "Ürün A", "adet", 1m, 0m, 1m, 4m);
        var none = item with { Sku = "B", MinQuantity = null };
        var page = new ReportPageInfo(null, false, 2);
        var byMinimum = ReportTables.CriticalStock(new CriticalStockReportResponse(CriticalStockModes.Thresholds, null, null, true, true, [item, none], page), "2026-11-07");
        Assert.Equal([4m, null], byMinimum.Rows.Select(r => r[^1]));
        var fixedThreshold = ReportTables.CriticalStock(new CriticalStockReportResponse(CriticalStockModes.Fixed, 2m, null, false, true, [item], page), "2026-11-07");
        Assert.Equal(2m, fixedThreshold.Rows.Single()[^1]);
    }

    private static ReportService Service(
        FakeAuthorization authorization, FakeOrders? orders = null, FakeCatalog? catalog = null, FakeInventory? inventory = null) =>
        new(authorization, catalog ?? new FakeCatalog([]), inventory ?? new FakeInventory(), orders ?? new FakeOrders(), new FakeWarehouses(), new FakeAiUsage(), new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAuthorization : IAuthorizationContext
    {
        private readonly Dictionary<string, PermissionScope> _scopes = [];

        public List<string> Checked { get; } = [];

        public IReadOnlyList<string> UncheckedRestrictedGrants => [];

        public PermissionScope this[string permission]
        {
            get => _scopes.GetValueOrDefault(permission, PermissionScope.None);
            set => _scopes[permission] = value;
        }

        public ValueTask<PermissionScope> ScopeAsync(string permission, CancellationToken cancellationToken)
        {
            Checked.Add(permission);
            return ValueTask.FromResult(this[permission]);
        }

        public async ValueTask<bool> CanAsync(string permission, bool recordCondition, CancellationToken cancellationToken) =>
            await ScopeAsync(permission, cancellationToken) switch { PermissionScope.All => true, PermissionScope.Restricted => recordCondition, _ => false };

        public Task<TenantAccessRules> RulesAsync(CancellationToken cancellationToken) => Task.FromResult(TenantAccessRules.Defaults);

        public ValueTask<IReadOnlyList<PermissionAccess>> EffectivePermissionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PermissionAccess>>([]);

        public ValueTask<PermissionScope> GateAsync(IReadOnlyList<string> anyOf, CancellationToken cancellationToken) => ValueTask.FromResult(anyOf.Max(p => this[p]));
    }

    private sealed class FakeOrders : IReportOrderSource
    {
        public IReadOnlyList<ReportOrderDay> Days { get; init; } = [];

        public IReadOnlyList<ReportTopProduct> Top { get; init; } = [];

        public (DateTimeOffset, DateTimeOffset) LastRange { get; private set; }

        public IReadOnlyList<string> Statuses { get; } = ["draft", "pending_approval", "approved", "picking", "packed", "shipped", "completed", "cancelled"];

        public IReadOnlyList<string> CommittedStatuses { get; } = ["approved", "picking", "packed", "shipped", "completed"];

        public Task<IReadOnlyList<ReportOrderDay>> OrdersByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<string> statuses, CancellationToken cancellationToken)
        {
            LastRange = (startUtc, endUtc);
            return Task.FromResult(Days);
        }

        public Task<IReadOnlyList<ReportTopProduct>> TopProductsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportTopProduct>>([.. Top.Take(limit)]);
    }

    private sealed class FakeCatalog(IReadOnlyList<ReportVariant> variants) : IReportCatalogSource
    {
        public Task<IReadOnlyList<ReportVariant>> ActiveVariantsAsync(IReadOnlyCollection<Guid> excluded, string? afterSku, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportVariant>>([.. variants
                .Where(v => !excluded.Contains(v.VariantId) && (afterSku is null || string.CompareOrdinal(v.Sku, afterSku) > 0))
                .OrderBy(v => v.Sku, StringComparer.Ordinal)
                .Take(limit)]);

        public Task<IReadOnlyList<ReportVariant>> ActiveVariantsInAsync(IReadOnlyCollection<Guid> included, string? afterSku, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportVariant>>([.. variants
                .Where(v => included.Contains(v.VariantId) && (afterSku is null || string.CompareOrdinal(v.Sku, afterSku) > 0))
                .OrderBy(v => v.Sku, StringComparer.Ordinal)
                .Take(limit)]);
    }

    private sealed class FakeInventory : IReportInventorySource
    {
        public IReadOnlyList<ReportStockLevel> Levels { get; init; } = [];

        public Dictionary<Guid, decimal> Minimums { get; init; } = [];

        /// <summary>What the "critical by minimum" selection returned when it was read (may be stale by the re-check).</summary>
        public IReadOnlyList<Guid> AtOrBelow { get; init; } = [];

        public IReadOnlyList<Guid> AboveOrInStock { get; init; } = [];

        public Task<IReadOnlyDictionary<Guid, decimal>> MinimumsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(Minimums.Where(m => variantIds.Contains(m.Key)).ToDictionary());

        public Task<IReadOnlyList<Guid>> VariantsAtOrBelowMinimumAsync(Guid? warehouseId, CancellationToken cancellationToken) => Task.FromResult(AtOrBelow);

        public Task<IReadOnlyList<Guid>> VariantsAboveMinimumOrZeroAsync(Guid? warehouseId, CancellationToken cancellationToken) => Task.FromResult(AboveOrInStock);

        public Task<bool> AnyMinimumAsync(Guid? warehouseId, CancellationToken cancellationToken) => Task.FromResult(Minimums.Count > 0);

        // Reads "before" the stock change: nothing above the threshold yet.
        public Task<IReadOnlyList<Guid>> VariantsAboveAsync(decimal threshold, Guid? warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<ReportStockLevel>> LevelsAsync(IReadOnlyCollection<Guid> variantIds, Guid? warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportStockLevel>>([.. Levels.Where(l => variantIds.Contains(l.VariantId))]);

        public Task<IReadOnlyList<ReportMovementDay>> MovementsByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportMovementDay>>([]);
    }

    private sealed class FakeWarehouses : IReportWarehouseSource
    {
        public Task<bool> WarehouseExistsAsync(Guid warehouseId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<ReportPickingDay>> PickingByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? warehouseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportPickingDay>>([]);
    }

    private sealed class FakeAiUsage : IReportAiUsageSource
    {
        public Task<IReadOnlyList<ReportAiUsageDay>> UsageByDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReportAiUsageDay>>([]);
    }
}

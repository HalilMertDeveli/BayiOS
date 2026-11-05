using System.Globalization;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Reports.Domain;
using BayiOS.Modules.Reports.Export;
using BayiOS.Modules.Reports.Infrastructure.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BayiOS.Modules.Reports.Application;

/// <summary>
/// CSV / XLSX export of every report (PRD REP-08; never behind a paywall — roles-permissions §1.5 and §5: exports are
/// gated by reports.export only, no entitlement). Synchronous and bounded: ≤ <see cref="ReportExportLimits.MaxRows"/>
/// rows, built in memory and returned as one file. Per tenant at most <see cref="ReportsOptions.ExportLimit"/> exports
/// per <see cref="ReportsOptions.ExportWindow"/> (threat-model §6.5: 10/hour), counted in <c>reports.export_requests</c>
/// so every API instance shares the limit. Each export writes one audit event with the report, format and row count —
/// never report data.
/// </summary>
internal sealed class ReportExportService(
    ReportService reports,
    ReportsDbContext db,
    IAuditLog audit,
    IRequestContext requestContext,
    IOptions<ReportsOptions> options,
    TimeProvider clock)
{
    /// <summary>Export log rows older than this are deleted on the next export of the tenant (rate limiting needs one hour).</summary>
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(1);

    public async Task<AppResult<ExportFile>> ExportAsync(string kind, ReportExportRequest request, CancellationToken cancellationToken)
    {
        // Permission first (403 before 422/429), then filters, then the limit: rejected requests never take a slot.
        if (await reports.AuthorizeAsync(kind, export: true, cancellationToken) is { } denied)
        {
            return denied;
        }

        var format = request.Format ?? ExportFormats.Csv;
        if (!ExportFormats.All.Contains(format))
        {
            return AppError.Validation("format", "format must be csv or xlsx");
        }

        if (UnusedFilter(kind, request) is { } unused)
        {
            return AppError.Validation(unused, $"{unused} is not a filter of this report");
        }

        if (await CheckRateAsync(cancellationToken) is { } limited)
        {
            return limited;
        }

        var built = await BuildAsync(kind, request, cancellationToken);
        if (built.Error is { } error)
        {
            return error;
        }

        var table = built.Value!;
        if (table.Rows.Count > ReportExportLimits.MaxRows)
        {
            return new AppError(422, ReportProblems.ExportTooLarge, $"The export would have more than {ReportExportLimits.MaxRows} rows; narrow the filters");
        }

        var file = format == ExportFormats.Xlsx ? XlsxReportWriter.Write(table) : CsvReportWriter.Write(table);
        var now = ReportClock.Now(clock);
        var record = ExportRequestRecord.Create(TenantId, kind, format, table.Rows.Count, now);
        db.ExportRequests.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        var keep = options.Value.ExportWindow > LogRetention ? options.Value.ExportWindow : LogRetention;
        await db.Database.ExecuteSqlAsync($"DELETE FROM reports.export_requests WHERE tenant_id = {TenantId} AND created_at < {now - keep}", cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(
                ReportAuditActions.ExportDownloaded,
                "report_export",
                record.Id,
                new AuditChanges().Value("report", kind).Value("format", format).Value("rows", table.Rows.Count).ToDictionary()),
            cancellationToken);
        return file;
    }

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("Exports need a tenant.");

    /// <summary>
    /// Postgres-backed limit (like the import upload limiter): the tenant's recent export rows; a transaction-scoped
    /// advisory lock per tenant serialises concurrent exports so two instances cannot both take the last slot.
    /// </summary>
    private async Task<AppError?> CheckRateAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.ExportLimit <= 0)
        {
            return null;
        }

        await db.JoinTransactionAsync(cancellationToken);
        var lockKey = $"reports.export:{TenantId:N}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var now = ReportClock.Now(clock);
        var since = now - settings.ExportWindow;
        var recent = await db.ExportRequests.AsNoTracking()
            .Where(e => e.CreatedAt > since)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.CreatedAt)
            .Take(settings.ExportLimit)
            .ToListAsync(cancellationToken);
        if (recent.Count < settings.ExportLimit)
        {
            return null;
        }

        // A slot frees up when the oldest of the last <limit> exports leaves the window.
        var retryAfter = (int)Math.Ceiling((recent[^1] + settings.ExportWindow - now).TotalSeconds);
        return new AppError(
            429,
            ReportProblems.RateLimited,
            $"Export limit reached: {settings.ExportLimit.ToString(CultureInfo.InvariantCulture)} exports per {settings.ExportWindow.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes for this tenant",
            RetryAfterSeconds: Math.Max(1, retryAfter));
    }

    /// <summary>The first filter the report does not take (exports reject them rather than silently ignore them).</summary>
    private static string? UnusedFilter(string kind, ReportExportRequest request)
    {
        var periodic = kind != ReportKinds.CriticalStock;
        var usesWarehouse = kind is ReportKinds.CriticalStock or ReportKinds.StockMovements or ReportKinds.PickingTasks;
        return (request.From, request.To, request.WarehouseId, request.Threshold, request.Statuses, request.Limit) switch
        {
            ({ }, _, _, _, _, _) when !periodic => "from",
            (_, { }, _, _, _, _) when !periodic => "to",
            (_, _, { }, _, _, _) when !usesWarehouse => "warehouseId",
            (_, _, _, { }, _, _) when kind != ReportKinds.CriticalStock => "threshold",
            (_, _, _, _, { Count: > 0 }, _) when kind != ReportKinds.Orders => "statuses",
            (_, _, _, _, _, { }) when kind != ReportKinds.TopProducts => "limit",
            _ => null,
        };
    }

    private async Task<AppResult<ReportTable>> BuildAsync(string kind, ReportExportRequest request, CancellationToken cancellationToken)
    {
        var period = new PeriodQuery(request.From, request.To, request.WarehouseId, request.Statuses ?? [], request.Limit);
        var stamp = reports.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        switch (kind)
        {
            case ReportKinds.CriticalStock:
                {
                    var result = await reports.CriticalStockAsync(
                        new CriticalStockQuery(request.Threshold, request.WarehouseId, ReportService.MaxCriticalPageSize, null), export: true, cancellationToken);
                    return result.Error ?? (AppResult<ReportTable>)ReportTables.CriticalStock(result.Value!, stamp);
                }

            case ReportKinds.Orders:
                {
                    var result = await reports.OrdersAsync(period, export: true, cancellationToken);
                    return result.Error ?? (AppResult<ReportTable>)ReportTables.Orders(result.Value!);
                }

            case ReportKinds.TopProducts:
                {
                    var result = await reports.TopProductsAsync(period, export: true, cancellationToken);
                    return result.Error ?? (AppResult<ReportTable>)ReportTables.TopProducts(result.Value!);
                }

            case ReportKinds.StockMovements:
                {
                    var result = await reports.StockMovementsAsync(period, export: true, cancellationToken);
                    return result.Error ?? (AppResult<ReportTable>)ReportTables.StockMovements(result.Value!);
                }

            case ReportKinds.PickingTasks:
                {
                    var result = await reports.PickingTasksAsync(period, export: true, cancellationToken);
                    return result.Error ?? (AppResult<ReportTable>)ReportTables.PickingTasks(result.Value!);
                }

            case ReportKinds.AiUsage:
                {
                    var result = await reports.AiUsageAsync(period, export: true, cancellationToken);
                    return result.Error ?? (AppResult<ReportTable>)ReportTables.AiUsage(result.Value!);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown report");
        }
    }
}

/// <summary>
/// The export layout of each report: Turkish headers, one row per JSON item. Amount columns are left out entirely
/// when the caller may not see amounts. Contents (data-policy): product codes/names, units, statuses, movement
/// types, counts, quantities and sell-side totals — no customer, member, address or cost data.
/// </summary>
internal static class ReportTables
{
    private static readonly ReportColumn Day = new("Gün", ReportColumnType.Date);

    public static ReportTable CriticalStock(CriticalStockReportResponse report, string stamp)
    {
        var table = new ReportTable("Kritik Stok", $"kritik-stok-{stamp}",
        [
            new("Stok Kodu", ReportColumnType.Text),
            new("Ürün", ReportColumnType.Text),
            new("Birim", ReportColumnType.Text),
            new("Eldeki", ReportColumnType.Decimal),
            new("Rezerve", ReportColumnType.Decimal),
            new("Kullanılabilir", ReportColumnType.Decimal),
            new("Eşik", ReportColumnType.Decimal),
        ]);
        foreach (var item in report.Items)
        {
            table.Add(item.Sku, item.Name, item.Unit, item.OnHand, item.Reserved, item.Available, report.Threshold);
        }

        return table;
    }

    public static ReportTable Orders(OrdersReportResponse report)
    {
        List<ReportColumn> columns =
        [
            Day,
            new("Durum", ReportColumnType.Text),
            new("Para Birimi", ReportColumnType.Text),
            new("Sipariş Sayısı", ReportColumnType.Integer),
        ];
        if (report.AmountsVisible)
        {
            columns.Add(new("Genel Toplam", ReportColumnType.Decimal));
        }

        var table = new ReportTable("Siparişler", $"siparisler-{Range(report.From, report.To)}", columns);
        foreach (var row in report.Days)
        {
            table.Add(report.AmountsVisible
                ? [row.Day, row.Status, row.Currency, row.OrderCount, row.GrandTotal]
                : [row.Day, row.Status, row.Currency, row.OrderCount]);
        }

        return table;
    }

    public static ReportTable TopProducts(TopProductsReportResponse report)
    {
        List<ReportColumn> columns =
        [
            new("Sıra", ReportColumnType.Integer),
            new("Stok Kodu", ReportColumnType.Text),
            new("Ürün", ReportColumnType.Text),
            new("Birim", ReportColumnType.Text),
            new("Miktar", ReportColumnType.Decimal),
            new("Sipariş Sayısı", ReportColumnType.Integer),
        ];
        if (report.AmountsVisible)
        {
            columns.Add(new("Para Birimi", ReportColumnType.Text));
            columns.Add(new("Tutar (KDV hariç)", ReportColumnType.Decimal));
        }

        var table = new ReportTable("En Çok Sipariş Edilenler", $"en-cok-siparis-edilenler-{Range(report.From, report.To)}", columns);
        foreach (var item in report.Items)
        {
            table.Add(report.AmountsVisible
                ? [item.Rank, item.Sku, item.Name, item.Unit, item.Quantity, item.OrderCount, item.Currency, item.Revenue]
                : [item.Rank, item.Sku, item.Name, item.Unit, item.Quantity, item.OrderCount]);
        }

        return table;
    }

    public static ReportTable StockMovements(StockMovementsReportResponse report)
    {
        var table = new ReportTable("Stok Hareketleri", $"stok-hareketleri-{Range(report.From, report.To)}",
        [
            Day,
            new("Hareket Tipi", ReportColumnType.Text),
            new("Hareket Sayısı", ReportColumnType.Integer),
            new("Giriş Miktarı", ReportColumnType.Decimal),
            new("Çıkış Miktarı", ReportColumnType.Decimal),
        ]);
        foreach (var row in report.Days)
        {
            table.Add(row.Day, row.Type, row.MovementCount, row.QuantityIn, row.QuantityOut);
        }

        return table;
    }

    public static ReportTable PickingTasks(PickingTasksReportResponse report)
    {
        var table = new ReportTable("Toplama Görevleri", $"toplama-gorevleri-{Range(report.From, report.To)}",
        [
            Day,
            new("Oluşturulan", ReportColumnType.Integer),
            new("Tamamlanan", ReportColumnType.Integer),
            new("Eksikle Tamamlanan", ReportColumnType.Integer),
            new("İptal Edilen", ReportColumnType.Integer),
            new("Ortalama Toplama (dk)", ReportColumnType.Decimal),
        ]);
        foreach (var row in report.Days)
        {
            table.Add(row.Day, row.Created, row.Completed, row.CompletedWithShortage, row.Cancelled, row.AveragePickMinutes);
        }

        return table;
    }

    public static ReportTable AiUsage(AiUsageReportResponse report)
    {
        var table = new ReportTable("AI Kullanımı", $"ai-kullanimi-{Range(report.From, report.To)}",
        [
            Day,
            new("Ayrıştırma İsteği", ReportColumnType.Integer),
            new("AI Çağrısı", ReportColumnType.Integer),
            new("AI Hatası", ReportColumnType.Integer),
            new("Kural Tabanlı Geri Dönüş", ReportColumnType.Integer),
            new("Girdi Token", ReportColumnType.Integer),
            new("Çıktı Token", ReportColumnType.Integer),
            new("Önbellek Okuma Token", ReportColumnType.Integer),
            new("Önbellek Yazma Token", ReportColumnType.Integer),
        ]);
        foreach (var row in report.Days)
        {
            table.Add(row.Day, row.ParseRequests, row.AiCalls, row.AiFailures, row.Fallbacks, row.InputTokens, row.OutputTokens, row.CacheReadTokens, row.CacheWriteTokens);
        }

        return table;
    }

    private static string Range(DateOnly from, DateOnly to) =>
        $"{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

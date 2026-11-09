using System.Globalization;
using BayiOS.Modules.Reports.Application;
using BayiOS.Modules.Reports.Contracts;
using BayiOS.Modules.Reports.Domain;
using BayiOS.Modules.Reports.Export;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Reports.Api;

/// <summary>
/// Reports (PRD module 09, api-strategy §12.13). Reads need <c>reports.read</c>, exports <c>reports.export</c>
/// (owner, admin, manager, viewer; sales only the order reports — footnote 4 → 403
/// <c>reports.orders_and_customers_only</c>; warehouse_staff none). The AI usage report is for owner/admin and is
/// gated by <c>billing.read</c>. Exports are never entitlement-gated (roles-permissions §5 "asla kilitlenmez").
/// Export = <c>POST …/export</c> (Idempotency-Key; the transaction writes the export log and the audit event, which a
/// read-only GET transaction cannot): 10 per hour per tenant (429 + Retry-After).
/// </summary>
internal static class ReportsEndpoints
{
    private const string ExportDescription =
        "CSV: UTF-8 with BOM, ';' delimiter, Turkish decimal comma, ISO dates. XLSX: one sheet. Every text cell starting with "
        + "= + - @ TAB or CR is prefixed with an apostrophe (formula injection). At most 20,000 rows (422 reports.export_too_large). "
        + "10 exports per hour per tenant (429 rate_limit.exceeded with Retry-After). Filters the report does not take → 422.";

    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/reports")
            .WithTags("Reports")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/critical-stock", CriticalStock).WithName("Reports_CriticalStock")
            .WithSummary("Kritik stok (REP-03): varsayılan olarak her aktif varyant kendi minimum stok eşiğine göre (kullanılabilir = eldeki − rezerve ≤ eşik; eşiği olmayanlar hariç, includeOutOfStock=true ile kullanılabilir ≤ 0 olanlar dahil); threshold verilirse tüm aktif varyantlar bu sabit eşiğe göre. SKU sırasıyla keyset sayfalama")
            .RequirePermission(Permissions.ReportsRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/orders", Orders).WithName("Reports_Orders")
            .WithSummary("Sipariş raporu: oluşturma gününe ve güncel duruma göre adet ve genel toplam (tutar yalnızca prices.read.sell ile)")
            .RequirePermission(Permissions.ReportsRead)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/top-products", TopProducts).WithName("Reports_TopProducts")
            .WithSummary("En çok sipariş edilen ürünler: onaylanmış ve sonrası siparişlerde miktar, sipariş sayısı (tutar yalnızca prices.read.sell ile)")
            .RequirePermission(Permissions.ReportsRead)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/stock-movements", StockMovements).WithName("Reports_StockMovements")
            .WithSummary("Stok hareket özeti: gün ve hareket tipine göre adet, giriş ve çıkış miktarı (maliyet yok)")
            .RequirePermission(Permissions.ReportsRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/picking-tasks", PickingTasks).WithName("Reports_PickingTasks")
            .WithSummary("Toplama görevi raporu: gün bazında oluşturulan, tamamlanan, eksikle tamamlanan, iptal ve ortalama toplama süresi")
            .RequirePermission(Permissions.ReportsRead)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/ai-usage", AiUsage).WithName("Reports_AiUsage")
            .WithSummary("AI asistan kullanımı: gün bazında ayrıştırma, AI çağrısı, hata, kural tabanlı geri dönüş ve token sayıları (owner, admin)")
            .RequirePermission(Permissions.BillingRead)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        MapExport(group, "/critical-stock/export", ReportKinds.CriticalStock, "Reports_ExportCriticalStock", "Kritik stok raporunu CSV/XLSX olarak dışa aktarır", Permissions.ReportsExport);
        MapExport(group, "/orders/export", ReportKinds.Orders, "Reports_ExportOrders", "Sipariş raporunu CSV/XLSX olarak dışa aktarır", Permissions.ReportsExport);
        MapExport(group, "/top-products/export", ReportKinds.TopProducts, "Reports_ExportTopProducts", "En çok sipariş edilen ürünleri CSV/XLSX olarak dışa aktarır", Permissions.ReportsExport);
        MapExport(group, "/stock-movements/export", ReportKinds.StockMovements, "Reports_ExportStockMovements", "Stok hareket özetini CSV/XLSX olarak dışa aktarır", Permissions.ReportsExport);
        MapExport(group, "/picking-tasks/export", ReportKinds.PickingTasks, "Reports_ExportPickingTasks", "Toplama görevi raporunu CSV/XLSX olarak dışa aktarır", Permissions.ReportsExport);
        MapExport(group, "/ai-usage/export", ReportKinds.AiUsage, "Reports_ExportAiUsage", "AI kullanım raporunu CSV/XLSX olarak dışa aktarır (owner, admin)", Permissions.BillingRead);
        return app;
    }

    private static void MapExport(RouteGroupBuilder group, string pattern, string kind, string name, string summary, string permission) =>
        group.MapPost(pattern, (HttpContext http, [FromBody] ReportExportRequest? request, ReportExportService service, CancellationToken cancellationToken) =>
                ExportAsync(http, kind, request, service, cancellationToken))
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(ExportDescription)
            .RequirePermission(permission)
            .RequireIdempotencyKey()
            .Accepts<ReportExportRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: CsvReportWriter.ContentType, additionalContentTypes: XlsxReportWriter.ContentType)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportAsync(
        HttpContext http, string kind, ReportExportRequest? request, ReportExportService service, CancellationToken cancellationToken)
    {
        if (http.Request.Query.Count > 0)
        {
            return Malformed("Export filters go in the JSON body, not the query string");
        }

        var result = await service.ExportAsync(kind, request ?? new ReportExportRequest(), cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        var file = result.Value!;
        return TypedResults.File(file.Content, file.ContentType, file.FileName);
    }

    /// <param name="threshold">Sabit kritik eşik (≥ 0). Verilmezse her varyantın kendi minimum stok eşiği kullanılır (#137).</param>
    /// <param name="warehouseId">Yalnızca bu depo (yoksa tüm depoların toplamı).</param>
    /// <param name="includeOutOfStock">Eşik modunda eşiği olmayan ama kullanılabilir ≤ 0 olan varyantları da listeler (true/false; threshold ile birlikte verilemez).</param>
    private static async Task<Results<Ok<CriticalStockReportResponse>, ProblemHttpResult>> CriticalStock(
        HttpContext http,
        ReportService service,
        string? threshold,
        string? warehouseId,
        string? includeOutOfStock,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (Unknown(http, "threshold", "warehouseId", "includeOutOfStock", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        var outOfStock = false;
        if (!string.IsNullOrEmpty(includeOutOfStock) && !bool.TryParse(includeOutOfStock, out outOfStock))
        {
            return Malformed("includeOutOfStock must be true or false");
        }

        decimal? value = null;
        if (!string.IsNullOrEmpty(threshold))
        {
            if (!decimal.TryParse(threshold, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            {
                return Malformed("threshold must be a non-negative number (e.g. 5 or 2.5)");
            }

            value = parsed;
        }

        if (!TryGuid(warehouseId, out var warehouse))
        {
            return Malformed("warehouseId must be a UUID");
        }

        if (!TryLimit(limit, ReportService.DefaultCriticalPageSize, ReportService.MaxCriticalPageSize, out var pageSize))
        {
            return Malformed($"limit must be an integer between 1 and {ReportService.MaxCriticalPageSize}");
        }

        string? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!ReportCursor.TryDecode(cursor, out var sku))
            {
                return Malformed("cursor is invalid");
            }

            after = sku;
        }

        return Ok(http, await service.CriticalStockAsync(new CriticalStockQuery(value, warehouse, pageSize, after, outOfStock), export: false, cancellationToken));
    }

    /// <param name="from">İlk gün (yyyy-MM-dd, Europe/Istanbul; varsayılan: son 30 gün).</param>
    /// <param name="to">Son gün (dahil; aralık en fazla 366 gün).</param>
    /// <param name="status">Virgülle sipariş durumu listesi (boş = hepsi).</param>
    private static async Task<Results<Ok<OrdersReportResponse>, ProblemHttpResult>> Orders(
        HttpContext http, ReportService service, string? from, string? to, string? status, CancellationToken cancellationToken)
    {
        if (Unknown(http, "from", "to", "status") is { } unknown)
        {
            return unknown;
        }

        if (!TryDate(from, out var start) || !TryDate(to, out var end))
        {
            return Malformed("from and to must be dates (yyyy-MM-dd)");
        }

        var statuses = string.IsNullOrWhiteSpace(status)
            ? []
            : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        return Ok(http, await service.OrdersAsync(new PeriodQuery(start, end, null, statuses, null), export: false, cancellationToken));
    }

    /// <param name="from">İlk gün (yyyy-MM-dd, Europe/Istanbul; varsayılan: son 30 gün).</param>
    /// <param name="to">Son gün (dahil; aralık en fazla 366 gün).</param>
    /// <param name="limit">Kaç ürün (1–100, varsayılan 20).</param>
    private static async Task<Results<Ok<TopProductsReportResponse>, ProblemHttpResult>> TopProducts(
        HttpContext http, ReportService service, string? from, string? to, string? limit, CancellationToken cancellationToken)
    {
        if (Unknown(http, "from", "to", "limit") is { } unknown)
        {
            return unknown;
        }

        if (!TryDate(from, out var start) || !TryDate(to, out var end))
        {
            return Malformed("from and to must be dates (yyyy-MM-dd)");
        }

        if (!TryLimit(limit, ReportService.DefaultTopProducts, ReportService.MaxTopProducts, out var count))
        {
            return Malformed($"limit must be an integer between 1 and {ReportService.MaxTopProducts}");
        }

        return Ok(http, await service.TopProductsAsync(new PeriodQuery(start, end, null, [], count), export: false, cancellationToken));
    }

    /// <param name="from">İlk gün (yyyy-MM-dd, Europe/Istanbul; varsayılan: son 30 gün).</param>
    /// <param name="to">Son gün (dahil; aralık en fazla 366 gün).</param>
    /// <param name="warehouseId">Yalnızca bu depo.</param>
    private static async Task<Results<Ok<StockMovementsReportResponse>, ProblemHttpResult>> StockMovements(
        HttpContext http, ReportService service, string? from, string? to, string? warehouseId, CancellationToken cancellationToken)
    {
        if (PeriodWithWarehouse(http, from, to, warehouseId, out var query) is { } invalid)
        {
            return invalid;
        }

        return Ok(http, await service.StockMovementsAsync(query!, export: false, cancellationToken));
    }

    /// <param name="from">İlk gün (yyyy-MM-dd, Europe/Istanbul; varsayılan: son 30 gün).</param>
    /// <param name="to">Son gün (dahil; aralık en fazla 366 gün).</param>
    /// <param name="warehouseId">Yalnızca bu depo.</param>
    private static async Task<Results<Ok<PickingTasksReportResponse>, ProblemHttpResult>> PickingTasks(
        HttpContext http, ReportService service, string? from, string? to, string? warehouseId, CancellationToken cancellationToken)
    {
        if (PeriodWithWarehouse(http, from, to, warehouseId, out var query) is { } invalid)
        {
            return invalid;
        }

        return Ok(http, await service.PickingTasksAsync(query!, export: false, cancellationToken));
    }

    /// <param name="from">İlk gün (yyyy-MM-dd, Europe/Istanbul; varsayılan: son 30 gün).</param>
    /// <param name="to">Son gün (dahil; aralık en fazla 366 gün).</param>
    private static async Task<Results<Ok<AiUsageReportResponse>, ProblemHttpResult>> AiUsage(
        HttpContext http, ReportService service, string? from, string? to, CancellationToken cancellationToken)
    {
        if (Unknown(http, "from", "to") is { } unknown)
        {
            return unknown;
        }

        if (!TryDate(from, out var start) || !TryDate(to, out var end))
        {
            return Malformed("from and to must be dates (yyyy-MM-dd)");
        }

        return Ok(http, await service.AiUsageAsync(new PeriodQuery(start, end, null, [], null), export: false, cancellationToken));
    }

    // ---- Helpers ----

    private static ProblemHttpResult? PeriodWithWarehouse(HttpContext http, string? from, string? to, string? warehouseId, out PeriodQuery? query)
    {
        query = null;
        if (Unknown(http, "from", "to", "warehouseId") is { } unknown)
        {
            return unknown;
        }

        if (!TryDate(from, out var start) || !TryDate(to, out var end))
        {
            return Malformed("from and to must be dates (yyyy-MM-dd)");
        }

        if (!TryGuid(warehouseId, out var warehouse))
        {
            return Malformed("warehouseId must be a UUID");
        }

        query = new PeriodQuery(start, end, warehouse, [], null);
        return null;
    }

    private static ProblemHttpResult? Unknown(HttpContext http, params string[] allowed)
    {
        var unknown = http.Request.Query.Keys.Where(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        return unknown.Count > 0 ? Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}") : null;
    }

    private static bool TryDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGuid(string? value, out Guid? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Guid.TryParse(value, out var parsed))
        {
            id = parsed;
            return true;
        }

        return false;
    }

    private static bool TryLimit(string? value, int fallback, int max, out int limit)
    {
        limit = fallback;
        return string.IsNullOrEmpty(value)
            || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= max);
    }

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(HttpContext http, AppResult<T> result) =>
        result.Error is { } error ? Problem(http, error) : TypedResults.Ok(result.Value!);

    private static ProblemHttpResult Problem(HttpContext http, AppError error)
    {
        if (error.RetryAfterSeconds is { } retryAfter)
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(error.Errors is null ? ApiProblems.Create(error.Status, error.Code, error.Title) : ApiProblems.Validation(error.Errors));
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));
}

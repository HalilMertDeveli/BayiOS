using System.Globalization;
using BayiOS.Modules.Warehouse.Application;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.Modules.Warehouse.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Warehouse.Api;

/// <summary>
/// Warehouse transfers (OD-01: two-stage, "Yolda") and stock counts (#168), for the web and a handheld scanner.
/// Permissions (roles-permissions §3.4): <c>inventory.transfer</c> creates, dispatches, closes and cancels transfers;
/// <c>inventory.transfer.receive</c> receives (warehouse_staff included); either reads. <c>inventory.count.manage</c>
/// starts, recounts, applies and cancels counts; <c>inventory.count.perform</c> enters counts and submits; either reads.
/// Every POST requires an Idempotency-Key (a retried scan never counts twice); If-Match is honoured when sent.
/// </summary>
internal static class StockDocumentEndpoints
{
    public static IEndpointRouteBuilder MapStockDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var transfers = app.MapGroup("/api/v1/stock-transfers")
            .WithTags("Stock transfers")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        transfers.MapGet("", ListTransfers).WithName("StockTransfers_List")
            .WithSummary("Depolar arası transferler (yeniden eskiye keyset): durum listesi, depo (kaynak veya hedef) filtresi")
            .RequirePermission(Permissions.InventoryTransfer, Permissions.InventoryTransferReceive);
        transfers.MapGet("/{id:guid}", GetTransfer).WithName("StockTransfers_Get")
            .WithSummary("Transfer: satırlar, gönderilen / teslim alınan / yolda / iade / kayıp miktarlar ve fark (ETag döner)")
            .RequirePermission(Permissions.InventoryTransfer, Permissions.InventoryTransferReceive)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Command(transfers.MapPost("", CreateTransfer).WithName("StockTransfers_Create")
            .WithSummary("Taslak transfer: kaynak ve hedef depo (farklı), satırlar (ürün, miktar, kaynak raf, planlanan hedef raf)")
            .RequirePermission(Permissions.InventoryTransfer));
        Command(transfers.MapPost("/{id:guid}/dispatch", Dispatch).WithName("StockTransfers_Dispatch")
            .WithSummary("Gönder: kaynaktan çıkış (yalnızca satılabilir miktar, asla eksi stok), mal yolda — hiçbir depoda satılabilir değil")
            .RequirePermission(Permissions.InventoryTransfer)
            .ProducesStockProblems());
        Command(transfers.MapPost("/{id:guid}/receive", Receive).WithName("StockTransfers_Receive")
            .WithSummary("Teslim al (kısmi olabilir): satır id veya okutulan ürün kodu + hedef raf (id veya okutulan raf kodu); yoldakinden fazlası reddedilir")
            .RequirePermission(Permissions.InventoryTransferReceive)
            .ProducesStockProblems());
        Command(transfers.MapPost("/{id:guid}/close", Close).WithName("StockTransfers_Close")
            .WithSummary("Farkı kapat: yolda kalan miktar kayıp (lost) olarak düşülür veya kaynağa iade edilir (return_to_source); sebep zorunlu")
            .RequirePermission(Permissions.InventoryTransfer)
            .ProducesStockProblems());
        Command(transfers.MapPost("/{id:guid}/cancel", CancelTransfer).WithName("StockTransfers_Cancel")
            .WithSummary("Taslağı iptal eder (yalnızca gönderilmeden önce); stok hareketi yok")
            .RequirePermission(Permissions.InventoryTransfer));

        var counts = app.MapGroup("/api/v1/stock-counts")
            .WithTags("Stock counts")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        counts.MapGet("", ListCounts).WithName("StockCounts_List")
            .WithSummary("Stok sayımları (yeniden eskiye keyset): durum listesi ve depo filtresi")
            .RequirePermission(Permissions.InventoryCountManage, Permissions.InventoryCountPerform);
        counts.MapGet("/{id:guid}", GetCount).WithName("StockCounts_Get")
            .WithSummary("Sayım ve satırları (fark raporu); kör sayımda sistem miktarları yalnızca inventory.count.manage ile görünür (ETag döner)")
            .RequirePermission(Permissions.InventoryCountManage, Permissions.InventoryCountPerform)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Command(counts.MapPost("", CreateCount).WithName("StockCounts_Create")
            .WithSummary("Sayım başlatır: depo, kapsam (full / locations / variants), kör sayım; kapsamdaki stok anlık görüntü olarak alınır (depo başına tek açık sayım)")
            .RequirePermission(Permissions.InventoryCountManage));
        Command(counts.MapPost("/{id:guid}/entries", Enter).WithName("StockCounts_Enter")
            .WithSummary("Sayılan miktarlar: satır id veya okutulan ürün + raf kodu; mode = set (mutlak) | add (okutma başına ekle)")
            .RequirePermission(Permissions.InventoryCountPerform));
        Command(counts.MapPost("/{id:guid}/submit", Submit).WithName("StockCounts_Submit")
            .WithSummary("Sayım bitti: fark raporu incelemeye hazır")
            .RequirePermission(Permissions.InventoryCountPerform));
        Command(counts.MapPost("/{id:guid}/recount", Recount).WithName("StockCounts_Recount")
            .WithSummary("Seçili satırları yeniden saydırır (sayılan miktar silinir, sayım açık duruma döner)")
            .RequirePermission(Permissions.InventoryCountManage));
        Command(counts.MapPost("/{id:guid}/apply", Apply).WithName("StockCounts_Apply")
            .WithSummary("Onayla: her fark için count_correction hareketi (sayım anındaki sistem bakiyesine göre); rezervin altına inemez (count.below_reserved)")
            .RequirePermission(Permissions.InventoryCountManage)
            .ProducesStockProblems());
        Command(counts.MapPost("/{id:guid}/cancel", CancelCount).WithName("StockCounts_Cancel")
            .WithSummary("Sayımı iptal eder (uygulanmadıysa); stok hareketi yok")
            .RequirePermission(Permissions.InventoryCountManage));

        return app;
    }

    private static void Command(RouteHandlerBuilder builder) => builder
        .RequireIdempotencyKey()
        .ProducesRetryableConflict("transfer.busy", "count.busy", "inventory.busy")
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    // ---- Transfers ----

    /// <param name="status">Virgülle durum listesi (draft, in_transit, partially_received, received, closed, cancelled).</param>
    /// <param name="warehouseId">Kaynağı veya hedefi bu depo olan transferler.</param>
    private static async Task<Results<Ok<StockTransferPageResponse>, ProblemHttpResult>> ListTransfers(
        HttpRequest http, StockTransferQueries queries, string? status, string? warehouseId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (ParseList(http, status, warehouseId, limit, cursor, StockTransferStatuses.All, out var statuses, out var warehouse, out var pageSize, out var keyset) is { } problem)
        {
            return problem;
        }

        return TypedResults.Ok(await queries.ListAsync(new StockTransferListQuery(statuses, warehouse, pageSize, keyset), cancellationToken));
    }

    private static async Task<Results<Ok<StockTransferResponse>, ProblemHttpResult>> GetTransfer(
        Guid id, StockTransferQueries queries, HttpContext http, CancellationToken cancellationToken) =>
        Ok<StockTransferResponse>(http, await queries.DetailAsync(id, cancellationToken) is { } transfer ? transfer : AppError.NotFound(), t => t.Transfer.Version);

    private static async Task<Results<Created<StockTransferResponse>, ProblemHttpResult>> CreateTransfer(
        CreateStockTransferRequest request, StockTransferService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Transfer.Version);
        return TypedResults.Created($"/api/v1/stock-transfers/{result.Value.Transfer.Id}", result.Value);
    }

    private static async Task<Results<Ok<StockTransferActionResponse>, ProblemHttpResult>> Dispatch(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, StockTransferService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.DispatchAsync(id, version, http.TraceIdentifier, cancellationToken), t => t.Transfer.Transfer.Version);

    private static async Task<Results<Ok<StockTransferActionResponse>, ProblemHttpResult>> Receive(
        Guid id, ReceiveStockTransferRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockTransferService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.ReceiveAsync(id, version, request, http.TraceIdentifier, cancellationToken), t => t.Transfer.Transfer.Version);

    private static async Task<Results<Ok<StockTransferActionResponse>, ProblemHttpResult>> Close(
        Guid id, CloseStockTransferRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockTransferService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.CloseAsync(id, version, request, http.TraceIdentifier, cancellationToken), t => t.Transfer.Transfer.Version);

    private static async Task<Results<Ok<StockTransferActionResponse>, ProblemHttpResult>> CancelTransfer(
        Guid id, CancelStockTransferRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockTransferService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.CancelAsync(id, version, request, cancellationToken), t => t.Transfer.Transfer.Version);

    // ---- Counts ----

    /// <param name="status">Virgülle durum listesi (open, submitted, applied, cancelled).</param>
    private static async Task<Results<Ok<StockCountPageResponse>, ProblemHttpResult>> ListCounts(
        HttpRequest http, StockCountQueries queries, string? status, string? warehouseId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (ParseList(http, status, warehouseId, limit, cursor, StockCountStatuses.All, out var statuses, out var warehouse, out var pageSize, out var keyset) is { } problem)
        {
            return problem;
        }

        return TypedResults.Ok(await queries.ListAsync(new StockCountListQuery(statuses, warehouse, pageSize, keyset), cancellationToken));
    }

    private static async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> GetCount(
        Guid id, StockCountQueries queries, HttpContext http, CancellationToken cancellationToken) =>
        Ok<StockCountResponse>(http, await queries.DetailAsync(id, cancellationToken) is { } count ? count : AppError.NotFound(), c => c.Count.Version);

    private static async Task<Results<Created<StockCountResponse>, ProblemHttpResult>> CreateCount(
        CreateStockCountRequest request, StockCountService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Count.Version);
        return TypedResults.Created($"/api/v1/stock-counts/{result.Value.Count.Id}", result.Value);
    }

    private static async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> Enter(
        Guid id, StockCountEntriesRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockCountService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.EnterAsync(id, version, request, cancellationToken), c => c.Count.Version);

    private static async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> Submit(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, StockCountService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.SubmitAsync(id, version, cancellationToken), c => c.Count.Version);

    private static async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> Recount(
        Guid id, RecountStockCountRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockCountService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.RecountAsync(id, version, request, cancellationToken), c => c.Count.Version);

    private static async Task<Results<Ok<ApplyStockCountResponse>, ProblemHttpResult>> Apply(
        Guid id, ApplyStockCountRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockCountService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.ApplyAsync(id, version, request, http.TraceIdentifier, cancellationToken), c => c.Count.Count.Version);

    private static async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> CancelCount(
        Guid id, CancelStockCountRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, StockCountService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.CancelAsync(id, version, request, cancellationToken), c => c.Count.Version);

    // ---- Helpers ----

    private static ProblemHttpResult? ParseList(
        HttpRequest http,
        string? status,
        string? warehouseId,
        string? limit,
        string? cursor,
        IReadOnlyList<string> allowed,
        out IReadOnlyList<string> statuses,
        out Guid? warehouse,
        out int pageSize,
        out FulfillmentCursor? keyset)
    {
        statuses = [];
        warehouse = null;
        pageSize = FulfillmentQueries.DefaultPageSize;
        keyset = null;
        var unknown = http.Query.Keys.Where(k => !new[] { "status", "warehouseId", "limit", "cursor" }.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            return Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
        }

        statuses = string.IsNullOrWhiteSpace(status)
            ? []
            : [.. status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
        if (!statuses.All(allowed.Contains))
        {
            return Malformed($"status must be a comma-separated subset of: {string.Join(", ", allowed)}");
        }

        if (!string.IsNullOrEmpty(warehouseId))
        {
            if (!Guid.TryParse(warehouseId, out var parsed))
            {
                return Malformed("warehouseId must be a UUID");
            }

            warehouse = parsed;
        }

        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > FulfillmentQueries.MaxPageSize))
        {
            return Malformed($"limit must be an integer between 1 and {FulfillmentQueries.MaxPageSize}");
        }

        if (cursor is not null && (keyset = FulfillmentCursor.TryDecode(cursor)) is null)
        {
            return Malformed("cursor is invalid for this query");
        }

        return null;
    }

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(HttpContext http, AppResult<T> result, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Ok(result.Value);
    }

    /// <summary>RFC 9457 with the stable code; field details in <c>errors</c>, stock details in <c>shortages</c>.</summary>
    private static ProblemHttpResult Problem(HttpContext http, AppError error)
    {
        var problem = error.Errors is null || error.Code != "validation.failed"
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors);
        if (error.Errors is not null && error.Code != "validation.failed")
        {
            problem.Extensions["errors"] = error.Errors;
        }

        if (error.Shortages is { Count: > 0 } shortages)
        {
            problem.Extensions["shortages"] = shortages.Select(s => new
            {
                variantId = s.VariantId,
                sku = s.Sku,
                warehouseId = s.WarehouseId,
                locationId = s.LocationId,
                requested = s.Requested,
                available = s.Available,
            }).ToList();
        }

        if (error.Code.EndsWith(".busy", StringComparison.Ordinal))
        {
            http.Response.Headers.RetryAfter = "1";
        }

        return TypedResults.Problem(problem);
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static ProblemHttpResult? IfMatch(string? ifMatch, out long? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return null;
        }

        var trimmed = ifMatch.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        if (long.TryParse(trimmed.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            version = parsed;
            return null;
        }

        return TypedResults.Problem(ApiProblems.Create(
            StatusCodes.Status412PreconditionFailed, "concurrency.version_mismatch", "If-Match does not match the current version"));
    }

    private static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";
}

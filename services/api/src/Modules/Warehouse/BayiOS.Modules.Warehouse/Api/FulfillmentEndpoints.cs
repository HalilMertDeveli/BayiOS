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
/// Picking, packing and shipping (api-strategy §12.8) for the mobile scanner client (roles-permissions §3.6).
/// Every POST requires an Idempotency-Key (a retried scan never counts twice); If-Match is honoured when sent.
/// Staff see their own tasks plus the unassigned pool and act only on tasks assigned to them; supervisors
/// (<c>tasks.assign</c>) see and act on all.
/// </summary>
internal static class FulfillmentEndpoints
{
    public static IEndpointRouteBuilder MapFulfillmentEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/api/v1/picking-tasks")
            .WithTags("Picking")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tasks.MapGet("", ListTasks).WithName("Picking_List")
            .WithSummary("Toplama görevleri (tümü): eskiden yeniye keyset; durum, tip, depo, sipariş, atanan (uuid | none) filtresi")
            .RequirePermission(Permissions.TasksReadAll);
        tasks.MapGet("/mine", MyTasks).WithName("Picking_Mine")
            .WithSummary("Mobil iş listesi: bana atanan aktif görevler, sonra havuz (scope = all | assigned | pool)")
            .RequirePermission(Permissions.TasksReadOwn);
        tasks.MapGet("/{id:guid}", GetTask).WithName("Picking_Get")
            .WithSummary("Görev ve raf sırasına göre satırlar (lokasyon, barkod, istenen/toplanan/eksik; ETag döner)")
            .RequirePermission(Permissions.TasksReadOwn)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Command(tasks.MapPost("", CreateTask).WithName("Picking_Create")
            .WithSummary("Siparişin açık rezervasyonlarından toplama görevi (onayda otomatik oluşur; kalan için yeniden)")
            .RequirePermission(Permissions.TasksAssign));
        Command(tasks.MapPost("/{id:guid}/assign", Assign).WithName("Picking_Assign")
            .WithSummary("Görevi personele atar (aktif üye, toplama yetkili rol)").RequirePermission(Permissions.TasksAssign));
        Command(tasks.MapPost("/{id:guid}/unassign", Unassign).WithName("Picking_Unassign")
            .WithSummary("Görevi havuza geri bırakır").RequirePermission(Permissions.TasksAssign));
        Command(tasks.MapPost("/{id:guid}/claim", Claim).WithName("Picking_Claim")
            .WithSummary("Havuzdaki görevi üstlenir; eşzamanlı iki üstlenmeden yalnızca biri kazanır").RequirePermission(Permissions.TasksClaim));
        Command(tasks.MapPost("/{id:guid}/start", Start).WithName("Picking_Start")
            .WithSummary("Toplamayı başlatır (sipariş Onaylandı → Toplanıyor)").RequirePermission(Permissions.TasksPick));
        Command(tasks.MapPost("/{id:guid}/scans", Scan).WithName("Picking_Scan")
            .WithSummary("Barkod okutma: lokasyon + ürün barkodu/SKU + miktar (varsayılan 1); yanlış ürün/raf ve fazla toplama reddedilir")
            .RequirePermission(Permissions.TasksPick));
        Command(tasks.MapPost("/{id:guid}/lines/{lineId:guid}/shortage", ReportTaskShortage).WithName("Picking_ReportShortage")
            .WithSummary("Eksik bildirimi (not_found, damaged, insufficient): sipariş işaretlenir, outbox bildirimi yazılır")
            .RequirePermission(Permissions.TasksReportShortage));
        Command(tasks.MapPost("/{id:guid}/complete", Complete).WithName("Picking_Complete")
            .WithSummary("Tüm satırlar toplandı/eksik bildirildi → görev tamamlanır, sipariş Paketlendi, toplananlardan taslak sevkiyat")
            .RequirePermission(Permissions.TasksPick));

        var shipments = app.MapGroup("/api/v1/shipments")
            .WithTags("Shipments")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        shipments.MapGet("", ListShipments).WithName("Shipments_List")
            .WithSummary("Sevkiyatlar: eskiden yeniye keyset; durum ve sipariş filtresi (personel yalnızca kendi görevlerininkini görür)")
            .RequirePermission(Permissions.TasksReadOwn);
        shipments.MapGet("/{id:guid}", GetShipment).WithName("Shipments_Get")
            .WithSummary("Sevkiyat: satırlar, koliler (ETag döner); stok hareketleri: GET /inventory/movements?sourceId={id}")
            .RequirePermission(Permissions.TasksReadOwn)
            .ProducesProblem(StatusCodes.Status404NotFound);
        Command(shipments.MapPost("/{id:guid}/pack", Pack).WithName("Shipments_Pack")
            .WithSummary("Paket kontrolü: ürünler kolilere tekrar okutulur; toplanan = paketlenen olmalı (koli sayısı kaydedilir)")
            .RequirePermission(Permissions.TasksPack));
        Command(shipments.MapPost("/{id:guid}/lines/{lineId:guid}/shortage", ReportShipmentShortage).WithName("Shipments_ReportShortage")
            .WithSummary("Paketlemede eksik: satır daha az sevk edilir, sipariş işaretlenir, outbox bildirimi yazılır")
            .RequirePermission(Permissions.TasksReportShortage));
        Command(shipments.MapPost("/{id:guid}/confirm", Confirm).WithName("Shipments_Confirm")
            .WithSummary("Sevkiyat onayı: rezerve stoktan tek seferlik çıkış (shipment_out), sipariş Sevk edildi veya kısmi sevk")
            .RequirePermission(Permissions.OrdersShip));

        return app;
    }

    private static void Command(RouteHandlerBuilder builder) => builder
        .RequireIdempotencyKey()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    // ---- Picking tasks ----

    /// <param name="status">Virgülle durum listesi (open, in_progress, completed, completed_with_shortage, cancelled).</param>
    /// <param name="type">pick veya putaway.</param>
    /// <param name="assignedTo">Kullanıcı kimliği veya <c>none</c> (havuz).</param>
    private static async Task<Results<Ok<PickingTaskPageResponse>, ProblemHttpResult>> ListTasks(
        HttpRequest http,
        FulfillmentQueries queries,
        string? status,
        string? type,
        string? warehouseId,
        string? orderId,
        string? assignedTo,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "status", "type", "warehouseId", "orderId", "assignedTo", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (ParseStatuses(status, PickingTaskStatuses.All, out var statuses) is { } statusProblem)
        {
            return statusProblem;
        }

        if (type is not null && !PickingTaskTypes.All.Contains(type))
        {
            return Malformed($"type must be one of: {string.Join(", ", PickingTaskTypes.All)}");
        }

        if (!TryGuid(warehouseId, out var warehouse) || !TryGuid(orderId, out var order))
        {
            return Malformed("warehouseId and orderId must be UUIDs");
        }

        Guid? assignee = null;
        if (assignedTo == "none")
        {
            assignee = Guid.Empty;
        }
        else if (!TryGuid(assignedTo, out assignee))
        {
            return Malformed("assignedTo must be a UUID or 'none'");
        }

        if (ParsePaging(limit, cursor, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return TypedResults.Ok(await queries.ListTasksAsync(
            new PickingTaskListQuery(statuses, type, warehouse, order, assignee, pageSize, keyset), cancellationToken));
    }

    /// <param name="scope">all (varsayılan) | assigned | pool.</param>
    private static async Task<Results<Ok<MyPickingTasksResponse>, ProblemHttpResult>> MyTasks(
        HttpRequest http, FulfillmentQueries queries, string? scope, string? warehouseId, string? limit, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "scope", "warehouseId", "limit") is { } unknown)
        {
            return unknown;
        }

        if (scope is not (null or "all" or "assigned" or "pool"))
        {
            return Malformed("scope must be one of: all, assigned, pool");
        }

        if (!TryGuid(warehouseId, out var warehouse))
        {
            return Malformed("warehouseId must be a UUID");
        }

        if (ParsePaging(limit, null, out var pageSize, out _) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return TypedResults.Ok(await queries.MyTasksAsync(scope is null or "all" or "assigned", scope is null or "all" or "pool", warehouse, pageSize, cancellationToken));
    }

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> GetTask(
        Guid id, FulfillmentQueries queries, HttpContext http, CancellationToken cancellationToken) =>
        Ok<PickingTaskResponse>(http, await queries.TaskAsync(id, cancellationToken) is { } task ? task : AppError.NotFound(), t => t.Task.Version);

    private static async Task<Results<Created<PickingTaskResponse>, ProblemHttpResult>> CreateTask(
        CreatePickingTaskRequest request, PickingService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Task.Version);
        return TypedResults.Created($"/api/v1/picking-tasks/{result.Value.Task.Id}", result.Value);
    }

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> Assign(
        Guid id, AssignPickingTaskRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.AssignAsync(id, version, request, cancellationToken), t => t.Task.Version);

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> Unassign(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.UnassignAsync(id, version, cancellationToken), t => t.Task.Version);

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> Claim(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.ClaimAsync(id, version, cancellationToken), t => t.Task.Version);

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> Start(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.StartAsync(id, version, http.TraceIdentifier, cancellationToken), t => t.Task.Version);

    private static async Task<Results<Ok<PickScanResponse>, ProblemHttpResult>> Scan(
        Guid id, PickScanRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.ScanAsync(id, version, request, http.TraceIdentifier, cancellationToken), s => s.Version);

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> ReportTaskShortage(
        Guid id, Guid lineId, ReportShortageRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.ReportShortageAsync(id, lineId, version, request, http.TraceIdentifier, cancellationToken), t => t.Task.Version);

    private static async Task<Results<Ok<PickingTaskResponse>, ProblemHttpResult>> Complete(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, PickingService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.CompleteAsync(id, version, http.TraceIdentifier, cancellationToken), t => t.Task.Version);

    // ---- Shipments ----

    /// <param name="status">Virgülle durum listesi (draft, packed, confirmed, cancelled).</param>
    private static async Task<Results<Ok<ShipmentPageResponse>, ProblemHttpResult>> ListShipments(
        HttpRequest http, FulfillmentQueries queries, string? status, string? orderId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "status", "orderId", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (ParseStatuses(status, ShipmentStatuses.All, out var statuses) is { } statusProblem)
        {
            return statusProblem;
        }

        if (!TryGuid(orderId, out var order))
        {
            return Malformed("orderId must be a UUID");
        }

        if (ParsePaging(limit, cursor, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return TypedResults.Ok(await queries.ListShipmentsAsync(new ShipmentListQuery(statuses, order, pageSize, keyset), cancellationToken));
    }

    private static async Task<Results<Ok<ShipmentResponse>, ProblemHttpResult>> GetShipment(
        Guid id, FulfillmentQueries queries, HttpContext http, CancellationToken cancellationToken) =>
        Ok<ShipmentResponse>(http, await queries.ShipmentAsync(id, cancellationToken) is { } shipment ? shipment : AppError.NotFound(), s => s.Version);

    private static async Task<Results<Ok<ShipmentResponse>, ProblemHttpResult>> Pack(
        Guid id, PackShipmentRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, ShipmentService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) ?? Ok(http, await service.PackAsync(id, version, request, cancellationToken), s => s.Version);

    private static async Task<Results<Ok<ShipmentResponse>, ProblemHttpResult>> ReportShipmentShortage(
        Guid id, Guid lineId, ReportShortageRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, ShipmentService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.ReportShortageAsync(id, lineId, version, request, http.TraceIdentifier, cancellationToken), s => s.Version);

    private static async Task<Results<Ok<ConfirmShipmentResponse>, ProblemHttpResult>> Confirm(
        Guid id, ConfirmShipmentRequest? request, [FromHeader(Name = "If-Match")] string? ifMatch, ShipmentService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version)
        ?? Ok(http, await service.ConfirmAsync(id, version, request ?? new ConfirmShipmentRequest(null, null, null), http.TraceIdentifier, cancellationToken), c => c.Shipment.Version);

    // ---- Helpers ----

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(HttpContext http, AppResult<T> result, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Ok(result.Value);
    }

    /// <summary>RFC 9457 with the stable code; field details go to <c>errors</c> for non-validation codes too.</summary>
    private static ProblemHttpResult Problem(HttpContext http, AppError error)
    {
        var problem = error.Errors is null || error.Code != "validation.failed"
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors);
        if (error.Errors is not null && error.Code != "validation.failed")
        {
            problem.Extensions["errors"] = error.Errors;
        }

        if (error.Code.EndsWith(".busy", StringComparison.Ordinal))
        {
            http.Response.Headers.RetryAfter = "1";
        }

        return TypedResults.Problem(problem);
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static ProblemHttpResult? RejectUnknownQuery(HttpRequest request, params string[] allowed)
    {
        var unknown = request.Query.Keys.Where(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        return unknown.Count == 0 ? null : Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
    }

    private static ProblemHttpResult? ParseStatuses(string? value, IReadOnlyList<string> allowed, out IReadOnlyList<string> statuses)
    {
        statuses = string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
        return statuses.All(allowed.Contains) ? null : Malformed($"status must be a comma-separated subset of: {string.Join(", ", allowed)}");
    }

    private static ProblemHttpResult? ParsePaging(string? limit, string? cursor, out int pageSize, out FulfillmentCursor? keyset)
    {
        keyset = null;
        pageSize = FulfillmentQueries.DefaultPageSize;
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

    private static bool TryGuid(string? value, out Guid? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Guid.TryParse(value, out var guid))
        {
            parsed = guid;
            return true;
        }

        return false;
    }

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

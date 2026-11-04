using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Orders.Application;
using BayiOS.Modules.Orders.Contracts;
using BayiOS.Modules.Orders.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Orders.Api;

/// <summary>
/// Orders (api-strategy §12, inventory-and-orders §3.1). Reads need <c>orders.read</c>; every create and transition
/// requires an Idempotency-Key; draft edits and approval require If-Match (other transitions honour it when sent).
/// Illegal transitions answer 409 <c>order.invalid_transition</c>.
/// </summary>
internal static class OrdersEndpoints
{
    private const string MergePatch = "application/merge-patch+json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("", List).WithName("Orders_List")
            .WithSummary("Sipariş listesi: yeniden eskiye keyset sayfalama; durum, müşteri, kaynak, tarih aralığı filtresi, sipariş no araması")
            .RequirePermission(Permissions.OrdersRead);
        group.MapGet("/{id:guid}", Get).WithName("Orders_Get")
            .WithSummary("Sipariş detayı: satırlar, toplamlar ve KDV dökümü, zaman çizelgesi, sevkiyatlar (ETag döner)")
            .RequirePermission(Permissions.OrdersRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", Create).WithName("Orders_Create")
            .WithSummary("Taslak sipariş oluşturur; fiyatlar fiyat listesinden çözülür (manuel fiyat/iskonto orders.price.override ister)")
            .RequirePermission(Permissions.OrdersCreate)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPatch("/{id:guid}", Update).WithName("Orders_Update")
            .WithSummary("Taslağı günceller (merge patch; lines verilirse tüm satırlar değişir ve yeniden fiyatlanır) — If-Match zorunlu")
            .RequirePermission(Permissions.OrdersEditDraft)
            .Accepts<UpdateOrderRequest>(MergePatch, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        Transition(group.MapPost("/{id:guid}/submit", Submit).WithName("Orders_Submit")
            .WithSummary("Taslak → Onay bekliyor (en az bir satır, fiyatsız satır yok)").RequirePermission(Permissions.OrdersSubmit));
        Transition(group.MapPost("/{id:guid}/withdraw", Withdraw).WithName("Orders_Withdraw")
            .WithSummary("Onay bekliyor → Taslak").RequirePermission(Permissions.OrdersSubmit));
        Transition(group.MapPost("/{id:guid}/reject", Reject).WithName("Orders_Reject")
            .WithSummary("Onay bekliyor → Taslak (sebep zorunlu)").RequirePermission(Permissions.OrdersApprove));
        Transition(group.MapPost("/{id:guid}/approve", Approve).WithName("Orders_Approve")
            .WithSummary("Onay bekliyor → Onaylandı: fiyat kontrolü ve tüm satırların tek transaction'da rezervasyonu (ya hep ya hiç) — If-Match zorunlu")
            .RequirePermission(Permissions.OrdersApprove));
        // Picking (approved → picking → packed) and shipping are Warehouse operations: /picking-tasks and /shipments
        // drive the order through IOrderFulfillment (OrderStateMachine stays the single place deciding transitions).
        Transition(group.MapPost("/{id:guid}/close-remaining", CloseRemaining).WithName("Orders_CloseRemaining")
            .WithSummary("Kısmi sevk sonrası kalanı kapatır, kalan rezervasyonu serbest bırakır → Sevk edildi (sebep zorunlu)")
            .RequirePermission(Permissions.OrdersApprove));
        Transition(group.MapPost("/{id:guid}/complete", Complete).WithName("Orders_Complete")
            .WithSummary("Sevk edildi → Tamamlandı").RequirePermission(Permissions.OrdersEditApproved));
        Transition(group.MapPost("/{id:guid}/cancel", Cancel).WithName("Orders_Cancel")
            .WithSummary("İptal (sebep zorunlu); onaylı siparişte rezervasyon serbest bırakılır. Kısmi sevk sonrası iptal yok")
            .RequirePermission(Permissions.OrdersCancel));

        return app;
    }

    private static void Transition(RouteHandlerBuilder builder) => builder
        .RequireIdempotencyKey()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <param name="status">Virgülle durum listesi (draft, pending_approval, approved, picking, packed, shipped, completed, cancelled).</param>
    /// <param name="from">Oluşturma tarihi ≥ (yyyy-MM-dd, UTC).</param>
    /// <param name="to">Oluşturma tarihi ≤ (yyyy-MM-dd, UTC).</param>
    /// <param name="q">Sipariş numarası içinde geçen metin.</param>
    private static async Task<Results<Ok<OrderPageResponse>, ProblemHttpResult>> List(
        HttpRequest http,
        OrderQueries queries,
        string? status,
        string? customerId,
        string? source,
        string? from,
        string? to,
        string? q,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var unknown = http.Query.Keys
            .Where(k => !new[] { "status", "customerId", "source", "from", "to", "q", "limit", "cursor" }.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unknown.Count > 0)
        {
            return Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
        }

        var statuses = string.IsNullOrWhiteSpace(status)
            ? []
            : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (!statuses.All(OrderStatuses.All.Contains))
        {
            return Malformed($"status must be a comma-separated subset of: {string.Join(", ", OrderStatuses.All)}");
        }

        Guid? customer = null;
        if (!string.IsNullOrEmpty(customerId))
        {
            if (!Guid.TryParse(customerId, out var parsed))
            {
                return Malformed("customerId must be a UUID");
            }

            customer = parsed;
        }

        if (source is not null && !OrderSources.All.Contains(source))
        {
            return Malformed($"source must be one of: {string.Join(", ", OrderSources.All)}");
        }

        if (!TryDate(from, out var fromDate) || !TryDate(to, out var toDate))
        {
            return Malformed("from and to must be dates (yyyy-MM-dd)");
        }

        var pageSize = OrderQueries.DefaultPageSize;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > OrderQueries.MaxPageSize))
        {
            return Malformed($"limit must be an integer between 1 and {OrderQueries.MaxPageSize}");
        }

        OrderCursor? keyset = null;
        if (cursor is not null && (keyset = OrderCursor.TryDecode(cursor)) is null)
        {
            return Malformed("cursor is invalid for this query");
        }

        var number = OrderText.Clean(q);
        return TypedResults.Ok(await queries.ListAsync(
            new OrderListQuery(statuses, customer, source, fromDate, toDate, number, pageSize, keyset), cancellationToken));
    }

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Get(
        Guid id, OrderQueries queries, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await queries.DetailAsync(id, cancellationToken) is { } detail ? detail : AppError.NotFound());

    private static async Task<Results<Created<OrderDetailResponse>, ProblemHttpResult>> Create(
        CreateOrderRequest request, OrderService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, http.TraceIdentifier, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Order.Version);
        return TypedResults.Created($"/api/v1/orders/{result.Value.Order.Id}", result.Value);
    }

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Update(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (RequiredIfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var (patch, problem) = await ReadMergePatchAsync<UpdateOrderRequest>(http.Request, cancellationToken);
        return problem ?? Ok(http, await service.UpdateAsync(id, patch!, version, cancellationToken));
    }

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Submit(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken) =>
        OptionalIfMatch(ifMatch, out var version) ?? Ok(http, await service.SubmitAsync(id, version, http.TraceIdentifier, cancellationToken));

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Withdraw(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken) =>
        OptionalIfMatch(ifMatch, out var version) ?? Ok(http, await service.WithdrawAsync(id, version, http.TraceIdentifier, cancellationToken));

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Reject(
        Guid id, OrderReasonRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken) =>
        OptionalIfMatch(ifMatch, out var version)
        ?? Ok(http, await service.RejectAsync(id, version, request.Reason, http.TraceIdentifier, cancellationToken));

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Approve(
        Guid id, ApproveOrderRequest? request, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (RequiredIfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        return Ok(http, await service.ApproveAsync(id, version, request?.ConfirmNegativeStock ?? false, http.TraceIdentifier, cancellationToken));
    }

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> CloseRemaining(
        Guid id, OrderReasonRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken) =>
        OptionalIfMatch(ifMatch, out var version)
        ?? Ok(http, await service.CloseRemainingAsync(id, version, request.Reason, http.TraceIdentifier, cancellationToken));

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Complete(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken) =>
        OptionalIfMatch(ifMatch, out var version) ?? Ok(http, await service.CompleteAsync(id, version, http.TraceIdentifier, cancellationToken));

    private static async Task<Results<Ok<OrderDetailResponse>, ProblemHttpResult>> Cancel(
        Guid id, OrderReasonRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, OrderService service, HttpContext http, CancellationToken cancellationToken) =>
        OptionalIfMatch(ifMatch, out var version)
        ?? Ok(http, await service.CancelAsync(id, version, request.Reason, http.TraceIdentifier, cancellationToken));

    // ---- Helpers ----

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

    private static async Task<(Patch<T>? Patch, ProblemHttpResult? Problem)> ReadMergePatchAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            if (await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken) is JsonObject document
                && document.Deserialize<T>(Json) is { } value)
            {
                return (new Patch<T>(value, document.Select(p => p.Key).ToHashSet(StringComparer.Ordinal)), null);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Falls through to the malformed-body problem.
        }

        return (null, Malformed("Body must be a JSON object with correctly typed members"));
    }

    private static Results<Ok<OrderDetailResponse>, ProblemHttpResult> Ok(HttpContext http, AppResult<OrderDetailResponse> result)
    {
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Order.Version);
        return TypedResults.Ok(result.Value);
    }

    /// <summary>RFC 9457 with the stable code, plus <c>shortages</c> / <c>confirmationRequired</c> for stock errors.</summary>
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

        if (error.ConfirmationRequired)
        {
            problem.Extensions["confirmationRequired"] = true;
        }

        if (error.Code is "inventory.busy" or "order.busy")
        {
            http.Response.Headers.RetryAfter = "1";
        }

        return TypedResults.Problem(problem);
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static ProblemHttpResult? RequiredIfMatch(string? ifMatch, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status428PreconditionRequired, "precondition.required", "If-Match header is required"));
        }

        var parsed = OptionalIfMatch(ifMatch, out var value);
        version = value ?? 0;
        return parsed;
    }

    private static ProblemHttpResult? OptionalIfMatch(string? ifMatch, out long? version)
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

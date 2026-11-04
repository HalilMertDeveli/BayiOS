using System.Globalization;
using BayiOS.Modules.Inventory.Application;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Inventory.Api;

/// <summary>
/// Inventory API (api-strategy §12.6). Balances need <c>inventory.read</c>; movement history needs
/// <c>inventory.movements.read</c>; receipts <c>inventory.receive</c>; adjustments <c>inventory.adjust</c>
/// (roles-permissions §3.4). Writes require Idempotency-Key and run in the request transaction.
/// Reservation, release and shipment issue have no endpoint: Orders/Warehouse call <see cref="IInventoryService"/>.
/// </summary>
internal static class InventoryEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory")
            .WithTags("Inventory")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/balances", ListBalances).WithName("Inventory_ListBalances")
            .WithSummary("Stok bakiyeleri (fiziksel, rezerve, satılabilir): keyset sayfalama; variantId/warehouseId/locationId filtresi")
            .RequirePermission(Permissions.InventoryRead);
        group.MapGet("/movements", ListMovements).WithName("Inventory_ListMovements")
            .WithSummary("Stok hareket geçmişi (stok kartı), yeniden eskiye; variantId/warehouseId/type/sourceId filtresi")
            .RequirePermission(Permissions.InventoryMovementsRead);
        group.MapGet("/movements/{id:guid}", GetMovement).WithName("Inventory_GetMovement")
            .WithSummary("Tek stok hareketi").RequirePermission(Permissions.InventoryMovementsRead)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/receipts", CreateReceipt).WithName("Inventory_CreateReceipt")
            .WithSummary("Mal kabul: her satır için receipt hareketi (depo verilmezse varsayılan depo)")
            .RequirePermission(Permissions.InventoryReceive)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/adjustments", CreateAdjustment).WithName("Inventory_CreateAdjustment")
            .WithSummary("Manuel stok düzeltme (sebep zorunlu); rezerve stok düşülemez, negatif stok politikası uygulanır")
            .RequirePermission(Permissions.InventoryAdjust)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<Results<Ok<BalancePageResponse>, ProblemHttpResult>> ListBalances(
        HttpRequest http,
        InventoryQueries queries,
        string? variantId,
        string? warehouseId,
        string? locationId,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "variantId", "warehouseId", "locationId", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (!TryParseGuid(variantId, out var variant) || !TryParseGuid(warehouseId, out var warehouse) || !TryParseGuid(locationId, out var location))
        {
            return Malformed("variantId, warehouseId and locationId must be UUIDs");
        }

        if (ParsePaging(limit, cursor, InventoryQueries.BalanceSort, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return TypedResults.Ok(await queries.ListBalancesAsync(new BalanceQuery(variant, warehouse, location, pageSize, keyset?.Id), cancellationToken));
    }

    /// <param name="type">Hareket tipi (receipt, adjustment_in, adjustment_out, shipment_out, …).</param>
    /// <param name="sourceId">Kaynak belge (ör. mal kabul) kimliği.</param>
    private static async Task<Results<Ok<MovementPageResponse>, ProblemHttpResult>> ListMovements(
        HttpRequest http,
        InventoryQueries queries,
        string? variantId,
        string? warehouseId,
        string? type,
        string? sourceId,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "variantId", "warehouseId", "type", "sourceId", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (!TryParseGuid(variantId, out var variant) || !TryParseGuid(warehouseId, out var warehouse) || !TryParseGuid(sourceId, out var source))
        {
            return Malformed("variantId, warehouseId and sourceId must be UUIDs");
        }

        if (type is not null && !MovementTypes.All.Contains(type))
        {
            return Malformed($"type must be one of: {string.Join(", ", MovementTypes.All)}");
        }

        if (ParsePaging(limit, cursor, InventoryQueries.MovementSort, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return TypedResults.Ok(await queries.ListMovementsAsync(new MovementQuery(variant, warehouse, type, source, pageSize, keyset), cancellationToken));
    }

    private static async Task<Results<Ok<MovementResult>, ProblemHttpResult>> GetMovement(
        Guid id, InventoryQueries queries, CancellationToken cancellationToken) =>
        await queries.GetMovementAsync(id, cancellationToken) is { } movement
            ? TypedResults.Ok(movement)
            : TypedResults.Problem(ApiProblems.Create(StatusCodes.Status404NotFound, "resource.not_found", "Resource not found"));

    private static async Task<Results<Created<ReceiptResult>, ProblemHttpResult>> CreateReceipt(
        CreateReceiptRequest request, IInventoryService inventory, HttpContext http, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var lines = request.Lines ?? [];
        for (var i = 0; i < lines.Count; i++)
        {
            Require(errors, $"lines[{i}].variantId", lines[i]?.VariantId);
            Require(errors, $"lines[{i}].quantity", lines[i]?.Quantity);
        }

        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var command = new ReceiveStock(
            request.WarehouseId,
            [.. lines.Select(l => new ReceiveLine(l.VariantId!.Value, l.LocationId, l.Quantity!.Value, l.UnitCost))],
            Reference: request.Reference,
            CorrelationId: http.TraceIdentifier);
        var result = await inventory.ReceiveAsync(command, cancellationToken);
        return result.Error is { } error
            ? Problem(http, error)
            : TypedResults.Created($"/api/v1/inventory/movements?sourceId={result.Value!.ReceiptId}", result.Value);
    }

    private static async Task<Results<Created<MovementResult>, ProblemHttpResult>> CreateAdjustment(
        CreateAdjustmentRequest request, IInventoryService inventory, HttpContext http, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "variantId", request.VariantId);
        Require(errors, "warehouseId", request.WarehouseId);
        Require(errors, "quantity", request.Quantity);
        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var command = new AdjustStock(
            request.VariantId!.Value,
            request.WarehouseId!.Value,
            request.LocationId,
            request.Type ?? string.Empty,
            request.Quantity!.Value,
            request.Reason ?? string.Empty,
            request.ConfirmNegativeStock ?? false,
            http.TraceIdentifier);
        var result = await inventory.AdjustAsync(command, cancellationToken);
        return result.Error is { } error
            ? Problem(http, error)
            : TypedResults.Created($"/api/v1/inventory/movements/{result.Value!.Id}", result.Value);
    }

    // ---- Helpers ----

    private static void Require<T>(Dictionary<string, string[]> errors, string field, T? value)
        where T : struct
    {
        if (value is null)
        {
            errors[field] = ["Required."];
        }
    }

    /// <summary>Maps an engine error to RFC 9457, with <c>shortages</c> / <c>confirmationRequired</c> extensions.</summary>
    private static ProblemHttpResult Problem(HttpContext http, InventoryError error)
    {
        var problem = error.Errors is null
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors);
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

        if (error.Code == InventoryError.Busy().Code)
        {
            http.Response.Headers.RetryAfter = "1";
        }

        return TypedResults.Problem(problem);
    }

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    /// <summary>Unknown query parameters are rejected (api-strategy §5.2).</summary>
    private static ProblemHttpResult? RejectUnknownQuery(HttpRequest request, params string[] allowed)
    {
        var unknown = request.Query.Keys.Where(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        return unknown.Count == 0 ? null : Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
    }

    private static ProblemHttpResult? ParsePaging(string? limit, string? cursor, string sort, out int pageSize, out InventoryCursor? keyset)
    {
        pageSize = DefaultLimit;
        keyset = null;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxLimit))
        {
            return Malformed($"limit must be an integer between 1 and {MaxLimit}");
        }

        if (cursor is not null)
        {
            if (!InventoryCursor.TryDecode(cursor, sort, out var decoded))
            {
                return Malformed("cursor is invalid for this query");
            }

            keyset = decoded;
        }

        return null;
    }

    private static bool TryParseGuid(string? value, out Guid? id)
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
}

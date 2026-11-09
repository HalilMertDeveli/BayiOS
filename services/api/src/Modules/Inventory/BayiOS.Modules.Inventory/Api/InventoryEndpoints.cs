using System.Globalization;
using BayiOS.Modules.Inventory.Application;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
            .WithSummary("Stok bakiyeleri (fiziksel, rezerve, satılabilir): keyset sayfalama; variantId/warehouseId/locationId filtresi, q = SKU/ad metin araması veya tam barkod")
            .RequirePermission(Permissions.InventoryRead)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
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
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesRetryableConflict("inventory.busy");
        group.MapPost("/adjustments", CreateAdjustment).WithName("Inventory_CreateAdjustment")
            .WithSummary("Manuel stok düzeltme (sebep zorunlu); rezerve stok düşülemez, negatif stok politikası uygulanır")
            .RequirePermission(Permissions.InventoryAdjust)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesRetryableConflict("inventory.busy")
            .ProducesStockProblems();
        group.MapPost("/relocations", CreateRelocation).WithName("Inventory_CreateRelocation")
            .WithSummary("Raf/göz arası anlık taşıma (aynı depo): her satır için tek işlemde transfer_out + transfer_in; yalnızca satılabilir miktar taşınır")
            .WithDescription(
                "Depolar arası transfer iki aşamalıdır: POST /api/v1/stock-transfers (OD-01). 409 `inventory.insufficient_available` "
                + "(`shortages`) when a source shelf has less available stock than requested; stock never goes negative.")
            .RequirePermission(Permissions.InventoryTransfer)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesRetryableConflict("inventory.busy")
            .ProducesStockProblems();

        // Minimum stock thresholds (#137): read with inventory.read, managed with catalog.write (product data, PRD CAT-12).
        group.MapGet("/thresholds", ListThresholds).WithName("Inventory_ListThresholds")
            .WithSummary("Minimum stok eşikleri: variantId / variantIds (virgülle, en çok 100) / warehouseId filtresi; warehouseId boş satır = varyantın genel eşiği")
            .RequirePermission(Permissions.InventoryRead);
        group.MapGet("/thresholds/{id:guid}", GetThreshold).WithName("Inventory_GetThreshold")
            .WithSummary("Tek minimum stok eşiği (ETag döner)")
            .RequirePermission(Permissions.InventoryRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/thresholds", SetThreshold).WithName("Inventory_SetThreshold")
            .WithSummary("Varyant (+ depo) için minimum stok eşiğini oluşturur veya değiştirir (upsert, son yazan kazanır); depo boş = genel eşik, depo = o depoda geçerli istisna")
            .RequirePermission(Permissions.CatalogWrite)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesRetryableConflict("inventory.busy");
        group.MapPost("/thresholds/bulk", BulkThresholds).WithName("Inventory_BulkThresholds")
            .WithSummary("Toplu eşik yazımı (en çok 500 satır, hepsi ya da hiçbiri): minQuantity ile ayarla, remove: true ile kaldır")
            .RequirePermission(Permissions.CatalogWrite)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesRetryableConflict("inventory.busy");
        group.MapPut("/thresholds/{id:guid}", UpdateThreshold).WithName("Inventory_UpdateThreshold")
            .WithSummary("Eşiğin minimum miktarını değiştirir (If-Match zorunlu)")
            .RequirePermission(Permissions.CatalogWrite)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesRetryableConflict("inventory.busy");
        group.MapDelete("/thresholds/{id:guid}", DeleteThreshold).WithName("Inventory_DeleteThreshold")
            .WithSummary("Eşiği kaldırır (If-Match zorunlu)")
            .RequirePermission(Permissions.CatalogWrite)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesRetryableConflict("inventory.busy");

        return app;
    }

    private const int MaxThresholdVariantIds = 100;

    /// <param name="variantIds">Virgülle ayrılmış varyant kimlikleri (en çok 100).</param>
    /// <param name="warehouseId">Yalnızca bu deponun istisnaları.</param>
    private static async Task<Results<Ok<StockThresholdPageResponse>, ProblemHttpResult>> ListThresholds(
        HttpRequest http,
        InventoryQueries queries,
        string? variantId,
        string? variantIds,
        string? warehouseId,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "variantId", "variantIds", "warehouseId", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (!TryParseGuid(variantId, out var variant) || !TryParseGuid(warehouseId, out var warehouse))
        {
            return Malformed("variantId and warehouseId must be UUIDs");
        }

        var ids = new List<Guid>();
        if (variant is { } single)
        {
            ids.Add(single);
        }

        if (!string.IsNullOrEmpty(variantIds))
        {
            foreach (var part in variantIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var id))
                {
                    return Malformed("variantIds must be a comma-separated list of UUIDs");
                }

                ids.Add(id);
            }
        }

        ids = [.. ids.Distinct()];
        if (ids.Count > MaxThresholdVariantIds)
        {
            return Malformed($"At most {MaxThresholdVariantIds} variant ids are allowed");
        }

        if (ParsePaging(limit, cursor, InventoryQueries.ThresholdSort, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return TypedResults.Ok(await queries.ListThresholdsAsync(new ThresholdQuery(ids, warehouse, pageSize, keyset?.Id), cancellationToken));
    }

    private static async Task<Results<Ok<StockThresholdResponse>, ProblemHttpResult>> GetThreshold(
        Guid id, InventoryQueries queries, HttpContext http, CancellationToken cancellationToken)
    {
        if (await queries.GetThresholdAsync(id, cancellationToken) is not { } threshold)
        {
            return TypedResults.Problem(ApiProblems.Create(StatusCodes.Status404NotFound, "resource.not_found", "Resource not found"));
        }

        http.Response.Headers.ETag = ETag(threshold.Version);
        return TypedResults.Ok(threshold);
    }

    private static async Task<Results<Created<StockThresholdResponse>, Ok<StockThresholdResponse>, ProblemHttpResult>> SetThreshold(
        SetStockThresholdRequest request, ThresholdService thresholds, HttpContext http, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "variantId", request.VariantId);
        Require(errors, "minQuantity", request.MinQuantity);
        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var result = await thresholds.SetAsync(request.VariantId!.Value, request.WarehouseId, request.MinQuantity!.Value, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        var (threshold, created) = (result.Value!.Threshold, result.Value.Created);
        http.Response.Headers.ETag = ETag(threshold.Version);
        return created
            ? TypedResults.Created($"/api/v1/inventory/thresholds/{threshold.Id}", threshold)
            : TypedResults.Ok(threshold);
    }

    private static async Task<Results<Ok<BulkStockThresholdResponse>, ProblemHttpResult>> BulkThresholds(
        BulkStockThresholdRequest request, ThresholdService thresholds, HttpContext http, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var items = request.Items ?? [];
        var changes = new List<ThresholdChange>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null)
            {
                errors[$"items[{i}]"] = ["Required."];
                continue;
            }

            Require(errors, $"items[{i}].variantId", item.VariantId);
            var remove = item.Remove ?? false;
            if (remove && item.MinQuantity is not null)
            {
                errors[$"items[{i}].minQuantity"] = ["Omit minQuantity when remove is true."];
            }
            else if (!remove)
            {
                Require(errors, $"items[{i}].minQuantity", item.MinQuantity);
            }

            if (item.VariantId is { } variantId)
            {
                changes.Add(new ThresholdChange(variantId, item.WarehouseId, remove ? null : item.MinQuantity));
            }
        }

        if (items.Count == 0)
        {
            errors["items"] = [$"Between 1 and {ThresholdService.MaxBulkItems} items are required."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var result = await thresholds.BulkAsync(changes, single: false, cancellationToken);
        return result.Error is { } error ? Problem(http, error) : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<StockThresholdResponse>, ProblemHttpResult>> UpdateThreshold(
        Guid id,
        UpdateStockThresholdRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ThresholdService thresholds,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var result = await thresholds.UpdateAsync(id, request.MinQuantity, version, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(http, error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteThreshold(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ThresholdService thresholds,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var result = await thresholds.DeleteAsync(id, version, cancellationToken);
        return result.Error is { } error ? Problem(http, error) : TypedResults.NoContent();
    }

    private static ProblemHttpResult? IfMatch(string? ifMatch, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status428PreconditionRequired, "precondition.required", "If-Match header is required"));
        }

        var trimmed = ifMatch.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return long.TryParse(trimmed.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            ? null
            : TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status412PreconditionFailed, "concurrency.version_mismatch", "If-Match does not match the current version"));
    }

    private static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <param name="q">SKU, varyant adı veya ürün adı içinde geçen metin ya da tam barkod (en çok 100 karakter).</param>
    private static async Task<Results<Ok<BalancePageResponse>, ProblemHttpResult>> ListBalances(
        HttpRequest http,
        InventoryQueries queries,
        string? variantId,
        string? warehouseId,
        string? locationId,
        string? q,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "variantId", "warehouseId", "locationId", "q", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        if (!TryParseGuid(variantId, out var variant) || !TryParseGuid(warehouseId, out var warehouse) || !TryParseGuid(locationId, out var location))
        {
            return Malformed("variantId, warehouseId and locationId must be UUIDs");
        }

        var term = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (term is { Length: > MaxSearchLength })
        {
            return Malformed($"q must be at most {MaxSearchLength} characters");
        }

        if (ParsePaging(limit, cursor, InventoryQueries.BalanceSort, out var pageSize, out var keyset) is { } pagingProblem)
        {
            return pagingProblem;
        }

        return await queries.ListBalancesAsync(new BalanceQuery(variant, warehouse, location, pageSize, keyset?.Id, term), cancellationToken) is { } page
            ? TypedResults.Ok(page)
            : TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status422UnprocessableEntity,
                "inventory.search_too_broad",
                $"The search matches more than {InventoryQueries.MaxSearchMatches} products; use a longer term"));
    }

    private const int MaxSearchLength = 100;

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
            http.TraceIdentifier);
        var result = await inventory.AdjustAsync(command, cancellationToken);
        return result.Error is { } error
            ? Problem(http, error)
            : TypedResults.Created($"/api/v1/inventory/movements/{result.Value!.Id}", result.Value);
    }

    private static async Task<Results<Created<TransferResult>, ProblemHttpResult>> CreateRelocation(
        CreateRelocationRequest request, IInventoryService inventory, HttpContext http, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "warehouseId", request.WarehouseId);
        var lines = request.Lines ?? [];
        for (var i = 0; i < lines.Count; i++)
        {
            Require(errors, $"lines[{i}].variantId", lines[i]?.VariantId);
            Require(errors, $"lines[{i}].fromLocationId", lines[i]?.FromLocationId);
            Require(errors, $"lines[{i}].toLocationId", lines[i]?.ToLocationId);
            Require(errors, $"lines[{i}].quantity", lines[i]?.Quantity);
        }

        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var relocationId = Guid.CreateVersion7();
        var warehouse = request.WarehouseId!.Value;
        var command = new TransferStock(
            relocationId,
            [.. lines.Select(l => new TransferMove(
                Guid.CreateVersion7(),
                l.VariantId!.Value,
                StockPoint.At(warehouse, l.FromLocationId),
                StockPoint.At(warehouse, l.ToLocationId),
                l.Quantity!.Value))],
            request.Reference,
            http.TraceIdentifier);
        var result = await inventory.TransferAsync(command, cancellationToken);
        return result.Error is { } error
            ? Problem(http, error)
            : TypedResults.Created($"/api/v1/inventory/movements?sourceId={relocationId}", result.Value);
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

    /// <summary>Maps an engine error to RFC 9457, with the <c>shortages</c> extension.</summary>
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

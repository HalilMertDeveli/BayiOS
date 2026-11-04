using System.Globalization;
using BayiOS.Modules.Warehouse.Application;
using BayiOS.Modules.Warehouse.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Warehouse.Api;

/// <summary>
/// Warehouse master data (api-strategy §12.6). Reads need <c>inventory.read</c>; changes need
/// <c>warehouses.manage</c> (roles-permissions §3.4). Creates require Idempotency-Key; updates require If-Match.
/// </summary>
internal static class WarehouseEndpoints
{
    private const string MergePatch = "application/merge-patch+json";

    public static IEndpointRouteBuilder MapWarehouseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Warehouse")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/warehouses", List).WithName("Warehouse_List")
            .WithSummary("Depolar (sayfasız, ≤ 1000; varsayılan depo önce)").RequirePermission(Permissions.InventoryRead);
        group.MapGet("/warehouses/{id:guid}", Get).WithName("Warehouse_Get")
            .WithSummary("Depo (ETag döner)").RequirePermission(Permissions.InventoryRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/warehouses", Create).WithName("Warehouse_Create")
            .WithSummary("Depo oluşturur (tenant'ın ilk deposu varsayılan olur)").RequirePermission(Permissions.WarehousesManage)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        Update(group.MapPatch("/warehouses/{id:guid}", UpdateWarehouse).WithName("Warehouse_Update")
            .WithSummary("Depoyu günceller: ad, durum, varsayılan, lokasyon kullanımı (stok yokken) — If-Match zorunlu")
            .RequirePermission(Permissions.WarehousesManage)
            .Accepts<UpdateWarehouseRequest>(MergePatch, "application/json"));

        group.MapGet("/warehouses/{id:guid}/locations", ListLocations).WithName("Warehouse_ListLocations")
            .WithSummary("Deponun raf/göz lokasyonları (toplama sırasıyla)").RequirePermission(Permissions.InventoryRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/warehouses/{id:guid}/locations", CreateLocation).WithName("Warehouse_CreateLocation")
            .WithSummary("Lokasyon oluşturur (yalnızca lokasyon kullanan depoda)").RequirePermission(Permissions.WarehousesManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        Update(group.MapPatch("/locations/{id:guid}", UpdateLocation).WithName("Warehouse_UpdateLocation")
            .WithSummary("Lokasyonu günceller (If-Match zorunlu)").RequirePermission(Permissions.WarehousesManage)
            .Accepts<UpdateLocationRequest>(MergePatch, "application/json"));

        return app;
    }

    private static void Update(RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static async Task<Results<Ok<WarehouseListResponse>, ProblemHttpResult>> List(
        HttpRequest http, WarehouseService service, bool? includeInactive, CancellationToken cancellationToken) =>
        RejectUnknownQuery(http, "includeInactive") is { } problem
            ? problem
            : TypedResults.Ok(new WarehouseListResponse(await service.ListAsync(includeInactive ?? false, cancellationToken)));

    private static async Task<Results<Ok<WarehouseResponse>, ProblemHttpResult>> Get(
        Guid id, WarehouseService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.GetAsync(id, cancellationToken), w => w.Version);

    private static async Task<Results<Created<WarehouseResponse>, ProblemHttpResult>> Create(
        CreateWarehouseRequest request, WarehouseService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateAsync(request, cancellationToken), w => $"/api/v1/warehouses/{w.Id}", w => w.Version);

    private static async Task<Results<Ok<WarehouseResponse>, ProblemHttpResult>> UpdateWarehouse(
        Guid id, UpdateWarehouseRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, WarehouseService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem
            ? problem
            : Ok(http, await service.UpdateAsync(id, request, version, cancellationToken), w => w.Version);

    private static async Task<Results<Ok<LocationListResponse>, ProblemHttpResult>> ListLocations(
        Guid id, HttpRequest http, WarehouseService service, bool? includeInactive, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "includeInactive") is { } unknown)
        {
            return unknown;
        }

        var result = await service.ListLocationsAsync(id, includeInactive ?? false, cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.Ok(new LocationListResponse(result.Value!));
    }

    private static async Task<Results<Created<LocationResponse>, ProblemHttpResult>> CreateLocation(
        Guid id, CreateLocationRequest request, WarehouseService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateLocationAsync(id, request, cancellationToken), l => $"/api/v1/warehouses/{l.WarehouseId}/locations", l => l.Version);

    private static async Task<Results<Ok<LocationResponse>, ProblemHttpResult>> UpdateLocation(
        Guid id, UpdateLocationRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, WarehouseService service, HttpContext http, CancellationToken cancellationToken) =>
        IfMatch(ifMatch, out var version) is { } problem
            ? problem
            : Ok(http, await service.UpdateLocationAsync(id, request, version, cancellationToken), l => l.Version);

    // ---- Helpers ----

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(HttpContext http, AppResult<T> result, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Ok(result.Value);
    }

    private static Results<Created<T>, ProblemHttpResult> Created<T>(HttpContext http, AppResult<T> result, Func<T, string> location, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Created(location(result.Value!), result.Value);
    }

    private static ProblemHttpResult Problem(AppError error) => TypedResults.Problem(
        error.Errors is null
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors));

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static ProblemHttpResult? RejectUnknownQuery(HttpRequest request, params string[] allowed)
    {
        var unknown = request.Query.Keys.Where(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        return unknown.Count == 0 ? null : Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
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
}

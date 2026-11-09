using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Pricing.Application;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Pricing.Api;

/// <summary>
/// Price lists, items and price quotes (api-strategy §12.4). Reads need <c>prices.read.sell</c> (purchase lists also
/// <c>prices.read.cost</c>); changes need <c>prices.write</c> (roles-permissions §3.2). Creates and bulk item upserts
/// require Idempotency-Key; list updates require If-Match.
/// </summary>
internal static class PricingEndpoints
{
    private const string MergePatch = "application/merge-patch+json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Pricing")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/price-lists", List).WithName("Pricing_ListPriceLists")
            .WithSummary("Fiyat listeleri (sayfasız, ≤ 1000; varsayılan önce)").RequirePermission(Permissions.PricesReadSell);
        group.MapGet("/price-lists/{id:guid}", Get).WithName("Pricing_GetPriceList")
            .WithSummary("Fiyat listesi (ETag döner)").RequirePermission(Permissions.PricesReadSell)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/price-lists", Create).WithName("Pricing_CreatePriceList")
            .WithSummary("Fiyat listesi oluşturur (para biriminin ilk satış listesi varsayılan olur)").RequirePermission(Permissions.PricesWrite)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPatch("/price-lists/{id:guid}", Update).WithName("Pricing_UpdatePriceList")
            .WithSummary("Fiyat listesini günceller: ad, varsayılan, KDV modu, geçerlilik, taban liste + iskonto, durum — If-Match zorunlu")
            .RequirePermission(Permissions.PricesWrite)
            .Accepts<UpdatePriceListRequest>(MergePatch, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/price-lists/{id:guid}/items", ListItems).WithName("Pricing_ListItems")
            .WithSummary("Liste kalemleri: keyset sayfalama, variantId filtresi").RequirePermission(Permissions.PricesReadSell)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/price-lists/{id:guid}/items", UpsertItems).WithName("Pricing_UpsertItems")
            .WithSummary("Kalemleri toplu ekler/günceller (≤ 500; anahtar variantId + minQuantity)").RequirePermission(Permissions.PricesWrite)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapDelete("/price-lists/{id:guid}/items/{itemId:guid}", DeleteItem).WithName("Pricing_DeleteItem")
            .WithSummary("Kalemi siler").RequirePermission(Permissions.PricesWrite)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/pricing/quote", Quote).WithName("Pricing_Quote")
            .WithSummary("Fiyat çözümler (yan etkisiz): müşteri listesi → bayi grubu listesi → varsayılan liste; bulunamazsa no_price")
            .RequirePermission(Permissions.PricesReadSell)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<Results<Ok<PriceListListResponse>, ProblemHttpResult>> List(
        HttpRequest http, PriceListService service, bool? includeArchived, CancellationToken cancellationToken) =>
        RejectUnknownQuery(http, "includeArchived") is { } problem
            ? problem
            : TypedResults.Ok(new PriceListListResponse(await service.ListAsync(includeArchived ?? false, cancellationToken)));

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> Get(
        Guid id, PriceListService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.GetAsync(id, cancellationToken), l => l.Version);

    private static async Task<Results<Created<PriceListResponse>, ProblemHttpResult>> Create(
        CreatePriceListRequest request, PriceListService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Created($"/api/v1/price-lists/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> Update(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, PriceListService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var (patch, present, problem) = await ReadMergePatchAsync<UpdatePriceListRequest>(http.Request, cancellationToken);
        return problem ?? Ok(http, await service.UpdateAsync(id, new PriceListPatch(patch!, present), version, cancellationToken), l => l.Version);
    }

    private static async Task<Results<Ok<PriceListItemPageResponse>, ProblemHttpResult>> ListItems(
        Guid id, HttpRequest http, PriceListService service, string? variantId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "variantId", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        Guid? variant = null;
        if (!string.IsNullOrEmpty(variantId))
        {
            if (!Guid.TryParse(variantId, out var parsed))
            {
                return Malformed("variantId must be a UUID");
            }

            variant = parsed;
        }

        var pageSize = PriceListService.DefaultPageSize;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > PriceListService.MaxPageSize))
        {
            return Malformed($"limit must be an integer between 1 and {PriceListService.MaxPageSize}");
        }

        Guid? after = null;
        if (cursor is not null)
        {
            if (!IdCursor.TryDecode(cursor, out var decoded))
            {
                return Malformed("cursor is invalid for this query");
            }

            after = decoded;
        }

        var result = await service.ListItemsAsync(id, variant, pageSize, after, cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<UpsertPriceListItemsResponse>, ProblemHttpResult>> UpsertItems(
        Guid id, UpsertPriceListItemsRequest request, PriceListService service, CancellationToken cancellationToken)
    {
        var result = await service.UpsertItemsAsync(id, request, cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteItem(
        Guid id, Guid itemId, PriceListService service, CancellationToken cancellationToken)
    {
        var result = await service.DeleteItemAsync(id, itemId, cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PriceQuote>, ProblemHttpResult>> Quote(
        PriceQuoteApiRequest request, IPriceResolver resolver, TimeProvider clock, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var lines = request.Lines ?? [];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].VariantId is null)
            {
                errors[$"lines[{i}].variantId"] = ["Required."];
            }

            if (lines[i].Quantity is null)
            {
                errors[$"lines[{i}].quantity"] = ["Required."];
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var date = request.Date ?? TodayInTurkey(clock);
        var result = await resolver.QuoteAsync(
            new PriceQuoteRequest(request.CustomerId, date, [.. lines.Select(l => new PriceQuoteLine(l.VariantId!.Value, l.Quantity!.Value))], request.Currency),
            cancellationToken);
        return result.Error is { } error
            ? TypedResults.Problem(error.Errors is null ? ApiProblems.Create(error.Status, error.Code, error.Title) : ApiProblems.Validation(error.Errors))
            : TypedResults.Ok(result.Value);
    }

    // ---- Helpers ----

    /// <summary>Business date of the MVP market (tenant time zones are all Europe/Istanbul for now).</summary>
    private static DateOnly TodayInTurkey(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Istanbul", out var istanbul) ? istanbul : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    /// <summary>JSON merge patch (RFC 7396): the typed values plus which members were present (null = clear).</summary>
    private static async Task<(T? Value, IReadOnlySet<string> Present, ProblemHttpResult? Problem)> ReadMergePatchAsync<T>(
        HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            if (await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken) is JsonObject document)
            {
                var present = document.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                if (document.Deserialize<T>(Json) is { } value)
                {
                    return (value, present, null);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Falls through to the malformed-body problem.
        }

        return (null, new HashSet<string>(), Malformed("Body must be a JSON object with correctly typed members"));
    }

    private static Results<Ok<T>, ProblemHttpResult> Ok<T>(HttpContext http, AppResult<T> result, Func<T, long> version)
    {
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(version(result.Value!));
        return TypedResults.Ok(result.Value);
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

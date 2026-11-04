using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Customers.Application;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Customers.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Customers.Api;

/// <summary>
/// Customers, addresses and dealer groups (api-strategy §12.5). Reads need <c>customers.read</c>; customer changes
/// <c>customers.write</c>; dealer groups <c>customers.groups.manage</c>; price list / group assignment additionally
/// <c>pricelists.assign</c> (roles-permissions §3.2, §3.5). Creates require Idempotency-Key; updates require If-Match.
/// </summary>
internal static class CustomersEndpoints
{
    private const string MergePatch = "application/merge-patch+json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCustomersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Customers")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/customers", List).WithName("Customers_List")
            .WithSummary("Müşteri listesi: keyset sayfalama (ad sırası), arama (ad/kod/vergi no/telefon/WhatsApp), dealerGroupId ve durum filtresi")
            .RequirePermission(Permissions.CustomersRead);
        group.MapGet("/customers/{id:guid}", Get).WithName("Customers_Get")
            .WithSummary("Müşteri ve adresleri (ETag döner)").RequirePermission(Permissions.CustomersRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/customers", Create).WithName("Customers_Create")
            .WithSummary("Müşteri oluşturur (bayi grubu / fiyat listesi ataması için pricelists.assign gerekir)")
            .RequirePermission(Permissions.CustomersWrite)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        Update(group.MapPatch("/customers/{id:guid}", UpdateCustomer).WithName("Customers_Update")
            .WithSummary("Müşteriyi günceller (merge patch; arşiv = status archived) — If-Match zorunlu")
            .RequirePermission(Permissions.CustomersWrite)
            .Accepts<UpdateCustomerRequest>(MergePatch, "application/json"));
        group.MapPost("/customers/{id:guid}/anonymize", Anonymize).WithName("Customers_Anonymize")
            .WithSummary("KVKK ilgili kişi talebi: kişisel alanları siler, adresleri kaldırır, müşteriyi arşivler (geri alınamaz)")
            .RequirePermission(Permissions.TenantSettingsManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/customers/{id:guid}/addresses", ListAddresses).WithName("Customers_ListAddresses")
            .WithSummary("Müşteri adresleri").RequirePermission(Permissions.CustomersRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/customers/{id:guid}/addresses", CreateAddress).WithName("Customers_CreateAddress")
            .WithSummary("Adres ekler (türünün ilk adresi varsayılan olur)").RequirePermission(Permissions.CustomersWrite)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        Update(group.MapPatch("/customers/{id:guid}/addresses/{addressId:guid}", UpdateAddress).WithName("Customers_UpdateAddress")
            .WithSummary("Adresi günceller (If-Match zorunlu)").RequirePermission(Permissions.CustomersWrite)
            .Accepts<UpdateAddressRequest>(MergePatch, "application/json"));
        group.MapDelete("/customers/{id:guid}/addresses/{addressId:guid}", DeleteAddress).WithName("Customers_DeleteAddress")
            .WithSummary("Adresi siler (varsayılansa sıradaki adres varsayılan olur)").RequirePermission(Permissions.CustomersWrite)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/dealer-groups", ListGroups).WithName("Customers_ListDealerGroups")
            .WithSummary("Bayi grupları (sayfasız, ≤ 1000)").RequirePermission(Permissions.CustomersRead);
        group.MapGet("/dealer-groups/{id:guid}", GetGroup).WithName("Customers_GetDealerGroup")
            .WithSummary("Bayi grubu (ETag döner)").RequirePermission(Permissions.CustomersRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/dealer-groups", CreateGroup).WithName("Customers_CreateDealerGroup")
            .WithSummary("Bayi grubu oluşturur (fiyat listesi ataması için pricelists.assign gerekir)")
            .RequirePermission(Permissions.CustomersGroupsManage)
            .RequireIdempotencyKey()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        Update(group.MapPatch("/dealer-groups/{id:guid}", UpdateGroup).WithName("Customers_UpdateDealerGroup")
            .WithSummary("Bayi grubunu günceller: ad, fiyat listesi, varsayılan iskonto, durum — If-Match zorunlu")
            .RequirePermission(Permissions.CustomersGroupsManage)
            .Accepts<UpdateDealerGroupRequest>(MergePatch, "application/json"));

        return app;
    }

    private static void Update(RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status412PreconditionFailed)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    // ---- Customers ----

    /// <param name="q">Ad veya kod içinde geçen metin; ya da tam vergi no / telefon / WhatsApp numarası.</param>
    /// <param name="status">Virgülle: active, inactive, archived. Varsayılan active,inactive.</param>
    private static async Task<Results<Ok<CustomerPageResponse>, ProblemHttpResult>> List(
        HttpRequest http,
        CustomerService service,
        string? q,
        string? dealerGroupId,
        string? status,
        string? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (RejectUnknownQuery(http, "q", "dealerGroupId", "status", "limit", "cursor") is { } unknown)
        {
            return unknown;
        }

        Guid? groupId = null;
        if (!string.IsNullOrEmpty(dealerGroupId))
        {
            if (!Guid.TryParse(dealerGroupId, out var parsed))
            {
                return Malformed("dealerGroupId must be a UUID");
            }

            groupId = parsed;
        }

        var statuses = string.IsNullOrWhiteSpace(status)
            ? [CustomerStatuses.Active, CustomerStatuses.Inactive]
            : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (!statuses.All(CustomerStatuses.All.Contains))
        {
            return Malformed($"status must be a comma-separated subset of: {string.Join(", ", CustomerStatuses.All)}");
        }

        var pageSize = CustomerService.DefaultPageSize;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > CustomerService.MaxPageSize))
        {
            return Malformed($"limit must be an integer between 1 and {CustomerService.MaxPageSize}");
        }

        NameCursor? keyset = null;
        if (cursor is not null && (keyset = NameCursor.TryDecode(cursor)) is null)
        {
            return Malformed("cursor is invalid for this query");
        }

        return TypedResults.Ok(await service.ListAsync(new CustomerListQuery(q, groupId, statuses, pageSize, keyset), cancellationToken));
    }

    private static async Task<Results<Ok<CustomerDetailResponse>, ProblemHttpResult>> Get(
        Guid id, CustomerService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.GetAsync(id, cancellationToken), c => c.Customer.Version);

    private static async Task<Results<Created<CustomerDetailResponse>, ProblemHttpResult>> Create(
        CreateCustomerRequest request, CustomerService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateAsync(request, cancellationToken), c => $"/api/v1/customers/{c.Customer.Id}", c => c.Customer.Version);

    private static async Task<Results<Ok<CustomerDetailResponse>, ProblemHttpResult>> UpdateCustomer(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, CustomerService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var (patch, problem) = await ReadMergePatchAsync<UpdateCustomerRequest>(http.Request, cancellationToken);
        return problem ?? Ok(http, await service.UpdateAsync(id, patch!, version, cancellationToken), c => c.Customer.Version);
    }

    private static async Task<Results<Ok<CustomerDetailResponse>, ProblemHttpResult>> Anonymize(
        Guid id, CustomerService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.AnonymizeAsync(id, cancellationToken), c => c.Customer.Version);

    // ---- Addresses ----

    private static async Task<Results<Ok<AddressListResponse>, ProblemHttpResult>> ListAddresses(
        Guid id, CustomerService service, CancellationToken cancellationToken)
    {
        var result = await service.ListAddressesAsync(id, cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Created<AddressResponse>, ProblemHttpResult>> CreateAddress(
        Guid id, CreateAddressRequest request, CustomerService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateAddressAsync(id, request, cancellationToken), a => $"/api/v1/customers/{a.CustomerId}/addresses", a => a.Version);

    private static async Task<Results<Ok<AddressResponse>, ProblemHttpResult>> UpdateAddress(
        Guid id, Guid addressId, [FromHeader(Name = "If-Match")] string? ifMatch, CustomerService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var (patch, problem) = await ReadMergePatchAsync<UpdateAddressRequest>(http.Request, cancellationToken);
        return problem ?? Ok(http, await service.UpdateAddressAsync(id, addressId, patch!, version, cancellationToken), a => a.Version);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAddress(
        Guid id, Guid addressId, CustomerService service, CancellationToken cancellationToken)
    {
        var result = await service.DeleteAddressAsync(id, addressId, cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.NoContent();
    }

    // ---- Dealer groups ----

    private static async Task<Results<Ok<DealerGroupListResponse>, ProblemHttpResult>> ListGroups(
        HttpRequest http, DealerGroupService service, bool? includeArchived, CancellationToken cancellationToken) =>
        RejectUnknownQuery(http, "includeArchived") is { } problem
            ? problem
            : TypedResults.Ok(new DealerGroupListResponse(await service.ListAsync(includeArchived ?? false, cancellationToken)));

    private static async Task<Results<Ok<DealerGroupResponse>, ProblemHttpResult>> GetGroup(
        Guid id, DealerGroupService service, HttpContext http, CancellationToken cancellationToken) =>
        Ok(http, await service.GetAsync(id, cancellationToken), g => g.Version);

    private static async Task<Results<Created<DealerGroupResponse>, ProblemHttpResult>> CreateGroup(
        CreateDealerGroupRequest request, DealerGroupService service, HttpContext http, CancellationToken cancellationToken) =>
        Created(http, await service.CreateAsync(request, cancellationToken), g => $"/api/v1/dealer-groups/{g.Id}", g => g.Version);

    private static async Task<Results<Ok<DealerGroupResponse>, ProblemHttpResult>> UpdateGroup(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, DealerGroupService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (IfMatch(ifMatch, out var version) is { } precondition)
        {
            return precondition;
        }

        var (patch, problem) = await ReadMergePatchAsync<UpdateDealerGroupRequest>(http.Request, cancellationToken);
        return problem ?? Ok(http, await service.UpdateAsync(id, patch!, version, cancellationToken), g => g.Version);
    }

    // ---- Helpers ----

    /// <summary>JSON merge patch (RFC 7396): the typed values plus which members were present (null = clear).</summary>
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

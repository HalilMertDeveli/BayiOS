using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using BayiOS.Modules.Identity.Application;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Identity.Api;

internal static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Identity")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", GetMe)
            .WithName("Identity_GetMe")
            .WithSummary("Oturumdaki kullanıcının profili ve tenant üyelikleri; X-Tenant-Id verilirse o tenant'taki etkin izinler (activeTenant)")
            .WithMetadata(new TenantOptionalMetadata());

        group.MapPost("/tenants", CreateTenant)
            .WithName("Identity_CreateTenant")
            .WithSummary("Yeni tenant oluşturur; çağıran kullanıcı owner olur")
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tenant", GetCurrentTenant)
            .WithName("Identity_GetCurrentTenant")
            .WithSummary("X-Tenant-Id ile seçilen aktif tenant")
            .RequirePermission(Permissions.TenantRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPatch("/tenant", UpdateTenant)
            .WithName("Identity_UpdateTenant")
            .WithSummary("İşletme profili: ad, sektör, para birimi, logo yolu, vergi no/dairesi, telefon (JSON merge patch, If-Match zorunlu)")
            .RequirePermission(Permissions.TenantSettingsManage)
            .Accepts<UpdateTenantRequest>("application/merge-patch+json", "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/tenant/settings", GetSettings)
            .WithName("Identity_GetTenantSettings")
            .WithSummary("Aktif tenant'ın ayarlanabilir kuralları (ETag döner)")
            .RequirePermission(Permissions.TenantSettingsManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPatch("/tenant/settings", UpdateSettings)
            .WithName("Identity_UpdateTenantSettings")
            .WithSummary("Tenant kurallarını günceller (JSON merge patch, If-Match zorunlu)")
            .RequirePermission(Permissions.TenantSettingsManage)
            .Accepts<UpdateTenantSettingsRequest>("application/merge-patch+json", "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/members", ListMembers)
            .WithName("Identity_ListMembers")
            .WithSummary("Aktif tenant'ın üyeleri (personel seçici): görünen ad, e-posta, rol, durum; rol/durum/izin filtresi, keyset sayfalama")
            .RequirePermission(Permissions.MembersRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <param name="role">Virgülle rol listesi (owner, admin, manager, sales, warehouse_staff, viewer).</param>
    /// <param name="status">Virgülle durum listesi (invited, active, disabled); verilmezse tümü.</param>
    /// <param name="limit">1–200 (varsayılan 50).</param>
    /// <param name="cursor">Önceki sayfanın <c>nextCursor</c> değeri.</param>
    /// <param name="permission">
    /// Virgülle izin listesi (ör. <c>tasks.pick</c>): yalnızca rolü bu izinlerin hepsini aktif tenant kurallarıyla taşıyan
    /// üyeler (koşullu izin dahil; kuralı kapalı K hariç). Personel seçiciler için.
    /// </param>
    private static async Task<Results<Ok<MemberPageResponse>, ProblemHttpResult>> ListMembers(
        HttpRequest http,
        MemberQueries queries,
        string? role,
        string? status,
        string? limit,
        string? cursor,
        string? permission,
        CancellationToken cancellationToken)
    {
        var unknown = http.Query.Keys
            .Where(k => !new[] { "role", "status", "limit", "cursor", "permission" }.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unknown.Count > 0)
        {
            return Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
        }

        var roles = SplitList(role);
        if (!roles.All(Roles.All.Contains))
        {
            return Malformed($"role must be a comma-separated subset of: {string.Join(", ", Roles.All)}");
        }

        string[] memberStatuses = ["invited", "active", "disabled"];
        var statuses = SplitList(status);
        if (!statuses.All(memberStatuses.Contains))
        {
            return Malformed($"status must be a comma-separated subset of: {string.Join(", ", memberStatuses)}");
        }

        var permissions = SplitList(permission);
        if (permissions.FirstOrDefault(p => !RolePermissions.AllPermissions.Contains(p)) is { } unknownPermission)
        {
            return Malformed($"permission '{unknownPermission}' is not a permission id (roles-permissions §3)");
        }

        var pageSize = MemberQueries.DefaultPageSize;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MemberQueries.MaxPageSize))
        {
            return Malformed($"limit must be an integer between 1 and {MemberQueries.MaxPageSize}");
        }

        Guid? after = null;
        if (cursor is not null && (after = MemberCursor.TryDecode(cursor)) is null)
        {
            return Malformed("cursor is invalid for this query");
        }

        return TypedResults.Ok(await queries.ListAsync(new MemberListQuery(roles, statuses, pageSize, after, permissions), cancellationToken));
    }

    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));

    private static async Task<Ok<MeResponse>> GetMe(IdentityService service, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetMeAsync(user.FindFirstValue("email"), cancellationToken));

    private static async Task<Results<Created<TenantResponse>, ProblemHttpResult>> CreateTenant(
        CreateTenantRequest request,
        IdentityService service,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateTenantAsync(request, user.FindFirstValue("email"), cancellationToken);
        return result.Error is { } error
            ? Problem(error)
            : TypedResults.Created("/api/v1/tenant", result.Value);
    }

    private static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> GetCurrentTenant(
        IdentityService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCurrentTenantAsync(cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<TenantResponse>, ProblemHttpResult>> UpdateTenant(
        [FromHeader(Name = "If-Match")] string? ifMatch,
        IdentityService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status428PreconditionRequired, "precondition.required", "If-Match header is required"));
        }

        if (!TryParseETag(ifMatch, out var expectedVersion))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status412PreconditionFailed, "concurrency.version_mismatch", "If-Match does not match the current version"));
        }

        JsonObject? document;
        try
        {
            document = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken) as JsonObject;
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
        {
            return TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", "Body must be a JSON object"));
        }

        var errors = new Dictionary<string, string[]>();
        PatchValue Read(string name)
        {
            if (!document.TryGetPropertyValue(name, out var node))
            {
                return default;
            }

            if (node is null)
            {
                return new PatchValue(true, null);
            }

            if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                return new PatchValue(true, value.GetValue<string>());
            }

            errors[name] = ["Must be a string or null."];
            return default;
        }

        var patch = new TenantPatch(
            Read("name"), Read("sector"), Read("defaultCurrency"), Read("logoObjectPath"), Read("taxNumber"), Read("taxOffice"), Read("phone"));
        if (errors.Count > 0)
        {
            return TypedResults.Problem(ApiProblems.Validation(errors));
        }

        var result = await service.UpdateTenantAsync(patch, expectedVersion, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value!.Version);
        return TypedResults.Ok(result.Value);
    }

    private static async Task<Results<Ok<TenantSettingsResponse>, ProblemHttpResult>> GetSettings(
        IdentityService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await service.GetSettingsAsync(cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value.Version);
        return TypedResults.Ok(result.Value.Settings);
    }

    private static async Task<Results<Ok<TenantSettingsResponse>, ProblemHttpResult>> UpdateSettings(
        UpdateTenantSettingsRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        IdentityService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status428PreconditionRequired, "precondition.required", "If-Match header is required"));
        }

        if (!TryParseETag(ifMatch, out var expectedVersion))
        {
            return TypedResults.Problem(ApiProblems.Create(
                StatusCodes.Status412PreconditionFailed, "concurrency.version_mismatch", "If-Match does not match the current version"));
        }

        var result = await service.UpdateSettingsAsync(request, expectedVersion, cancellationToken);
        if (result.Error is { } error)
        {
            return Problem(error);
        }

        http.Response.Headers.ETag = ETag(result.Value.Version);
        return TypedResults.Ok(result.Value.Settings);
    }

    internal static ProblemHttpResult Problem(AppError error)
    {
        if (error.Errors is not null)
        {
            return TypedResults.Problem(ApiProblems.Validation(error.Errors));
        }

        var problem = ApiProblems.Create(error.Status, error.Code, error.Title);
        foreach (var (name, value) in error.Extensions ?? new Dictionary<string, object?>())
        {
            problem.Extensions[name] = value;
        }

        return TypedResults.Problem(problem);
    }

    private static string ETag(long version) => $"W/\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static bool TryParseETag(string value, out long version)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return long.TryParse(trimmed.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}

using System.Globalization;
using System.Security.Claims;
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
            .WithSummary("Oturumdaki kullanıcının profili ve tenant üyelikleri");

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

        return app;
    }

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
        CancellationToken cancellationToken)
    {
        var result = await service.GetCurrentTenantAsync(cancellationToken);
        return result.Error is { } error ? Problem(error) : TypedResults.Ok(result.Value);
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

    private static ProblemHttpResult Problem(AppError error) => TypedResults.Problem(
        error.Errors is null
            ? ApiProblems.Create(error.Status, error.Code, error.Title)
            : ApiProblems.Validation(error.Errors));

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

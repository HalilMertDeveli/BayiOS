using BayiOS.Modules.Notifications.Application;
using BayiOS.Modules.Notifications.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Notifications.Api;

/// <summary>
/// Push device registrations (api-strategy §12.11, issue #44). Any member of the active tenant manages their own
/// registrations (<c>tenant.read</c> = every role); a request never sees or touches another user's rows (query + RLS).
/// The token is accepted, never returned. Rate-limited like every unit-of-work endpoint.
/// </summary>
internal static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/v1/devices")
            .WithTags("Devices")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        devices.MapGet("", List).WithName("Devices_List")
            .WithSummary("Bu işletmede kendi push kayıtlarım (token döndürülmez)")
            .RequirePermission(Permissions.TenantRead);
        devices.MapPost("", Register).WithName("Devices_Register")
            .WithSummary("FCM token kaydı / yenileme (giriş, işletme seçimi, token yenilenmesi); token başka kullanıcıdaysa bu kullanıcıya geçer")
            .WithDescription(
                "Upsert by token: the same token refreshes the existing registration (lastSeenAt, platform, appVersion, "
                + "locale). A token registered by another user, or by the caller in another tenant, moves to the caller. "
                + "At most Push:MaxDevicesPerUser registrations per user and tenant (least recently seen dropped). The "
                + "token is sensitive: it is never returned or logged.")
            .RequirePermission(Permissions.TenantRead)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        devices.MapDelete("/{id:guid}", Delete).WithName("Devices_Delete")
            .WithSummary("Push kaydını siler (çıkış, işletme değişimi); yalnızca kendi kaydı")
            .RequirePermission(Permissions.TenantRead)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<DeviceListResponse>> List(DeviceService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(new DeviceListResponse(await service.ListAsync(cancellationToken)));

    private static async Task<Results<Ok<DeviceResponse>, ProblemHttpResult>> Register(
        RegisterDeviceRequest request, DeviceService service, CancellationToken cancellationToken)
    {
        var (device, errors) = await service.RegisterAsync(request, cancellationToken);
        return errors is not null ? TypedResults.Problem(ApiProblems.Validation(errors)) : TypedResults.Ok(device!);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Delete(Guid id, DeviceService service, CancellationToken cancellationToken) =>
        await service.DeleteAsync(id, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.Problem(ApiProblems.Create(StatusCodes.Status404NotFound, "resource.not_found", "Resource not found"));
}

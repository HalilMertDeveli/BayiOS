using BayiOS.Modules.Notifications.Contracts;
using BayiOS.Modules.Notifications.Domain;
using BayiOS.Modules.Notifications.Infrastructure.Persistence;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Modules.Notifications.Application;

/// <summary>
/// Push registrations of the caller in the active tenant (api-strategy §12.11). Rows are scoped to tenant + user twice:
/// by the query (<c>user_id = caller</c>) and by RLS (a request sees only its own user's rows). A token registered by
/// someone else — another user or the same user in another tenant — moves to the caller: the device changed hands.
/// </summary>
internal sealed class DeviceService(
    NotificationsDbContext db,
    UnitOfWork unitOfWork,
    IRequestContext requestContext,
    IOptions<PushOptions> options,
    TimeProvider clock)
{
    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No user resolved.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    public async Task<IReadOnlyList<DeviceResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var rows = await db.Devices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAt).ThenByDescending(d => d.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(Map)];
    }

    /// <returns>The registration, or the field errors (400).</returns>
    public async Task<(DeviceResponse? Device, Dictionary<string, string[]>? Errors)> RegisterAsync(
        RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var (tenantId, userId) = (TenantId, UserId);
        var token = request.Token!;
        var platform = request.Platform!.Trim().ToLowerInvariant();
        var appVersion = NullIfBlank(request.AppVersion);
        var locale = NullIfBlank(request.Locale);
        var hash = DeviceToken.Hash(token);
        var ticks = clock.GetUtcNow().UtcTicks;
        var now = new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero); // microseconds, as timestamptz stores

        await db.JoinTransactionAsync(cancellationToken);

        // Takes the token over from any other (tenant, user) under a per-token lock held until commit, so two
        // concurrent registrations of one token never both keep it (SECURITY DEFINER; see migration InitialNotifications).
        await using (var release = new NpgsqlCommand("SELECT notifications.release_device_token(@hash)", unitOfWork.Connection, unitOfWork.Transaction))
        {
            release.Parameters.Add(new NpgsqlParameter<byte[]>("hash", hash));
            await release.ExecuteScalarAsync(cancellationToken);
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenSha256 == hash, cancellationToken);
        if (device is null)
        {
            device = DeviceToken.Register(tenantId, userId, platform, token, appVersion, locale, now);
            db.Devices.Add(device);
        }
        else
        {
            device.Refresh(platform, appVersion, locale, now);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Bounded per user: the least recently seen registrations beyond the cap are dropped (reinstalls, old phones).
        var surplus = await db.Devices
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAt).ThenByDescending(d => d.Id)
            .Skip(options.Value.MaxDevicesPerUser)
            .ToListAsync(cancellationToken);
        if (surplus.Count > 0)
        {
            db.Devices.RemoveRange(surplus);
            await db.SaveChangesAsync(cancellationToken);
        }

        return (Map(device), null);
    }

    /// <summary>Sign-out / tenant switch: removes the caller's own registration. False when there is none (404).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserId;
        await db.JoinTransactionAsync(cancellationToken);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, cancellationToken);
        if (device is null)
        {
            return false;
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static Dictionary<string, string[]> Validate(RegisterDeviceRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(request.Token))
        {
            errors["token"] = ["Required."];
        }
        else if (!DeviceToken.IsValidToken(request.Token))
        {
            errors["token"] = ["Not an FCM registration token."];
        }

        if (string.IsNullOrWhiteSpace(request.Platform))
        {
            errors["platform"] = ["Required."];
        }
        else if (!DevicePlatforms.All.Contains(request.Platform.Trim().ToLowerInvariant()))
        {
            errors["platform"] = [$"One of: {string.Join(", ", DevicePlatforms.All)}."];
        }

        if (!DeviceToken.IsValidAppVersion(NullIfBlank(request.AppVersion)))
        {
            errors["appVersion"] = ["At most 32 characters: letters, digits, '.', '+', '-'."];
        }

        if (!DeviceToken.IsValidLocale(NullIfBlank(request.Locale)))
        {
            errors["locale"] = ["A language tag such as tr-TR."];
        }

        return errors;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DeviceResponse Map(DeviceToken d) => new(d.Id, d.Platform, d.AppVersion, d.Locale, d.CreatedAt, d.LastSeenAt);
}

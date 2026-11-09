namespace BayiOS.Modules.Notifications.Contracts;

/// <param name="Token">FCM registration token of this app install (sensitive: never echoed back or logged).</param>
/// <param name="Platform"><c>android</c> or <c>ios</c>.</param>
/// <param name="AppVersion">App version, e.g. <c>0.1.0+1</c> (optional, ≤ 32).</param>
/// <param name="Locale">Device locale, e.g. <c>tr-TR</c> (optional, ≤ 35).</param>
public sealed record RegisterDeviceRequest(string? Token, string? Platform, string? AppVersion, string? Locale);

/// <summary>A push registration of the caller in the active tenant. The token itself is never returned.</summary>
public sealed record DeviceResponse(
    Guid Id,
    string Platform,
    string? AppVersion,
    string? Locale,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt);

public sealed record DeviceListResponse(IReadOnlyList<DeviceResponse> Items);

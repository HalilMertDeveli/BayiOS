using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BayiOS.SharedKernel.Tenancy;

namespace BayiOS.Modules.Notifications.Domain;

/// <summary>Platforms a device token may come from (the web has no push yet).</summary>
internal static class DevicePlatforms
{
    public const string Android = "android";
    public const string Ios = "ios";

    public static readonly IReadOnlyList<string> All = [Android, Ios];
}

/// <summary>
/// One push registration (FCM registration token) of one user in one tenant (data-model §5.11). The token is a
/// credential for sending to that device: it is stored because FCM needs it, never returned by the API and never
/// logged; <see cref="TokenSha256"/> is the global uniqueness key (one token = one tenant + user at a time).
/// </summary>
internal sealed partial class DeviceToken : ITenantOwned
{
    public const int MaxTokenLength = 4096;

    private DeviceToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Platform { get; private set; } = string.Empty;

    public string Token { get; private set; } = string.Empty;

    public byte[] TokenSha256 { get; private set; } = [];

    public string? AppVersion { get; private set; }

    public string? Locale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public static DeviceToken Register(
        Guid tenantId, Guid userId, string platform, string token, string? appVersion, string? locale, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Platform = platform,
            Token = token,
            TokenSha256 = Hash(token),
            AppVersion = appVersion,
            Locale = locale,
            CreatedAt = now,
            LastSeenAt = now,
        };

    /// <summary>The app registered the same token again (start, sign-in, token refresh): keep it fresh.</summary>
    public void Refresh(string platform, string? appVersion, string? locale, DateTimeOffset now)
    {
        Platform = platform;
        AppVersion = appVersion;
        Locale = locale;
        LastSeenAt = now;
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>FCM registration tokens are URL-safe base64 with ':' separators; anything else is refused.</summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: >= 32 and <= MaxTokenLength } && TokenPattern().IsMatch(token);

    public static bool IsValidAppVersion(string? value) => value is null || (value.Length <= 32 && AppVersionPattern().IsMatch(value));

    public static bool IsValidLocale(string? value) => value is null || (value.Length <= 35 && LocalePattern().IsMatch(value));

    [GeneratedRegex("^[A-Za-z0-9_:.-]+$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[0-9A-Za-z.+-]+$")]
    private static partial Regex AppVersionPattern();

    [GeneratedRegex("^[A-Za-z]{2,3}([_-][A-Za-z0-9]{2,8}){0,3}$")]
    private static partial Regex LocalePattern();
}

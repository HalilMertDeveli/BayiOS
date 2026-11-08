using System.ComponentModel.DataAnnotations;

namespace BayiOS.Modules.Notifications.Application;

/// <summary>
/// <c>Push:*</c> configuration (docs/operations/push-notifications.md). Off by default: with <c>Enabled=false</c> the
/// API still accepts device registrations and runs the send jobs, but the sender is a no-op. Turning it on needs a
/// Firebase project and its service-account key (an owner action).
/// </summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>
    /// Secret: the Firebase service-account JSON key (from the secret store as <c>Push__ServiceAccountJson</c>; never in
    /// a file in the repository, never logged). Read with <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
    /// </summary>
    public const string ServiceAccountJsonKey = "Push:ServiceAccountJson";

    /// <summary>
    /// Alternative to <see cref="ServiceAccountJsonKey"/> for local development: path to the key file outside the
    /// repository (ignored patterns: <c>*firebase-adminsdk*.json</c>, <c>service-account*.json</c>).
    /// </summary>
    public string? ServiceAccountFile { get; set; }

    /// <summary>Send through FCM. False → <c>DisabledPushSender</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Firebase project id; defaults to <c>project_id</c> of the service-account key.</summary>
    [RegularExpression("^[a-z][a-z0-9-]{4,29}$")]
    public string? ProjectId { get; set; }

    /// <summary>A registration not refreshed for this long is deleted (data-policy §4: 90 days; the app re-registers on every start).</summary>
    [Range(30, 270)]
    public int StaleAfterDays { get; set; } = 90;

    /// <summary>Most registrations one user keeps per tenant; the least recently seen ones are dropped.</summary>
    [Range(1, 50)]
    public int MaxDevicesPerUser { get; set; } = 10;

    /// <summary>Timeout of one FCM / Google OAuth call.</summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;
}

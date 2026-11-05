using System.ComponentModel.DataAnnotations;

namespace BayiOS.Modules.Billing.Application;

/// <summary><c>Billing:*</c> configuration (PRD SUB-05: "30 gün (ayarlanabilir)", AQ-08).</summary>
public sealed class BillingOptions
{
    public const string Section = "Billing";

    /// <summary>Length of the plan trial a new tenant gets (all modules open).</summary>
    [Range(1, 365)]
    public int TrialDays { get; set; } = 30;

    /// <summary>Length of the free activation of one add-on from the module store (no payment in this phase).</summary>
    [Range(1, 365)]
    public int ModuleTrialDays { get; set; } = 30;
}

internal static class BillingClock
{
    /// <summary>Microsecond precision: what PostgreSQL <c>timestamptz</c> stores (responses equal what is read back).</summary>
    public static DateTimeOffset Now(this TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }
}

internal sealed record AppError(int Status, string Code, string Title)
{
    public static AppError NotFound() => new(404, "resource.not_found", "Resource not found");
}

internal sealed record AppResult<T>(T? Value, AppError? Error)
{
    public static implicit operator AppResult<T>(T value) => new(value, null);

    public static implicit operator AppResult<T>(AppError error) => new(default, error);
}

/// <summary>Problem codes of the module store (api-strategy §6).</summary>
internal static class BillingProblems
{
    /// <summary>409: the module is announced but not built yet.</summary>
    public const string ModuleUnavailable = "billing.module_unavailable";

    /// <summary>409: the module is already on.</summary>
    public const string ModuleAlreadyActive = "billing.module_already_active";

    /// <summary>409: the free activation of this module was used up; paid activation does not exist yet.</summary>
    public const string TrialUsed = "billing.trial_used";

    /// <summary>409: nothing to switch off (not activated, or only included in the tenant trial).</summary>
    public const string ModuleNotActive = "billing.module_not_active";
}

/// <summary>Audit action codes of the Billing module (threat-model §7).</summary>
internal static class BillingAuditActions
{
    public const string TrialStarted = "billing.trial_started";
    public const string TrialExpired = "billing.trial_expired";
    public const string ModuleActivated = "billing.module_activated";
    public const string ModuleDeactivated = "billing.module_deactivated";
    public const string ModuleTrialExpired = "billing.module_trial_expired";
}

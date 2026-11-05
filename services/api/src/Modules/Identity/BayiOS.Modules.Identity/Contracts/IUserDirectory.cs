namespace BayiOS.Modules.Identity.Contracts;

/// <summary>
/// Display names of users for other modules (order history, task assignees). Batch only: callers collect every user id
/// of a response and resolve them in one query (no N+1). Runs on the request connection and transaction; only the caller
/// and users who hold (or held) a membership in the current tenant resolve — anyone else is absent from the result.
/// </summary>
public interface IUserDirectory
{
    /// <summary>User id → display name (<see cref="UserDisplayName.From"/>); users without a usable name are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

/// <summary>
/// The single display-name rule: the profile's full name (trimmed) when set; otherwise the local part of the e-mail
/// (before <c>@</c>), because sign-up leaves <c>full_name</c> empty until the user completes a profile; otherwise null.
/// </summary>
public static class UserDisplayName
{
    public static string? From(string? fullName, string? email)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName.Trim();
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        var local = at < 0 ? trimmed : trimmed[..at];
        return local.Length == 0 ? null : local;
    }
}

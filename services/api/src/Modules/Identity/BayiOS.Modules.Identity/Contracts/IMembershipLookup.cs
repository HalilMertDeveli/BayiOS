namespace BayiOS.Modules.Identity.Contracts;

/// <summary>Resolves a caller's tenant access. Used by the host to validate <c>X-Tenant-Id</c>.</summary>
public interface IMembershipLookup
{
    /// <summary>The role of <paramref name="userId"/>'s <b>active</b> membership in the tenant, or null.</summary>
    Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}

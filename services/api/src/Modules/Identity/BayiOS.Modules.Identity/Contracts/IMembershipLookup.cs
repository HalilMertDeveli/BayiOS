namespace BayiOS.Modules.Identity.Contracts;

/// <summary>The caller's access to one tenant: the active membership's role and the tenant's own status.</summary>
/// <param name="TenantStatus">active, suspended or closed (data-model, identity.tenants).</param>
public sealed record TenantAccess(string Role, string TenantStatus)
{
    /// <summary>Only an <c>active</c> tenant is usable; suspended and closed tenants are denied (fail-closed).</summary>
    public bool TenantIsActive => TenantStatus == "active";
}

/// <summary>Resolves a caller's tenant access. Used by the host to validate <c>X-Tenant-Id</c>.</summary>
public interface IMembershipLookup
{
    /// <summary>The role of <paramref name="userId"/>'s <b>active</b> membership in the tenant, or null.</summary>
    Task<string?> FindActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The active membership's role plus the tenant status, or null without an active membership.</summary>
    Task<TenantAccess?> FindAccessAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}

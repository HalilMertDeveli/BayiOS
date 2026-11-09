namespace BayiOS.SharedKernel.Tenancy;

/// <summary>
/// Who is calling and in which tenant. Populated only by the host from the validated JWT (<c>sub</c>) and the
/// caller's active membership for <c>X-Tenant-Id</c> — never from request bodies. Background jobs get a context with
/// the job's tenant and no user.
/// </summary>
public interface IRequestContext
{
    Guid? UserId { get; }

    Guid? TenantId { get; }

    /// <summary>The caller's role in <see cref="TenantId"/>; null when no tenant is resolved.</summary>
    string? Role { get; }

    /// <summary>Server-formatted correlation id of the request or job (never raw caller text).</summary>
    string? CorrelationId { get; }
}

/// <summary>Scoped, request-local implementation of <see cref="IRequestContext"/>.</summary>
public sealed class RequestContext : IRequestContext
{
    public Guid? UserId { get; set; }

    public Guid? TenantId { get; set; }

    public string? Role { get; set; }

    public string? CorrelationId { get; set; }
}

/// <summary>Marks an entity whose rows belong to exactly one tenant (column <c>tenant_id</c>).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

using System.Buffers.Text;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BayiOS.Modules.Identity.Application;

/// <param name="Roles">Empty = every role.</param>
/// <param name="Statuses">Empty = every status.</param>
/// <param name="After">Membership id of the last row of the previous page.</param>
internal sealed record MemberListQuery(IReadOnlyList<string> Roles, IReadOnlyList<string> Statuses, int Limit, Guid? After);

/// <summary>
/// Members of the active tenant (personnel picker, team screen). Keyset on the membership id (UUIDv7 ≈ the order in
/// which people joined). The tenant comes from the request context only; RLS and the query filter sit underneath, and
/// the explicit tenant predicate keeps the caller's own memberships in other tenants out.
/// </summary>
internal sealed class MemberQueries(IdentityDbContext db, IRequestContext requestContext)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<MemberPageResponse> ListAsync(MemberListQuery query, CancellationToken cancellationToken)
    {
        var tenantId = requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");
        var memberships = db.Memberships.AsNoTracking().Where(m => m.TenantId == tenantId);
        if (query.Roles.Count > 0)
        {
            memberships = memberships.Where(m => query.Roles.Contains(m.Role));
        }

        if (query.Statuses.Count > 0)
        {
            memberships = memberships.Where(m => query.Statuses.Contains(m.Status));
        }

        if (query.After is { } after)
        {
            memberships = memberships.Where(m => m.Id.CompareTo(after) > 0);
        }

        var rows = await (from m in memberships
                          join p in db.UserProfiles.AsNoTracking() on m.UserId equals p.UserId into profiles
                          from p in profiles.DefaultIfEmpty()
                          orderby m.Id
                          select new { m.Id, m.UserId, m.Role, m.Status, m.CreatedAt, FullName = p == null ? null : p.FullName, Email = p == null ? null : p.Email })
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).ToList();
        var items = page.Select(r => new MemberResponse(
                r.UserId,
                UserDisplayName.From(r.FullName, r.Email),
                string.IsNullOrWhiteSpace(r.FullName) ? null : r.FullName.Trim(),
                r.Email,
                r.Role,
                r.Status,
                r.CreatedAt))
            .ToList();
        var next = hasMore ? MemberCursor.Encode(page[^1].Id) : null;
        return new MemberPageResponse(items, new MemberPageInfo(next, hasMore, query.Limit));
    }
}

/// <summary>Opaque keyset cursor: base64url of the last membership id.</summary>
internal static class MemberCursor
{
    public static string Encode(Guid id) => Base64Url.EncodeToString(id.ToByteArray(bigEndian: true));

    public static Guid? TryDecode(string value)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(value);
            return bytes.Length == 16 ? new Guid(bytes, bigEndian: true) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Identity side of <see cref="IUserDirectory"/>: one query for the whole batch.</summary>
internal sealed class UserDirectory(IdentityDbContext db, IRequestContext requestContext) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0 || requestContext.TenantId is not { } tenantId)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = userIds.Distinct().ToList();
        var profiles = await db.UserProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.UserId) && db.Memberships.Any(m => m.UserId == p.UserId && m.TenantId == tenantId))
            .Select(p => new { p.UserId, p.FullName, p.Email })
            .ToListAsync(cancellationToken);
        var names = new Dictionary<Guid, string>();
        foreach (var profile in profiles)
        {
            if (UserDisplayName.From(profile.FullName, profile.Email) is { } name)
            {
                names[profile.UserId] = name;
            }
        }

        return names;
    }
}

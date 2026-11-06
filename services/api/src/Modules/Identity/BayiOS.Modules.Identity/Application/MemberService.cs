using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Domain;
using BayiOS.Modules.Identity.Infrastructure.Persistence;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BayiOS.Modules.Identity.Application;

/// <summary>Invitation settings (section <c>Invitations</c>).</summary>
public sealed class InvitationOptions
{
    /// <summary>Days an invitation token stays valid (PRD TEN-07: 7). 1–30.</summary>
    public int LifetimeDays { get; set; } = 7;

    /// <summary>
    /// Accept Supabase's <c>user_metadata.email_verified</c> as proof of a verified address (see <see cref="EmailVerification"/>).
    /// Set to false once a custom access-token hook puts a server-asserted top-level <c>email_verified</c> in the token.
    /// </summary>
    public bool TrustUserMetadataEmailVerified { get; set; } = true;
}

/// <summary>What the validated access token says about the caller's e-mail address (claims only, never the body).</summary>
internal sealed record InviteeClaims(string? Email, bool? EmailVerified, bool? UserMetadataEmailVerified, bool IsAnonymous);

/// <summary>
/// Team management (#129, roles-permissions §3.1, user-flows AF-09): invitations without e-mail infrastructure, role
/// changes, disabling and ownership transfer. Every change in a tenant runs under one per-tenant transaction lock, so
/// seat counting (<c>max_users</c> = active members + open invitations) and the "at least one active owner" rule cannot
/// be raced. Tenant and caller come from the request context only.
/// </summary>
internal sealed class MemberService(
    IdentityDbContext db,
    IRequestContext requestContext,
    UnitOfWork unitOfWork,
    IAuditLog audit,
    IAuthorizationContext authorization,
    IEntitlements entitlements,
    IOptions<InvitationOptions> options,
    TimeProvider clock)
{
    public const int MaxListedInvitations = 200;

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    private TimeSpan Lifetime => TimeSpan.FromDays(Math.Clamp(options.Value.LifetimeDays, 1, 30));

    // ---- Invitations ----

    public async Task<InvitationListResponse> ListInvitationsAsync(IReadOnlyList<string> statuses, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var query = db.Invitations.AsNoTracking();
        if (statuses.Count > 0)
        {
            var stored = statuses.Where(s => s != InvitationStatuses.Expired && s != InvitationStatuses.Pending).ToList();
            var pending = statuses.Contains(InvitationStatuses.Pending);
            var expired = statuses.Contains(InvitationStatuses.Expired);
            query = query.Where(i => stored.Contains(i.Status)
                || (pending && i.Status == InvitationStatuses.Pending && i.ExpiresAt > now)
                || (expired && (i.Status == InvitationStatuses.Expired || (i.Status == InvitationStatuses.Pending && i.ExpiresAt <= now))));
        }

        var rows = await query.OrderByDescending(i => i.Id).Take(MaxListedInvitations).ToListAsync(cancellationToken);
        return new InvitationListResponse([.. rows.Select(i => ToResponse(i, now))]);
    }

    public async Task<AppResult<IssuedInvitationResponse>> InviteAsync(CreateInvitationRequest request, CancellationToken cancellationToken)
    {
        var email = EmailVerification.Normalize(request.Email);
        var role = request.Role?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (email is null)
        {
            errors["email"] = ["Must be a single e-mail address (at most 254 characters)."];
        }

        if (role is null || !MemberRoleRules.Grantable.Contains(role))
        {
            errors["role"] = [$"Must be one of: {string.Join(", ", MemberRoleRules.Grantable)}."];
        }

        if (errors.Count > 0)
        {
            return Validation(errors);
        }

        if (!MemberRoleRules.CanGrant(requestContext.Role, role!))
        {
            return RoleNotGrantable(role!);
        }

        await LockMembersAsync(cancellationToken);
        var tenantId = TenantId;
        var now = clock.GetUtcNow();

        // Profile e-mails are stored as Supabase reports them; compare in SQL with lower() (not a .NET culture call).
#pragma warning disable CA1304, CA1311, CA1862
        var alreadyMember = await (
                from m in db.Memberships
                join p in db.UserProfiles on m.UserId equals p.UserId
                where m.TenantId == tenantId && p.Email != null && p.Email.Trim().ToLower() == email
                select m.Id)
            .AnyAsync(cancellationToken);
#pragma warning restore CA1304, CA1311, CA1862
        if (alreadyMember)
        {
            return new AppError(409, "members.already_member", "This e-mail address already belongs to a member of the tenant");
        }

        var pending = await db.Invitations.SingleOrDefaultAsync(i => i.Email == email && i.Status == InvitationStatuses.Pending, cancellationToken);
        if (pending is not null)
        {
            if (pending.IsOpen(now))
            {
                return AlreadyPending(pending.Id);
            }

            // Past its expiry: close it so the new invitation can take the address.
            pending.Expire(UserId, now);
            await db.SaveChangesAsync(cancellationToken);
        }

        await entitlements.EnsureCapacityAsync(EntitlementCodes.MaxUsers, await SeatsAsync(null, now, cancellationToken), cancellationToken);

        var (token, hash) = InvitationTokens.Generate();
        var invitation = Invitation.Create(tenantId, email!, role!, hash, now + Lifetime, UserId, now);
        db.Invitations.Add(invitation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdentityDbContext.PendingInvitationIndex })
        {
            return new AppError(409, "invitation.already_pending", "A pending invitation for this e-mail address already exists");
        }

        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberInvited, "invitation", invitation.Id, new AuditChanges()
                .Value("role", invitation.Role)
                .Value("expiresAt", invitation.ExpiresAt)
                .ToDictionary()),
            cancellationToken);
        return new IssuedInvitationResponse(ToResponse(invitation, now), token);
    }

    /// <summary>A new token and a fresh expiry for a pending (possibly expired) invitation; the previous token stops working.</summary>
    public async Task<AppResult<IssuedInvitationResponse>> ResendAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        await LockMembersAsync(cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId, cancellationToken);
        if (invitation is null)
        {
            return InvitationNotFound();
        }

        if (!MemberRoleRules.CanGrant(requestContext.Role, invitation.Role))
        {
            return RoleNotGrantable(invitation.Role);
        }

        if (invitation.Status != InvitationStatuses.Pending)
        {
            return NotPending(invitation);
        }

        var now = clock.GetUtcNow();
        await entitlements.EnsureCapacityAsync(EntitlementCodes.MaxUsers, await SeatsAsync(invitation.Id, now, cancellationToken), cancellationToken);

        var (token, hash) = InvitationTokens.Generate();
        invitation.Rotate(hash, now + Lifetime, UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberInviteResent, "invitation", invitation.Id, new AuditChanges()
                .Value("role", invitation.Role)
                .Value("tokenCount", invitation.TokenCount)
                .Value("expiresAt", invitation.ExpiresAt)
                .ToDictionary()),
            cancellationToken);
        return new IssuedInvitationResponse(ToResponse(invitation, now), token);
    }

    /// <summary>Withdraws a pending invitation (also one past its expiry); its token stops working and the seat is freed.</summary>
    public async Task<AppResult<InvitationResponse>> RevokeAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        await LockMembersAsync(cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId, cancellationToken);
        if (invitation is null)
        {
            return InvitationNotFound();
        }

        if (!MemberRoleRules.CanGrant(requestContext.Role, invitation.Role))
        {
            return RoleNotGrantable(invitation.Role);
        }

        if (invitation.Status != InvitationStatuses.Pending)
        {
            return NotPending(invitation);
        }

        var now = clock.GetUtcNow();
        invitation.Revoke(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberInviteRevoked, "invitation", invitation.Id, new AuditChanges()
                .Value("role", invitation.Role)
                .ToDictionary()),
            cancellationToken);
        return ToResponse(invitation, now);
    }

    /// <summary>
    /// The invitee accepts (tenant-less request). The token names the tenant through
    /// <c>identity.find_invitation_tenant</c> (SECURITY DEFINER: the caller is no member yet, so RLS hides the row); the
    /// rest runs inside that tenant's RLS context. Accepted only when the token's e-mail equals the invited address and
    /// is verified; the membership is created active with the invited role.
    /// </summary>
    public async Task<AppResult<AcceptInvitationResponse>> AcceptAsync(AcceptInvitationRequest request, InviteeClaims claims, CancellationToken cancellationToken)
    {
        if (InvitationTokens.TryHash(request.Token) is not { } hash)
        {
            return InvitationNotFound();
        }

        Guid? tenantId;
        await using (var command = new NpgsqlCommand("SELECT identity.find_invitation_tenant(@hash)", unitOfWork.Connection, unitOfWork.Transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<byte[]>("hash", hash));
            tenantId = await command.ExecuteScalarAsync(cancellationToken) is Guid found ? found : null;
        }

        if (tenantId is not { } invitedTenant)
        {
            return InvitationNotFound();
        }

        await unitOfWork.SetTenantAsync(invitedTenant, cancellationToken);
        await LockMembersAsync(cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        if (invitation is not { Status: InvitationStatuses.Pending })
        {
            return InvitationNotFound();
        }

        var email = EmailVerification.Normalize(claims.Email);
        if (email is null || email != invitation.Email)
        {
            return new AppError(403, "invitation.email_mismatch", "This invitation was sent to another e-mail address; sign in with the invited address");
        }

        if (!EmailVerification.IsVerified(claims.EmailVerified, claims.UserMetadataEmailVerified, claims.IsAnonymous, options.Value.TrustUserMetadataEmailVerified))
        {
            return new AppError(403, "invitation.email_not_verified", "Confirm your e-mail address before accepting the invitation");
        }

        var now = clock.GetUtcNow();
        if (!invitation.IsOpen(now))
        {
            return new AppError(410, "invitation.expired", "The invitation has expired; ask for a new one");
        }

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == invitedTenant, cancellationToken);
        if (tenant.Status != TenantStatuses.Active)
        {
            return new AppError(403, "tenant.inactive", "This tenant is not active");
        }

        var userId = UserId;
        if (await db.Memberships.AnyAsync(m => m.TenantId == invitedTenant && m.UserId == userId, cancellationToken))
        {
            return new AppError(409, "invitation.already_member", "You already have a membership in this tenant");
        }

        // The invitation held a seat; accepting converts it (a downgrade since then may still leave no room).
        await entitlements.EnsureCapacityAsync(EntitlementCodes.MaxUsers, await SeatsAsync(invitation.Id, now, cancellationToken), cancellationToken);

        var membership = Membership.Join(invitedTenant, userId, invitation.Role, invitation.CreatedBy, now);
        db.Memberships.Add(membership);
        if (!await db.UserProfiles.AnyAsync(p => p.UserId == userId, cancellationToken))
        {
            db.UserProfiles.Add(UserProfile.Create(userId, email, now));
        }

        invitation.Accept(userId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberJoined, "membership", membership.Id, new AuditChanges()
                .Value("userId", userId)
                .Value("role", membership.Role)
                .Value("invitationId", invitation.Id)
                .ToDictionary()),
            cancellationToken);
        return new AcceptInvitationResponse(tenant.Id, tenant.Name, tenant.Slug, membership.Role, membership.Status);
    }

    // ---- Members ----

    public async Task<AppResult<MemberResponse>> ChangeRoleAsync(Guid userId, ChangeMemberRoleRequest request, CancellationToken cancellationToken)
    {
        var role = request.Role?.Trim();
        if (role is null || !MemberRoleRules.Grantable.Contains(role))
        {
            // Checked first, but the record-level grant is still evaluated below on every other path.
            await authorization.ScopeAsync(Permissions.MembersManage, cancellationToken);
            return Validation(new Dictionary<string, string[]>
            {
                ["role"] = [$"Must be one of: {string.Join(", ", MemberRoleRules.Grantable)} (owner moves by ownership transfer)."],
            });
        }

        var (membership, error) = await ManageableAsync(userId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!MemberRoleRules.CanGrant(requestContext.Role, role))
        {
            return RoleNotGrantable(role);
        }

        if (membership!.Role == role)
        {
            return await ToMemberAsync(membership, cancellationToken);
        }

        if (membership.Role == Roles.Owner && membership.IsActive && !await AnotherActiveOwnerAsync(membership.UserId, cancellationToken))
        {
            return LastOwner();
        }

        var before = membership.Role;
        membership.ChangeRole(role, UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberRoleChanged, "membership", membership.Id, new AuditChanges()
                .Value("userId", membership.UserId)
                .Field("role", before, role)
                .ToDictionary()),
            cancellationToken);
        return await ToMemberAsync(membership, cancellationToken);
    }

    /// <summary>The member loses access at once: tenant resolution only admits active memberships, on every request.</summary>
    public async Task<AppResult<MemberResponse>> DisableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (membership, error) = await ManageableAsync(userId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (membership!.UserId == UserId)
        {
            return new AppError(409, "members.cannot_disable_self", "You cannot disable your own membership");
        }

        if (membership.Status == MembershipStatuses.Disabled)
        {
            return await ToMemberAsync(membership, cancellationToken);
        }

        if (membership.Role == Roles.Owner && membership.IsActive && !await AnotherActiveOwnerAsync(membership.UserId, cancellationToken))
        {
            return LastOwner();
        }

        var before = membership.Status;
        membership.Disable(UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberDisabled, "membership", membership.Id, new AuditChanges()
                .Value("userId", membership.UserId)
                .Value("role", membership.Role)
                .Field("status", before, membership.Status)
                .ToDictionary()),
            cancellationToken);
        return await ToMemberAsync(membership, cancellationToken);
    }

    public async Task<AppResult<MemberResponse>> EnableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (membership, error) = await ManageableAsync(userId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (membership!.IsActive)
        {
            return await ToMemberAsync(membership, cancellationToken);
        }

        var now = clock.GetUtcNow();
        await entitlements.EnsureCapacityAsync(EntitlementCodes.MaxUsers, await SeatsAsync(null, now, cancellationToken), cancellationToken);

        var before = membership.Status;
        membership.Enable(UserId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberEnabled, "membership", membership.Id, new AuditChanges()
                .Value("userId", membership.UserId)
                .Value("role", membership.Role)
                .Field("status", before, membership.Status)
                .ToDictionary()),
            cancellationToken);
        return await ToMemberAsync(membership, cancellationToken);
    }

    /// <summary>
    /// Ownership transfer (<c>ownership.transfer</c>, owner only): an active member becomes owner and the caller becomes
    /// admin, atomically — the tenant always keeps an active owner.
    /// </summary>
    public async Task<AppResult<OwnershipTransferResponse>> TransferOwnershipAsync(TransferOwnershipRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId is not { } targetId)
        {
            return Validation(new Dictionary<string, string[]> { ["userId"] = ["Required."] });
        }

        await LockMembersAsync(cancellationToken);
        var tenantId = TenantId;
        var callerId = UserId;
        if (targetId == callerId)
        {
            return new AppError(409, "members.transfer_to_self", "You already own this tenant");
        }

        var target = await db.Memberships.SingleOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == targetId, cancellationToken);
        if (target is null)
        {
            return MemberNotFound();
        }

        if (!target.IsActive)
        {
            return new AppError(409, "members.not_active", "Only an active member can become the owner");
        }

        if (target.Role == Roles.Owner)
        {
            return new AppError(409, "members.already_owner", "This member is already an owner");
        }

        var caller = await db.Memberships.SingleAsync(m => m.TenantId == tenantId && m.UserId == callerId, cancellationToken);
        var now = clock.GetUtcNow();
        var targetBefore = target.Role;
        target.ChangeRole(Roles.Owner, callerId, now);
        caller.ChangeRole(Roles.Admin, callerId, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.MemberOwnershipTransferred, "membership", target.Id, new AuditChanges()
                .Value("fromUserId", callerId)
                .Value("toUserId", targetId)
                .Value("toPreviousRole", targetBefore)
                .Value("fromNewRole", Roles.Admin)
                .ToDictionary()),
            cancellationToken);
        return new OwnershipTransferResponse(await ToMemberAsync(target, cancellationToken), await ToMemberAsync(caller, cancellationToken));
    }

    // ---- Helpers ----

    /// <summary>
    /// The target membership if the caller may manage it: an owner manages everyone; an admin (footnote 1, K grant
    /// evaluated here record by record) only manager and lower roles. Takes the per-tenant member lock first.
    /// </summary>
    private async Task<(Membership? Membership, AppError? Error)> ManageableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var scope = await authorization.ScopeAsync(Permissions.MembersManage, cancellationToken);
        await LockMembersAsync(cancellationToken);
        var tenantId = TenantId;
        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return (null, MemberNotFound());
        }

        var allowed = scope == PermissionScope.All || (scope == PermissionScope.Restricted && MemberRoleRules.IsBelowAdmin(membership.Role));
        return allowed
            ? (membership, null)
            : (null, new AppError(403, "members.not_manageable", "You cannot manage a member with this role"));
    }

    /// <summary>Serializes member changes of the tenant (seat count, last-owner rule) until the transaction ends.</summary>
    private async Task LockMembersAsync(CancellationToken cancellationToken)
    {
        await db.JoinTransactionAsync(cancellationToken);
        var tenant = TenantId.ToString();
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('identity.members:' || {tenant}, 0))", cancellationToken);
    }

    /// <summary><c>max_users</c> usage: active members plus open (pending, unexpired) invitations.</summary>
    private async Task<long> SeatsAsync(Guid? exceptInvitation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var active = await db.Memberships.CountAsync(m => m.TenantId == tenantId && m.Status == MembershipStatuses.Active, cancellationToken);
        var open = await db.Invitations.CountAsync(
            i => i.Status == InvitationStatuses.Pending && i.ExpiresAt > now && i.Id != exceptInvitation, cancellationToken);
        return active + open;
    }

    private Task<bool> AnotherActiveOwnerAsync(Guid exceptUserId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        return db.Memberships.AnyAsync(
            m => m.TenantId == tenantId && m.UserId != exceptUserId && m.Role == Roles.Owner && m.Status == MembershipStatuses.Active,
            cancellationToken);
    }

    private async Task<MemberResponse> ToMemberAsync(Membership membership, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == membership.UserId)
            .Select(p => new { p.FullName, p.Email })
            .SingleOrDefaultAsync(cancellationToken);
        return new MemberResponse(
            membership.UserId,
            UserDisplayName.From(profile?.FullName, profile?.Email),
            string.IsNullOrWhiteSpace(profile?.FullName) ? null : profile.FullName.Trim(),
            profile?.Email,
            membership.Role,
            membership.Status,
            membership.CreatedAt);
    }

    private static InvitationResponse ToResponse(Invitation i, DateTimeOffset now) => new(
        i.Id, i.Email, i.Role, i.EffectiveStatus(now), i.ExpiresAt, i.TokenCount, i.CreatedAt, i.CreatedBy, i.AcceptedAt, i.AcceptedBy, i.RevokedAt);

    private static AppError Validation(IDictionary<string, string[]> errors) => new(422, "validation.failed", "Validation failed", errors);

    private static AppError RoleNotGrantable(string role) =>
        new(403, "members.role_not_grantable", "You cannot grant this role", Extensions: new Dictionary<string, object?> { ["role"] = role });

    private static AppError AlreadyPending(Guid invitationId) =>
        new(409, "invitation.already_pending", "A pending invitation for this e-mail address already exists; resend or revoke it",
            Extensions: new Dictionary<string, object?> { ["invitationId"] = invitationId });

    private static AppError NotPending(Invitation invitation) =>
        new(409, "invitation.not_pending", "The invitation is no longer pending",
            Extensions: new Dictionary<string, object?> { ["status"] = invitation.Status });

    private static AppError InvitationNotFound() => new(404, "invitation.not_found", "Invitation not found or no longer valid");

    private static AppError MemberNotFound() => new(404, "resource.not_found", "Resource not found");

    private static AppError LastOwner() =>
        new(409, "members.last_owner", "The tenant must keep at least one active owner; transfer ownership first");
}

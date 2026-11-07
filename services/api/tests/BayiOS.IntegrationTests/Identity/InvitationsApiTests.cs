using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BayiOS.IntegrationTests.Billing;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Billing.Contracts;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Platform.Audit;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>
/// #129: member invitations without e-mail infrastructure. The inviter gets a token once; the invitee accepts it only
/// while signed in with the same, verified address (claims of the validated token, never the body). Synthetic data only.
/// </summary>
public sealed class InvitationsApiTests(ApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Invite_then_accept_with_the_same_verified_email_creates_an_active_membership()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("davetli");
        var issued = await MembersKit.InviteOkAsync(owner, "  " + email.ToUpperInvariant() + " ", Roles.Sales);

        Assert.Equal(43, issued.Token.Length);
        Assert.Equal((email, Roles.Sales, "pending", 1), (issued.Invitation.Email, issued.Invitation.Role, issued.Invitation.Status, issued.Invitation.TokenCount));
        Assert.InRange(issued.Invitation.ExpiresAt - issued.Invitation.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        // Only the SHA-256 is stored; the token never appears in a list or in the database.
        Assert.Equal(
            SHA256.HashData(Encoding.ASCII.GetBytes(issued.Token)),
            await BillingKit.ScalarAsync<byte[]>(fixture, "SELECT token_hash FROM identity.invitations WHERE id = $1", issued.Invitation.Id));
        var listed = await owner.GetAsync("/api/v1/members/invitations?status=pending");
        var listedText = await listed.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.DoesNotContain(issued.Token, listedText, StringComparison.Ordinal);
        Assert.Equal(issued.Invitation.Id, Assert.Single((await owner.OkAsync<InvitationListResponse>(listed)).Items).Id);
        Assert.Equal(0L, await BillingKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM platform.idempotency_keys WHERE position(convert_to($1, 'UTF8') IN response_body) > 0", issued.Token));

        // Before accepting, the invitee has no access to the tenant.
        var userId = Guid.NewGuid();
        var invitee = MembersKit.Invitee(fixture, userId, email.ToUpperInvariant());
        var early = new CatalogClient(invitee, owner.TenantId, userId);
        await HttpAssert.ProblemAsync(await early.GetAsync("/api/v1/tenant"), HttpStatusCode.Forbidden, "tenant.access_denied");

        var accepted = await MembersKit.AcceptAsync(invitee, issued.Token);
        var joined = await early.OkAsync<AcceptInvitationResponse>(accepted);
        Assert.Equal((owner.TenantId, Roles.Sales, "active"), (joined.TenantId, joined.Role, joined.Status));

        // Now a member: the tenant resolves, /me lists the membership with the invited role.
        Assert.Equal(HttpStatusCode.OK, (await early.GetAsync("/api/v1/tenant")).StatusCode);
        var me = await early.OkAsync<MeResponse>(await invitee.GetAsync("/api/v1/me", Ct));
        var membership = Assert.Single(me.Memberships);
        Assert.Equal((owner.TenantId, Roles.Sales, "active"), (membership.TenantId, membership.Role, membership.Status));
        Assert.Equal(email, me.Email);

        var members = await owner.OkAsync<MemberPageResponse>(await owner.GetAsync("/api/v1/members"));
        Assert.Equal(email, members.Items.Single(m => m.UserId == userId).Email);
        var closed = Assert.Single((await owner.OkAsync<InvitationListResponse>(await owner.GetAsync("/api/v1/members/invitations"))).Items);
        Assert.Equal(("accepted", userId), (closed.Status, closed.AcceptedBy));

        // Audit: invited + joined, without the address or the token.
        var invited = Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberInvited));
        Assert.Equal((issued.Invitation.Id, owner.UserId, "invitation"), (invited.EntityId!.Value, invited.ActorUserId!.Value, invited.EntityType));
        var joinedEvent = Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberJoined));
        Assert.Equal((userId, "membership"), (joinedEvent.ActorUserId!.Value, joinedEvent.EntityType));
        foreach (var e in new[] { invited, joinedEvent })
        {
            var json = MembersKit.ChangesJson(e);
            Assert.DoesNotContain("example.test", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(issued.Token, json, StringComparison.Ordinal);
        }

        Assert.Contains("\"role\":\"sales\"", MembersKit.ChangesJson(joinedEvent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_used_token_cannot_be_replayed_by_anyone()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("replay");
        var issued = await MembersKit.InviteOkAsync(owner, email, Roles.Viewer);
        var userId = Guid.NewGuid();
        var invitee = MembersKit.Invitee(fixture, userId, email);
        var key = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(invitee, issued.Token, key)).StatusCode);

        // Same Idempotency-Key: the stored success is replayed (no second membership).
        var replayed = await MembersKit.AcceptAsync(invitee, issued.Token, key);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.True(replayed.Headers.Contains("Idempotent-Replayed"));

        // A new request with the used token: gone for the invitee and for another user with the same address.
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(invitee, issued.Token), HttpStatusCode.NotFound, "invitation.not_found");
        await HttpAssert.ProblemAsync(
            await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, Guid.NewGuid(), email), issued.Token), HttpStatusCode.NotFound, "invitation.not_found");
        Assert.Equal(1L, await BillingKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM identity.memberships WHERE tenant_id = $1 AND user_id = $2", owner.TenantId, userId));
    }

    [Fact]
    public async Task An_unverified_or_anonymous_session_cannot_accept()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("unverified");
        var issued = await MembersKit.InviteOkAsync(owner, email, Roles.Manager);
        var userId = Guid.NewGuid();

        foreach (var invitee in new[]
                 {
                     MembersKit.Invitee(fixture, userId, email, metadataEmailVerified: false),
                     MembersKit.Invitee(fixture, userId, email, metadataEmailVerified: null),
                     MembersKit.Invitee(fixture, userId, email, metadataEmailVerified: false, topLevelEmailVerified: false),
                 })
        {
            await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(invitee, issued.Token), HttpStatusCode.Forbidden, "invitation.email_not_verified");
        }

        // An anonymous session is not an identity at all (audit 2026-11 L-2): refused at authentication, before the
        // invitation rules (which still reject it too — EmailVerification.IsVerified, MemberRulesTests).
        var anonymous = MembersKit.Invitee(fixture, userId, email, metadataEmailVerified: true, isAnonymous: true);
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(anonymous, issued.Token), HttpStatusCode.Unauthorized, "auth.invalid_token");

        // The invitation is untouched; a server-asserted top-level email_verified is enough on its own.
        var verified = MembersKit.Invitee(fixture, userId, email, metadataEmailVerified: null, topLevelEmailVerified: true);
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(verified, issued.Token)).StatusCode);
    }

    [Fact]
    public async Task A_different_or_missing_email_cannot_accept_and_the_body_cannot_supply_one()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("right");
        var issued = await MembersKit.InviteOkAsync(owner, email, Roles.WarehouseStaff);

        var other = MembersKit.Invitee(fixture, Guid.NewGuid(), MembersKit.NewEmail("wrong"));
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(other, issued.Token), HttpStatusCode.Forbidden, "invitation.email_mismatch");
        var noEmail = MembersKit.Invitee(fixture, Guid.NewGuid(), null);
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(noEmail, issued.Token), HttpStatusCode.Forbidden, "invitation.email_mismatch");

        // An e-mail in the body is ignored (unknown members are not bound; the claims decide).
        using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invitations/accept")
        {
            Content = JsonContent.Create(new { token = issued.Token, email }),
        };
        forged.Headers.Add(IdempotentRequests.Header, Guid.NewGuid().ToString());
        await HttpAssert.ProblemAsync(await other.SendAsync(forged, Ct), HttpStatusCode.Forbidden, "invitation.email_mismatch");

        // Malformed and unknown tokens look the same as used ones.
        var right = MembersKit.Invitee(fixture, Guid.NewGuid(), email);
        foreach (var token in new[] { null, string.Empty, "short", new string('A', 43), issued.Token + "x", issued.Token.Replace(issued.Token[0], issued.Token[0] == 'A' ? 'B' : 'A') })
        {
            await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(right, token), HttpStatusCode.NotFound, "invitation.not_found");
        }

        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(right, issued.Token)).StatusCode);
    }

    [Fact]
    public async Task Expired_revoked_and_rotated_tokens_are_rejected()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);

        // Expired → 410; resend issues a new token with a fresh expiry, the old one is gone.
        var lateEmail = MembersKit.NewEmail("late");
        var late = await MembersKit.InviteOkAsync(owner, lateEmail, Roles.Sales);
        await MembersKit.ExpireAsync(fixture, late.Invitation.Id);
        var lateInvitee = MembersKit.Invitee(fixture, Guid.NewGuid(), lateEmail);
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(lateInvitee, late.Token), HttpStatusCode.Gone, "invitation.expired");
        Assert.Equal("expired", Assert.Single((await owner.OkAsync<InvitationListResponse>(await owner.GetAsync("/api/v1/members/invitations?status=expired"))).Items).Status);

        var resent = await owner.OkAsync<IssuedInvitationResponse>(await owner.PostAsync($"/api/v1/members/invitations/{late.Invitation.Id}/resend", new { }));
        Assert.NotEqual(late.Token, resent.Token);
        Assert.Equal(("pending", 2), (resent.Invitation.Status, resent.Invitation.TokenCount));
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(lateInvitee, late.Token), HttpStatusCode.NotFound, "invitation.not_found");
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(lateInvitee, resent.Token)).StatusCode);
        Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberInviteResent));

        // Revoked → 404 and cannot be revoked or resent again.
        var goneEmail = MembersKit.NewEmail("gone");
        var gone = await MembersKit.InviteOkAsync(owner, goneEmail, Roles.Viewer);
        var revoked = await owner.OkAsync<InvitationResponse>(await owner.PostAsync($"/api/v1/members/invitations/{gone.Invitation.Id}/revoke", new { }));
        Assert.Equal("revoked", revoked.Status);
        await HttpAssert.ProblemAsync(
            await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, Guid.NewGuid(), goneEmail), gone.Token), HttpStatusCode.NotFound, "invitation.not_found");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/members/invitations/{gone.Invitation.Id}/revoke", new { }), HttpStatusCode.Conflict, "invitation.not_pending");
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/members/invitations/{gone.Invitation.Id}/resend", new { }), HttpStatusCode.Conflict, "invitation.not_pending");
        var revokedEvent = Assert.Single(await MembersKit.AuditAsync(owner, AuditActions.MemberInviteRevoked));
        Assert.DoesNotContain("example.test", MembersKit.ChangesJson(revokedEvent), StringComparison.Ordinal);

        // A revoked address can be invited again.
        await MembersKit.InviteOkAsync(owner, goneEmail, Roles.Viewer);
    }

    [Fact]
    public async Task One_pending_invitation_per_address_and_none_for_existing_members()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("once");
        var first = await MembersKit.InviteOkAsync(owner, email, Roles.Sales);

        var again = await HttpAssert.ProblemJsonAsync(await MembersKit.InviteAsync(owner, email.ToUpperInvariant(), Roles.Viewer), HttpStatusCode.Conflict, "invitation.already_pending");
        Assert.Equal(first.Invitation.Id, again.GetProperty("invitationId").GetGuid());

        // An expired pending invitation does not block a new one (it is closed as expired).
        await MembersKit.ExpireAsync(fixture, first.Invitation.Id);
        var second = await MembersKit.InviteOkAsync(owner, email, Roles.Viewer);
        var all = (await owner.OkAsync<InvitationListResponse>(await owner.GetAsync("/api/v1/members/invitations"))).Items;
        Assert.Equal("expired", all.Single(i => i.Id == first.Invitation.Id).Status);

        // Once accepted, the address is a member: no new invitation.
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, Guid.NewGuid(), email), second.Token)).StatusCode);
        await HttpAssert.ProblemAsync(await MembersKit.InviteAsync(owner, email, Roles.Sales), HttpStatusCode.Conflict, "members.already_member");

        // A user who already belongs to the tenant cannot take a second membership through another invitation.
        var otherEmail = MembersKit.NewEmail("second-address");
        var other = await MembersKit.InviteOkAsync(owner, otherEmail, Roles.Viewer);
        await HttpAssert.ProblemAsync(
            await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, owner.UserId, otherEmail), other.Token), HttpStatusCode.Conflict, "invitation.already_member");

        await HttpAssert.ProblemAsync(await MembersKit.InviteAsync(owner, "not-an-address", Roles.Sales), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await MembersKit.InviteAsync(owner, MembersKit.NewEmail("x"), Roles.Owner), HttpStatusCode.UnprocessableEntity, "validation.failed");
        await HttpAssert.ProblemAsync(await MembersKit.InviteAsync(owner, MembersKit.NewEmail("x"), "dealer_user"), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Theory]
    [InlineData("owner", "admin", true)]
    [InlineData("owner", "viewer", true)]
    [InlineData("admin", "admin", false)]
    [InlineData("admin", "manager", true)]
    [InlineData("admin", "warehouse_staff", true)]
    public async Task Only_an_owner_invites_admins_and_nobody_grants_above_their_role(string inviterRole, string role, bool allowed)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var inviter = inviterRole == Roles.Owner ? owner : await owner.AddMemberAsync(fixture, inviterRole);
        var response = await MembersKit.InviteAsync(inviter, MembersKit.NewEmail("h"), role);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        else
        {
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "members.role_not_grantable");
        }
    }

    [Fact]
    public async Task An_admin_cannot_resend_or_revoke_an_admin_invitation()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var admin = await owner.AddMemberAsync(fixture, Roles.Admin);
        var issued = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("adm"), Roles.Admin);
        await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/members/invitations/{issued.Invitation.Id}/resend", new { }), HttpStatusCode.Forbidden, "members.role_not_grantable");
        await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/members/invitations/{issued.Invitation.Id}/revoke", new { }), HttpStatusCode.Forbidden, "members.role_not_grantable");
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("sales")]
    [InlineData("warehouse_staff")]
    [InlineData("viewer")]
    public async Task Roles_without_members_invite_cannot_invite(string role)
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var issued = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("p"), Roles.Viewer);
        var caller = await owner.AddMemberAsync(fixture, role);
        await HttpAssert.ProblemAsync(await MembersKit.InviteAsync(caller, MembersKit.NewEmail("q"), Roles.Viewer), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await caller.PostAsync($"/api/v1/members/invitations/{issued.Invitation.Id}/revoke", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
        await HttpAssert.ProblemAsync(await caller.PostAsync($"/api/v1/members/invitations/{issued.Invitation.Id}/resend", new { }), HttpStatusCode.Forbidden, "auth.forbidden");
        var list = await caller.GetAsync("/api/v1/members/invitations");
        Assert.Equal(role == Roles.Manager ? HttpStatusCode.OK : HttpStatusCode.Forbidden, list.StatusCode); // members.read
    }

    [Fact]
    public async Task Max_users_counts_active_members_and_open_invitations()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await BillingKit.OverrideAsync(fixture, owner.TenantId, EntitlementCodes.MaxUsers, limit: 3);
        var member = await MembersKit.JoinAsync(fixture, owner, Roles.Sales); // owner + sales = 2 seats
        var pending = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("seat3"), Roles.Viewer); // 3 seats

        var full = await HttpAssert.ProblemJsonAsync(await MembersKit.InviteAsync(owner, MembersKit.NewEmail("seat4"), Roles.Viewer), HttpStatusCode.Forbidden, EntitlementProblems.LimitExceeded);
        Assert.Equal((EntitlementCodes.MaxUsers, 3, 3), (full.GetProperty("feature").GetString(), full.GetProperty("limit").GetInt32(), full.GetProperty("current").GetInt32()));

        // An expired invitation frees its seat; resending it takes the seat back only if there is room.
        await MembersKit.ExpireAsync(fixture, pending.Invitation.Id);
        var replacement = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("seat3b"), Roles.Viewer);
        await HttpAssert.ProblemAsync(
            await owner.PostAsync($"/api/v1/members/invitations/{pending.Invitation.Id}/resend", new { }), HttpStatusCode.Forbidden, EntitlementProblems.LimitExceeded);

        // A disabled member frees a seat; re-enabling needs one.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/members/{member.UserId}/disable", new { })).StatusCode);
        await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("seat2b"), Roles.Viewer); // owner + 2 open invitations = 3
        await HttpAssert.ProblemAsync(await owner.PostAsync($"/api/v1/members/{member.UserId}/enable", new { }), HttpStatusCode.Forbidden, EntitlementProblems.LimitExceeded);

        // Revoking frees the seat again.
        await owner.OkAsync<InvitationResponse>(await owner.PostAsync($"/api/v1/members/invitations/{replacement.Invitation.Id}/revoke", new { }));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/members/{member.UserId}/enable", new { })).StatusCode);

        // Usage shown to clients is the same count: owner + re-enabled member + one open invitation.
        var usage = await owner.OkAsync<UsageResponse>(await owner.GetAsync("/api/v1/billing/usage"));
        var seats = usage.Items.Single(i => i.Code == EntitlementCodes.MaxUsers);
        Assert.Equal((3L, (long?)3), (seats.Current, seats.Limit));
    }

    [Fact]
    public async Task Accepting_after_a_downgrade_that_left_no_room_is_refused()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("late-seat");
        var issued = await MembersKit.InviteOkAsync(owner, email, Roles.Viewer);
        await BillingKit.OverrideAsync(fixture, owner.TenantId, EntitlementCodes.MaxUsers, limit: 1);
        await HttpAssert.ProblemAsync(
            await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, Guid.NewGuid(), email), issued.Token), HttpStatusCode.Forbidden, EntitlementProblems.LimitExceeded);

        await BillingKit.OverrideAsync(fixture, owner.TenantId, EntitlementCodes.MaxUsers, limit: 2);
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, Guid.NewGuid(), email), issued.Token)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_invitations_cannot_overshoot_max_users()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        await BillingKit.OverrideAsync(fixture, owner.TenantId, EntitlementCodes.MaxUsers, limit: 4);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => MembersKit.InviteAsync(owner, MembersKit.NewEmail("race" + i), Roles.Viewer)));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
        Assert.Equal(3L, await BillingKit.ScalarAsync<long>(
            fixture, "SELECT count(*) FROM identity.invitations WHERE tenant_id = $1 AND status = 'pending'", owner.TenantId));
    }

    [Fact]
    public async Task Invitations_are_tenant_isolated()
    {
        var a = await CatalogClient.NewTenantOwnerAsync(fixture);
        var b = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("iso");
        var issued = await MembersKit.InviteOkAsync(a, email, Roles.Sales);

        Assert.Empty((await b.OkAsync<InvitationListResponse>(await b.GetAsync("/api/v1/members/invitations"))).Items);
        await HttpAssert.ProblemAsync(await b.PostAsync($"/api/v1/members/invitations/{issued.Invitation.Id}/revoke", new { }), HttpStatusCode.NotFound, "invitation.not_found");
        await HttpAssert.ProblemAsync(await b.PostAsync($"/api/v1/members/invitations/{issued.Invitation.Id}/resend", new { }), HttpStatusCode.NotFound, "invitation.not_found");

        // B's owner sending A's id with A's header is not a member of A.
        await HttpAssert.ProblemAsync(await b.Targeting(a.TenantId).GetAsync("/api/v1/members/invitations"), HttpStatusCode.Forbidden, "tenant.access_denied");

        // B may invite the same address independently; accepting A's token joins A only.
        var fromB = await MembersKit.InviteOkAsync(b, email, Roles.Viewer);
        var userId = Guid.NewGuid();
        var invitee = MembersKit.Invitee(fixture, userId, email);
        var joined = await a.OkAsync<AcceptInvitationResponse>(await MembersKit.AcceptAsync(invitee, issued.Token));
        Assert.Equal(a.TenantId, joined.TenantId);
        var me = await a.OkAsync<MeResponse>(await invitee.GetAsync("/api/v1/me", Ct));
        Assert.Equal(a.TenantId, Assert.Single(me.Memberships).TenantId);
        Assert.Equal("pending", Assert.Single((await b.OkAsync<InvitationListResponse>(await b.GetAsync("/api/v1/members/invitations"))).Items).Status);
        Assert.Equal(HttpStatusCode.OK, (await MembersKit.AcceptAsync(invitee, fromB.Token)).StatusCode);
    }

    [Fact]
    public async Task Accepting_into_a_suspended_tenant_is_refused()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var email = MembersKit.NewEmail("suspended");
        var issued = await MembersKit.InviteOkAsync(owner, email, Roles.Viewer);
        await BillingKit.ExecuteAsync(fixture, "UPDATE identity.tenants SET status = 'suspended' WHERE id = $1", owner.TenantId);
        await HttpAssert.ProblemAsync(
            await MembersKit.AcceptAsync(MembersKit.Invitee(fixture, Guid.NewGuid(), email), issued.Token), HttpStatusCode.Forbidden, "tenant.inactive");
    }

    [Fact]
    public async Task The_runtime_role_finds_an_invitation_only_through_the_token_lookup_function()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var issued = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("db"), Roles.Viewer);
        var revoked = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("db-revoked"), Roles.Viewer);
        await owner.OkAsync<InvitationResponse>(await owner.PostAsync($"/api/v1/members/invitations/{revoked.Invitation.Id}/revoke", new { }));

        await using var connection = new Npgsql.NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(Ct);
        async Task<object?> Scalar(string sql, object? parameter = null)
        {
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            if (parameter is not null)
            {
                command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = parameter });
            }

            return await command.ExecuteScalarAsync(Ct);
        }

        // No tenant context: RLS shows nothing, the function maps a pending token hash to its tenant id only.
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM identity.invitations"));
        Assert.Equal(owner.TenantId, await Scalar("SELECT identity.find_invitation_tenant($1)", SHA256.HashData(Encoding.ASCII.GetBytes(issued.Token))));
        Assert.Equal(DBNull.Value, await Scalar("SELECT identity.find_invitation_tenant($1)", SHA256.HashData(Encoding.ASCII.GetBytes(revoked.Token))));
        Assert.Equal(DBNull.Value, await Scalar("SELECT identity.find_invitation_tenant($1)", new byte[32]));
        Assert.Equal(DBNull.Value, await Scalar("SELECT identity.find_invitation_tenant(NULL)"));
        Assert.Equal(false, await Scalar("SELECT has_function_privilege('public', 'identity.find_invitation_tenant(bytea)', 'EXECUTE')"));
        Assert.Equal(false, await Scalar("SELECT has_table_privilege('bayios_app', 'identity.invitations', 'DELETE')"));
    }

    [Fact]
    public async Task Accept_needs_an_authenticated_caller()
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture);
        var issued = await MembersKit.InviteOkAsync(owner, MembersKit.NewEmail("anon"), Roles.Viewer);
        await HttpAssert.ProblemAsync(await MembersKit.AcceptAsync(fixture.CreateClient(), issued.Token), HttpStatusCode.Unauthorized, "auth.invalid_token");
    }
}

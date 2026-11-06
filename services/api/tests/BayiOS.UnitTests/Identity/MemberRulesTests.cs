using System.Security.Claims;
using BayiOS.Modules.Identity.Api;
using BayiOS.Modules.Identity.Domain;
using BayiOS.SharedKernel.Authorization;

namespace BayiOS.UnitTests.Identity;

/// <summary>#129: who may grant which role, the e-mail verification rule, invitation tokens and states.</summary>
public class MemberRulesTests
{
    [Theory]
    [InlineData("owner", "admin", true)]
    [InlineData("owner", "manager", true)]
    [InlineData("owner", "viewer", true)]
    [InlineData("owner", "owner", false)]
    [InlineData("admin", "admin", false)]
    [InlineData("admin", "owner", false)]
    [InlineData("admin", "manager", true)]
    [InlineData("admin", "sales", true)]
    [InlineData("admin", "warehouse_staff", true)]
    [InlineData("admin", "viewer", true)]
    [InlineData("manager", "sales", false)]
    [InlineData("sales", "viewer", false)]
    [InlineData(null, "viewer", false)]
    [InlineData("owner", "dealer_user", false)]
    public void Nobody_grants_a_role_above_their_own_and_only_an_owner_grants_admin(string? caller, string role, bool allowed) =>
        Assert.Equal(allowed, MemberRoleRules.CanGrant(caller, role));

    [Fact]
    public void Grantable_roles_are_the_matrix_roles_without_owner()
    {
        Assert.Equal(Roles.All.Where(r => r != Roles.Owner), MemberRoleRules.Grantable);
        Assert.All(MemberRoleRules.Grantable.Where(r => r != Roles.Admin), r => Assert.True(MemberRoleRules.IsBelowAdmin(r)));
        Assert.False(MemberRoleRules.IsBelowAdmin(Roles.Admin));
        Assert.False(MemberRoleRules.IsBelowAdmin(Roles.Owner));
    }

    [Theory]
    [InlineData(null, true, false, true, true)] // Supabase: user_metadata.email_verified after confirmation
    [InlineData(null, true, false, false, false)] // metadata not trusted (hook deployed)
    [InlineData(true, null, false, false, true)] // server-asserted top-level claim
    [InlineData(false, true, false, true, true)]
    [InlineData(null, false, false, true, false)]
    [InlineData(null, null, false, true, false)]
    [InlineData(true, true, true, true, false)] // anonymous sessions never count
    public void Email_verification_rule(bool? topLevel, bool? metadata, bool anonymous, bool trustMetadata, bool verified) =>
        Assert.Equal(verified, EmailVerification.IsVerified(topLevel, metadata, anonymous, trustMetadata));

    [Theory]
    [InlineData("  Ayse.Yilmaz@Example.TEST ", "ayse.yilmaz@example.test")]
    [InlineData("a@b.co", "a@b.co")]
    [InlineData("no-at-sign", null)]
    [InlineData("@example.test", null)]
    [InlineData("a@b", null)]
    [InlineData("a@b.", null)]
    [InlineData("a@@b.co", null)]
    [InlineData("a b@c.co", null)]
    [InlineData("a@b.co,c@d.co", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Emails_are_normalised_or_rejected(string? input, string? expected) =>
        Assert.Equal(expected, EmailVerification.Normalize(input));

    [Fact]
    public void Tokens_are_random_and_only_their_hash_is_comparable()
    {
        var (token, hash) = InvitationTokens.Generate();
        var (other, otherHash) = InvitationTokens.Generate();
        Assert.Equal(InvitationTokens.Length, token.Length);
        Assert.NotEqual(token, other);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, InvitationTokens.TryHash(token));
        Assert.NotEqual(hash, otherHash);
        Assert.Null(InvitationTokens.TryHash(null));
        Assert.Null(InvitationTokens.TryHash(token[..42]));
        Assert.Null(InvitationTokens.TryHash(token[..42] + "="));
        Assert.Null(InvitationTokens.TryHash(token[..42] + "é"));
    }

    [Fact]
    public void Invitation_states()
    {
        var now = DateTimeOffset.Parse("2026-11-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var by = Guid.NewGuid();
        var invitation = Invitation.Create(Guid.NewGuid(), "a@b.co", Roles.Sales, new byte[32], now.AddDays(7), by, now);
        Assert.True(invitation.IsOpen(now));
        Assert.Equal(InvitationStatuses.Pending, invitation.EffectiveStatus(now));
        Assert.False(invitation.IsOpen(now.AddDays(7)));
        Assert.Equal(InvitationStatuses.Expired, invitation.EffectiveStatus(now.AddDays(7)));

        invitation.Rotate(new byte[32], now.AddDays(14), by, now.AddDays(7));
        Assert.Equal((2, 2L), (invitation.TokenCount, invitation.RowVersion));
        Assert.True(invitation.IsOpen(now.AddDays(8)));

        var user = Guid.NewGuid();
        invitation.Accept(user, now.AddDays(8));
        Assert.Equal((InvitationStatuses.Accepted, user), (invitation.EffectiveStatus(now.AddDays(30)), invitation.AcceptedBy));
        Assert.False(invitation.IsOpen(now.AddDays(8)));
    }

    [Fact]
    public void Claims_come_from_the_supabase_token_shape()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("email", "a@b.co"),
            new Claim("user_metadata", """{"email":"a@b.co","email_verified":true,"sub":"x"}""", "JSON"),
            new Claim("is_anonymous", "false"),
        ]));
        var claims = MemberEndpoints.ReadClaims(principal);
        Assert.Equal(("a@b.co", (bool?)null, (bool?)true, false), (claims.Email, claims.EmailVerified, claims.UserMetadataEmailVerified, claims.IsAnonymous));

        var odd = MemberEndpoints.ReadClaims(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("user_metadata", "not json"),
            new Claim("email_verified", "true"),
            new Claim("is_anonymous", "true"),
        ])));
        Assert.Equal(((string?)null, (bool?)true, (bool?)null, true), (odd.Email, odd.EmailVerified, odd.UserMetadataEmailVerified, odd.IsAnonymous));

        var stringly = MemberEndpoints.ReadClaims(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user_metadata", """{"email_verified":"true"}""")])));
        Assert.Null(stringly.UserMetadataEmailVerified); // only a JSON boolean counts
    }
}

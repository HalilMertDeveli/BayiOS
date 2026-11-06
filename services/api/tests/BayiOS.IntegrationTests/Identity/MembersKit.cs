using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BayiOS.Api.Platform.Audit;
using BayiOS.IntegrationTests.Billing;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Identity.Contracts;

namespace BayiOS.IntegrationTests.Identity;

/// <summary>Helpers for the team-management tests (#129). Synthetic addresses only (example.test).</summary>
internal static class MembersKit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string NewEmail(string tag) => $"{tag}-{Guid.NewGuid():N}@example.test";

    public static Task<HttpResponseMessage> InviteAsync(CatalogClient inviter, string email, string role) =>
        inviter.PostAsync("/api/v1/members/invitations", new { email, role });

    public static async Task<IssuedInvitationResponse> InviteOkAsync(CatalogClient inviter, string email, string role)
    {
        var response = await InviteAsync(inviter, email, role);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"invite: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<IssuedInvitationResponse>(Json, Ct))!;
    }

    /// <summary>A signed-in Supabase user (no tenant header): what the invitee has before accepting.</summary>
    public static HttpClient Invitee(ApiFixture fixture, Guid userId, string? email, bool? metadataEmailVerified = true, bool? topLevelEmailVerified = null, bool isAnonymous = false)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.CreateSupabase(userId, email, metadataEmailVerified, topLevelEmailVerified, isAnonymous));
        return client;
    }

    public static async Task<HttpResponseMessage> AcceptAsync(HttpClient invitee, string? token, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invitations/accept")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token }, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(IdempotentRequests.Header, idempotencyKey ?? Guid.NewGuid().ToString());
        return await invitee.SendAsync(request, Ct);
    }

    /// <summary>Accepts and returns the invitee as a tenant client.</summary>
    public static async Task<CatalogClient> JoinAsync(ApiFixture fixture, CatalogClient inviter, string role)
    {
        var email = NewEmail(role.Replace('_', '-'));
        var issued = await InviteOkAsync(inviter, email, role);
        var userId = Guid.NewGuid();
        var invitee = Invitee(fixture, userId, email);
        var accepted = await AcceptAsync(invitee, issued.Token);
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"accept: {(int)accepted.StatusCode} {await accepted.Content.ReadAsStringAsync(Ct)}");
        return new CatalogClient(invitee, inviter.TenantId, userId);
    }

    public static Task<HttpResponseMessage> ChangeRoleAsync(CatalogClient caller, Guid userId, string role) =>
        caller.SendAsync(HttpMethod.Put, $"/api/v1/members/{userId}/role", new { role }, idempotencyKey: Guid.NewGuid().ToString());

    public static async Task<IReadOnlyList<AuditEventResponse>> AuditAsync(CatalogClient owner, string action)
    {
        var page = await owner.OkAsync<AuditEventPageResponse>(await owner.GetAsync($"/api/v1/audit-events?action={action}"));
        return page.Items;
    }

    public static string ChangesJson(AuditEventResponse auditEvent) =>
        JsonSerializer.Serialize(auditEvent.Changes ?? throw new InvalidOperationException("No changes recorded."));

    /// <summary>Moves an invitation's lifetime into the past (as if 8 days had gone by).</summary>
    public static Task ExpireAsync(ApiFixture fixture, Guid invitationId) =>
        BillingKit.ExecuteAsync(
            fixture,
            "UPDATE identity.invitations SET created_at = now() - interval '8 days', expires_at = now() - interval '1 day' WHERE id = $1",
            invitationId);
}

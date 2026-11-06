using System.Security.Claims;
using System.Text.Json;
using BayiOS.Modules.Identity.Application;
using BayiOS.Modules.Identity.Contracts;
using BayiOS.Modules.Identity.Domain;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Entitlements;
using BayiOS.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BayiOS.Modules.Identity.Api;

/// <summary>Team management (#129): invitations, role changes, disable/enable, ownership transfer, invitation acceptance.</summary>
internal static class MemberEndpoints
{
    private const string TokenNote =
        " No e-mail is sent: the response carries the invitation token once (only its SHA-256 is stored, and the response is"
        + " not kept for Idempotency-Key replay, so this call takes no Idempotency-Key). Share it with the invitee manually.";

    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Identity")
            .RequireAuthorization()
            .WithUnitOfWork()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/members/invitations", ListInvitations)
            .WithName("Identity_ListInvitations")
            .WithSummary("Aktif tenant'ın davetleri (en yeni 200); durum filtresi: pending, accepted, revoked, expired. Token hiçbir zaman dönmez")
            .RequirePermission(Permissions.MembersRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/members/invitations", Invite)
            .WithName("Identity_CreateInvitation")
            .WithSummary("E-posta + rol ile davet oluşturur (7 gün geçerli); kopyalanabilir token döner")
            .WithDescription(
                "Owner admin ve altını, admin yalnızca manager ve altını davet eder (403 members.role_not_grantable). E-posta başına tek "
                + "bekleyen davet (409 invitation.already_pending, invitationId ile); zaten üye olan adres 409 members.already_member. "
                + "max_users = aktif üye + açık davet (403 entitlement.limit_exceeded)." + TokenNote)
            .RequirePermission(Permissions.MembersInvite)
            .EnforcesEntitlementLimit(EntitlementCodes.MaxUsers)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/members/invitations/{invitationId:guid}/resend", Resend)
            .WithName("Identity_ResendInvitation")
            .WithSummary("Bekleyen (süresi dolmuş olabilir) davete yeni token ve yeni süre verir; eski token geçersizleşir")
            .WithDescription("409 invitation.not_pending (kabul edilmiş/iptal edilmiş); 403 entitlement.limit_exceeded." + TokenNote)
            .RequirePermission(Permissions.MembersInvite)
            .EnforcesEntitlementLimit(EntitlementCodes.MaxUsers)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/members/invitations/{invitationId:guid}/revoke", Revoke)
            .WithName("Identity_RevokeInvitation")
            .WithSummary("Bekleyen daveti iptal eder (token geçersizleşir, koltuk boşalır)")
            .RequirePermission(Permissions.MembersInvite)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/members/{userId:guid}/role", ChangeRole)
            .WithName("Identity_ChangeMemberRole")
            .WithSummary("Üyenin rolünü değiştirir")
            .WithDescription(
                "Owner herkesi, admin yalnızca manager ve altını yönetir (403 members.not_manageable) ve admin atayamaz "
                + "(403 members.role_not_grantable). owner rolü yalnızca sahiplik devriyle verilir. Son aktif owner düşürülemez (409 members.last_owner).")
            .RequirePermission(Permissions.MembersManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/members/{userId:guid}/disable", Disable)
            .WithName("Identity_DisableMember")
            .WithSummary("Üyeyi askıya alır; tenant erişimi bir sonraki istekte kesilir. Kendini ve son aktif owner'ı askıya alamaz")
            .RequirePermission(Permissions.MembersManage)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/members/{userId:guid}/enable", Enable)
            .WithName("Identity_EnableMember")
            .WithSummary("Askıdaki üyeyi yeniden etkinleştirir (max_users sınırına tabi)")
            .RequirePermission(Permissions.MembersManage)
            .EnforcesEntitlementLimit(EntitlementCodes.MaxUsers)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/members/ownership-transfer", TransferOwnership)
            .WithName("Identity_TransferOwnership")
            .WithSummary("Sahipliği aktif bir üyeye devreder; çağıran owner admin olur (tek transaction)")
            .RequirePermission(Permissions.OwnershipTransfer)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/invitations/accept", Accept)
            .WithName("Identity_AcceptInvitation")
            .WithSummary("Daveti kabul eder (X-Tenant-Id yok): token + oturumdaki doğrulanmış e-posta davet adresiyle aynı olmalı")
            .WithDescription(
                "E-posta ve doğrulama yalnızca erişim token'ının claim'lerinden okunur (email; email_verified veya Supabase "
                + "user_metadata.email_verified), gövdeden asla. 404 invitation.not_found (bilinmeyen, kullanılmış, iptal edilmiş "
                + "veya yenilenmiş token), 403 invitation.email_mismatch, 403 invitation.email_not_verified, 410 invitation.expired, "
                + "409 invitation.already_member, 403 tenant.inactive, 403 entitlement.limit_exceeded. Başarıda üyelik aktif oluşturulur.")
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        return app;
    }

    /// <param name="status">Virgülle durum listesi (pending, accepted, revoked, expired); verilmezse tümü.</param>
    private static async Task<Results<Ok<InvitationListResponse>, ProblemHttpResult>> ListInvitations(
        HttpRequest http, MemberService service, string? status, CancellationToken cancellationToken)
    {
        var unknown = http.Query.Keys.Where(k => !string.Equals(k, "status", StringComparison.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            return Malformed($"Unknown query parameter(s): {string.Join(", ", unknown)}");
        }

        string[] statuses = string.IsNullOrWhiteSpace(status)
            ? []
            : [.. status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
        if (!statuses.All(InvitationStatuses.All.Contains))
        {
            return Malformed($"status must be a comma-separated subset of: {string.Join(", ", InvitationStatuses.All)}");
        }

        return TypedResults.Ok(await service.ListInvitationsAsync(statuses, cancellationToken));
    }

    private static async Task<Results<Created<IssuedInvitationResponse>, ProblemHttpResult>> Invite(
        CreateInvitationRequest request, MemberService service, CancellationToken cancellationToken)
    {
        var result = await service.InviteAsync(request, cancellationToken);
        return result.Error is { } error
            ? IdentityEndpoints.Problem(error)
            : TypedResults.Created("/api/v1/members/invitations", result.Value);
    }

    private static async Task<Results<Ok<IssuedInvitationResponse>, ProblemHttpResult>> Resend(
        Guid invitationId, MemberService service, CancellationToken cancellationToken) =>
        OkOrProblem(await service.ResendAsync(invitationId, cancellationToken));

    private static async Task<Results<Ok<InvitationResponse>, ProblemHttpResult>> Revoke(
        Guid invitationId, MemberService service, CancellationToken cancellationToken) =>
        OkOrProblem(await service.RevokeAsync(invitationId, cancellationToken));

    private static async Task<Results<Ok<MemberResponse>, ProblemHttpResult>> ChangeRole(
        Guid userId, ChangeMemberRoleRequest request, MemberService service, CancellationToken cancellationToken) =>
        OkOrProblem(await service.ChangeRoleAsync(userId, request, cancellationToken));

    private static async Task<Results<Ok<MemberResponse>, ProblemHttpResult>> Disable(
        Guid userId, MemberService service, CancellationToken cancellationToken) =>
        OkOrProblem(await service.DisableAsync(userId, cancellationToken));

    private static async Task<Results<Ok<MemberResponse>, ProblemHttpResult>> Enable(
        Guid userId, MemberService service, CancellationToken cancellationToken) =>
        OkOrProblem(await service.EnableAsync(userId, cancellationToken));

    private static async Task<Results<Ok<OwnershipTransferResponse>, ProblemHttpResult>> TransferOwnership(
        TransferOwnershipRequest request, MemberService service, CancellationToken cancellationToken) =>
        OkOrProblem(await service.TransferOwnershipAsync(request, cancellationToken));

    private static async Task<Results<Ok<AcceptInvitationResponse>, ProblemHttpResult>> Accept(
        AcceptInvitationRequest request, MemberService service, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        OkOrProblem(await service.AcceptAsync(request, ReadClaims(user), cancellationToken));

    /// <summary>
    /// E-mail facts from the validated access token. Supabase: <c>email</c>, <c>user_metadata.email_verified</c>,
    /// <c>is_anonymous</c>; a top-level <c>email_verified</c> (OIDC / custom access-token hook) wins when present.
    /// </summary>
    internal static InviteeClaims ReadClaims(ClaimsPrincipal user)
    {
        static bool? Bool(string? value) => bool.TryParse(value, out var parsed) ? parsed : null;

        bool? metadataVerified = null;
        if (user.FindFirst("user_metadata")?.Value is { Length: > 0 } metadata)
        {
            try
            {
                using var document = JsonDocument.Parse(metadata);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("email_verified", out var verified)
                    && verified.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    metadataVerified = verified.GetBoolean();
                }
            }
            catch (JsonException)
            {
                metadataVerified = null;
            }
        }

        return new InviteeClaims(
            user.FindFirst("email")?.Value,
            Bool(user.FindFirst("email_verified")?.Value),
            metadataVerified,
            Bool(user.FindFirst("is_anonymous")?.Value) == true);
    }

    private static Results<Ok<T>, ProblemHttpResult> OkOrProblem<T>(AppResult<T> result) =>
        result.Error is { } error ? IdentityEndpoints.Problem(error) : TypedResults.Ok(result.Value!);

    private static ProblemHttpResult Malformed(string title) =>
        TypedResults.Problem(ApiProblems.Create(StatusCodes.Status400BadRequest, "request.malformed", title));
}

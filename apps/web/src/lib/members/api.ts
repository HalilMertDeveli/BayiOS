import type { ApiClient } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";

type S = components["schemas"];
export type Member = S["MemberResponse"];
export type MemberPage = S["MemberPageResponse"];
export type Invitation = S["InvitationResponse"];
export type IssuedInvitation = S["IssuedInvitationResponse"];
export type AcceptedInvitation = S["AcceptInvitationResponse"];
export type OwnershipTransfer = S["OwnershipTransferResponse"];

/** Membership statuses GET /members filters by (`?status=`). */
export const MEMBER_STATUSES = ["active", "disabled", "invited"] as const;
export type MemberStatus = (typeof MEMBER_STATUSES)[number];

export type MemberListQuery = { role?: string; status?: string; cursor?: string | null; limit?: number };

export const MEMBER_PAGE_SIZE = 50;

/** One page of GET /members (members.read): keyset paging, role/status filters on the server. */
export async function listMembers(api: ApiClient, query: MemberListQuery = {}, signal?: AbortSignal): Promise<MemberPage> {
  const params = new URLSearchParams({ limit: String(query.limit ?? MEMBER_PAGE_SIZE) });
  if (query.role) params.set("role", query.role);
  if (query.status) params.set("status", query.status);
  if (query.cursor) params.set("cursor", query.cursor);
  return (await api.request<MemberPage>(`/api/v1/members?${params}`, { tenant: true, signal })).data;
}

/** GET /members/invitations (members.read): newest 200; never carries a token. */
export async function listInvitations(api: ApiClient, status: string | null = "pending", signal?: AbortSignal): Promise<Invitation[]> {
  const path = status ? `/api/v1/members/invitations?status=${encodeURIComponent(status)}` : "/api/v1/members/invitations";
  return (await api.request<S["InvitationListResponse"]>(path, { tenant: true, signal })).data.items;
}

/**
 * POST /members/invitations (members.invite). The response carries the one-time token: the caller shows it once and
 * keeps it only in component state. No Idempotency-Key on purpose (the API does not store this response for replay).
 */
export async function createInvitation(api: ApiClient, body: { email: string; role: string }): Promise<IssuedInvitation> {
  return (await api.request<IssuedInvitation>("/api/v1/members/invitations", { method: "POST", tenant: true, body })).data;
}

/** POST /members/invitations/{id}/resend: a new token and a new 7 days; the old link stops working. No Idempotency-Key. */
export async function resendInvitation(api: ApiClient, invitationId: string): Promise<IssuedInvitation> {
  return (
    await api.request<IssuedInvitation>(`/api/v1/members/invitations/${encodeURIComponent(invitationId)}/resend`, { method: "POST", tenant: true })
  ).data;
}

export async function revokeInvitation(api: ApiClient, invitationId: string, idempotencyKey: string): Promise<Invitation> {
  return (
    await api.request<Invitation>(`/api/v1/members/invitations/${encodeURIComponent(invitationId)}/revoke`, {
      method: "POST",
      tenant: true,
      idempotencyKey,
    })
  ).data;
}

export async function changeMemberRole(api: ApiClient, userId: string, role: string, idempotencyKey: string): Promise<Member> {
  return (
    await api.request<Member>(`/api/v1/members/${encodeURIComponent(userId)}/role`, { method: "PUT", tenant: true, body: { role }, idempotencyKey })
  ).data;
}

export async function setMemberEnabled(api: ApiClient, userId: string, enabled: boolean, idempotencyKey: string): Promise<Member> {
  const action = enabled ? "enable" : "disable";
  return (await api.request<Member>(`/api/v1/members/${encodeURIComponent(userId)}/${action}`, { method: "POST", tenant: true, idempotencyKey })).data;
}

export async function transferOwnership(api: ApiClient, userId: string, idempotencyKey: string): Promise<OwnershipTransfer> {
  return (
    await api.request<OwnershipTransfer>("/api/v1/members/ownership-transfer", { method: "POST", tenant: true, body: { userId }, idempotencyKey })
  ).data;
}

/** POST /invitations/accept: identity-only (no X-Tenant-Id). The API reads the e-mail from the access token, never the body. */
export async function acceptInvitation(api: ApiClient, token: string, idempotencyKey: string): Promise<AcceptedInvitation> {
  return (await api.request<AcceptedInvitation>("/api/v1/invitations/accept", { method: "POST", body: { token }, idempotencyKey })).data;
}

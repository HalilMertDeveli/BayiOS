import type { Access } from "@/lib/session/access";
import { ROLES, type Role } from "@/lib/navigation";

/**
 * Team management visibility (#129). The hierarchy is the API's (roles-permissions §3.1 footnote 1, "#129 ekip
 * yönetimi"): nobody grants a role at or above their own, admin is granted only by the owner, and `owner` moves only by
 * ownership transfer. The permissions come from GET /me; the role is only used to place the caller in that hierarchy.
 * Visibility only — the API checks every call again (403 members.role_not_grantable / members.not_manageable).
 */

/** Lower number = higher in the hierarchy. Unknown roles rank lowest (never a reason to show more). */
export function roleRank(role: string): number {
  const index = (ROLES as readonly string[]).indexOf(role);
  return index === -1 ? ROLES.length : index;
}

/** Roles an invitation or a role change can carry: every role except owner. */
export const ASSIGNABLE_ROLES: readonly Role[] = ROLES.filter((r) => r !== "owner");

/** Roles the caller may invite with (members.invite): strictly below their own role. */
export function invitableRoles(callerRole: string | null, access: Access): Role[] {
  if (!callerRole || !access.canSome("members.invite")) return [];
  const rank = roleRank(callerRole);
  return ASSIGNABLE_ROLES.filter((r) => roleRank(r) > rank);
}

export type MemberLike = { userId: string; role: string; status: string };

/**
 * May the caller change this member's role or suspend / re-enable them (members.manage)? Owner: anyone but
 * themselves; admin (conditional grant): only members below admin. Self-management is never offered (the API refuses
 * self-disable; changing one's own role goes through ownership transfer).
 */
export function canManageMember(callerRole: string | null, callerUserId: string | null, access: Access, member: MemberLike): boolean {
  if (!callerRole || !access.canSome("members.manage")) return false;
  if (member.userId === callerUserId) return false;
  if (member.status === "invited") return false;
  if (callerRole === "owner" && access.can("members.manage")) return true;
  return roleRank(member.role) > roleRank(callerRole);
}

/** Roles the caller may set on a member they manage: strictly below their own role, never owner. */
export function grantableRoles(callerRole: string | null, access: Access): Role[] {
  if (!callerRole || !access.canSome("members.manage")) return [];
  const rank = roleRank(callerRole);
  return ASSIGNABLE_ROLES.filter((r) => roleRank(r) > rank);
}

/** Ownership transfer (ownership.transfer, owner only): to an active member who is not already an owner. */
export function canTransferOwnershipTo(callerUserId: string | null, access: Access, member: MemberLike): boolean {
  return access.can("ownership.transfer") && member.userId !== callerUserId && member.status === "active" && member.role !== "owner";
}

/** May the caller resend / revoke an invitation of this role? Same hierarchy as inviting (admin cannot touch admin invites). */
export function canHandleInvitation(callerRole: string | null, access: Access, invitationRole: string): boolean {
  return invitableRoles(callerRole, access).some((r) => r === invitationRole);
}

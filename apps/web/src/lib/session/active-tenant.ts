import { ROLES, type Role } from "@/lib/navigation";
import type { components } from "@/lib/api/schema";

export type Me = components["schemas"]["MeResponse"];
export type Membership = components["schemas"]["MembershipResponse"];

/**
 * The active tenant is a client-side *selection* sent as X-Tenant-Id; it grants nothing — the API checks
 * the membership on every request (ADR 0002). Stored per user so a shared browser never carries one
 * user's selection over to another.
 */
const STORAGE_PREFIX = "bayios.activeTenant.";

export function readActiveTenantId(userId: string): string | null {
  try {
    return window.localStorage.getItem(STORAGE_PREFIX + userId);
  } catch {
    return null;
  }
}

export function writeActiveTenantId(userId: string, tenantId: string): void {
  try {
    window.localStorage.setItem(STORAGE_PREFIX + userId, tenantId);
  } catch {
    // Storage unavailable (private mode): the selection lasts for this page only.
  }
}

export function clearActiveTenantId(userId: string): void {
  try {
    window.localStorage.removeItem(STORAGE_PREFIX + userId);
  } catch {
    // ignore
  }
}

/** Only active memberships can be used (the API rejects the others with 403). */
export function activeMemberships(me: Pick<Me, "memberships">): Membership[] {
  return me.memberships.filter((m) => m.status === "active");
}

/** Status of the business behind a membership (MembershipResponse.tenantStatus, #111): active, suspended or closed. */
export function isTenantUsable(m: Pick<Membership, "tenantStatus">): boolean {
  return !m.tenantStatus || m.tenantStatus === "active";
}

export const tenantStatusLabels: Record<string, string> = { suspended: "Askıya alındı", closed: "Kapatıldı" };

/** "Askıya alındı" / "Kapatıldı" for an unusable business (unknown statuses: "Kullanılamıyor"); null when active. */
export function tenantStatusLabel(m: Pick<Membership, "tenantStatus">): string | null {
  return isTenantUsable(m) ? null : (tenantStatusLabels[m.tenantStatus] ?? "Kullanılamıyor");
}

export type TenantResolution =
  | { kind: "onboarding" }
  | { kind: "pick"; memberships: Membership[] }
  | { kind: "active"; membership: Membership };

/**
 * No usable membership → onboarding; a stored selection that is still valid wins;
 * exactly one membership is selected automatically; otherwise the user picks.
 */
export function resolveActiveTenant(memberships: Membership[], storedTenantId: string | null): TenantResolution {
  if (memberships.length === 0) return { kind: "onboarding" };
  const stored = storedTenantId ? memberships.find((m) => m.tenantId === storedTenantId) : undefined;
  if (stored) return { kind: "active", membership: stored };
  if (memberships.length === 1) return { kind: "active", membership: memberships[0] };
  return { kind: "pick", memberships };
}

/**
 * Menu role from the membership. An unknown role (newer API) falls back to the most restricted role:
 * the menu is visibility only, never authorization.
 */
export function toRole(value: string): Role {
  return (ROLES as readonly string[]).includes(value) ? (value as Role) : "viewer";
}

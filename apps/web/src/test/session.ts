import { vi } from "vitest";
import type { ApiClient } from "@/lib/api/client";
import { createAccess, type Access } from "@/lib/session/access";
import type { Membership } from "@/lib/session/active-tenant";
import type { SessionContextValue } from "@/lib/session/session-context";
import { grantsFor, type FixtureRole, type FixtureRules } from "./me-fixtures";

/** Effective permissions a role gets from the (mocked) GET /me under the given tenant rules. Test helper only. */
export function accessFor(role: FixtureRole, rules: Partial<FixtureRules> = {}): Access {
  return createAccess(grantsFor(role, rules));
}

export const TEST_USER = { id: "u-1", email: "ayse@example.test", fullName: "Ayşe Yılmaz" };

export function membership(role: FixtureRole, overrides: Partial<Membership> = {}): Membership {
  return { tenantId: "t-1", tenantName: "Örnek Motor", tenantSlug: "ornek-motor", role, status: "active", ...overrides };
}

/** A ready session as the provider builds it from GET /me (MOCK: fixtures, not the real API). */
export function mockSession(
  role: FixtureRole,
  options: { api?: ApiClient; rules?: Partial<FixtureRules>; user?: typeof TEST_USER; memberships?: Membership[] } & Partial<SessionContextValue> = {},
): SessionContextValue {
  const { api, rules, user = TEST_USER, memberships, ...overrides } = options;
  const active = membership(role);
  const access = accessFor(role, rules);
  return {
    state: { status: "ready", user, memberships: memberships ?? [active], active, access },
    api: api ?? ({} as ApiClient),
    role,
    access,
    selectTenant: vi.fn(),
    reload: vi.fn(),
    signOut: vi.fn(),
    ...overrides,
  };
}

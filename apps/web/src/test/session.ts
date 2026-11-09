import { vi } from "vitest";
import type { ApiClient } from "@/lib/api/client";
import { createAccess, type Access } from "@/lib/session/access";
import type { Membership } from "@/lib/session/active-tenant";
import type { SessionContextValue } from "@/lib/session/session-context";
import { createEntitlements } from "@/lib/billing/entitlements";
import { entitlementsFor, grantsFor, type FixturePlan, type FixtureRole, type FixtureRules } from "./me-fixtures";

/** Effective permissions a role gets from the (mocked) GET /me under the given tenant rules. Test helper only. */
export function accessFor(role: FixtureRole, rules: Partial<FixtureRules> = {}): Access {
  return createAccess(grantsFor(role, rules));
}

export const TEST_USER = { id: "u-1", email: "ayse@example.test", fullName: "Ayşe Yılmaz" };

export function membership(role: FixtureRole, overrides: Partial<Membership> = {}): Membership {
  return { tenantId: "t-1", tenantName: "Örnek Motor", tenantSlug: "ornek-motor", role, status: "active", tenantStatus: "active", ...overrides };
}

/** A ready session as the provider builds it from GET /me (MOCK: fixtures, not the real API). */
export function mockSession(
  role: FixtureRole,
  options: {
    api?: ApiClient;
    rules?: Partial<FixtureRules>;
    user?: typeof TEST_USER;
    memberships?: Membership[];
    /** MOCK plan of the active tenant (GET /me entitlements); default: a fresh 30-day trial with every add-on. */
    plan?: FixturePlan;
  } & Partial<SessionContextValue> = {},
): SessionContextValue {
  const { api, rules, user = TEST_USER, memberships, plan = "trial", ...overrides } = options;
  const active = membership(role);
  const access = accessFor(role, rules);
  const raw = entitlementsFor(plan);
  return {
    state: { status: "ready", user, memberships: memberships ?? [active], active, access, entitlements: raw },
    api: api ?? ({} as ApiClient),
    role,
    access,
    entitlements: createEntitlements(raw),
    selectTenant: vi.fn(),
    reload: vi.fn(),
    signOut: vi.fn(),
    ...overrides,
  };
}

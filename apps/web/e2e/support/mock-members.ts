/**
 * MOCK TEAM MANAGEMENT API FOR PLAYWRIGHT (#129) — NOT A REAL API.
 *
 * In-memory members + invitations behind GET /members, /members/invitations (+ create, resend, revoke), role change,
 * disable/enable, ownership transfer and POST /invitations/accept. Registered after `signedIn()` (Playwright matches the
 * newest route first) and falls back to the shared mock for everything else. The hierarchy, last-owner, self-disable,
 * already-pending and max_users rules mirror the API (roles-permissions §3.1 #129) so the UI's handling can be tested;
 * the API's own tests are the source of truth. Tokens are synthetic random strings.
 */
import type { Page, Route } from "@playwright/test";
import { grantsForRoleName } from "../../src/test/me-fixtures";
import { API_URL, USER, problem, type MockMembership } from "./mock-backend";

type Api = Awaited<ReturnType<typeof import("./mock-backend").mockApi>>;

export type MockMember = { userId: string; displayName: string | null; fullName: string | null; email: string | null; role: string; status: string; createdAt: string };
export type MockInvitation = {
  id: string;
  email: string;
  role: string;
  status: string;
  expiresAt: string;
  tokenCount: number;
  createdAt: string;
  invitedBy: string;
  acceptedAt: string | null;
  acceptedBy: string | null;
  revokedAt: string | null;
};

const ROLE_ORDER = ["owner", "admin", "manager", "sales", "warehouse_staff", "viewer"];
const rank = (role: string) => (ROLE_ORDER.includes(role) ? ROLE_ORDER.indexOf(role) : ROLE_ORDER.length);
const iso = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString();
const id = (n: number) => `00000000-0000-4000-8000-${n.toString(16).padStart(12, "0")}`;

export function member(n: number, name: string, role: string, status = "active"): MockMember {
  return { userId: id(0x7d0000 + n), displayName: name, fullName: name, email: `uye${n}@example.test`, role, status, createdAt: "2026-11-01T00:00:00Z" };
}

export function defaultMembers(callerRole = "owner"): MockMember[] {
  return [
    { userId: USER.id, displayName: USER.fullName, fullName: USER.fullName, email: USER.email, role: callerRole, status: "active", createdAt: "2026-10-01T00:00:00Z" },
    member(1, "Ayşe Yönetici (mock)", "admin"),
    member(2, "Mert Müdür (mock)", "manager"),
    member(3, "Selin Satış (mock)", "sales"),
    member(4, "Deniz Depocu (mock)", "warehouse_staff", "disabled"),
  ];
}

export type MockTeam = {
  members: MockMember[];
  invitations: MockInvitation[];
  /** Issued tokens (synthetic) → invitation id; the newest per invitation only (resend rotates). */
  tokens: Map<string, string>;
  /** Override for POST /invitations/accept (e.g. an error problem). */
  acceptResult: { status: number; body: unknown } | null;
  /** Force one change call (PUT role / disable / transfer) to fail with this problem. */
  failNext: { status: number; code: string } | null;
  requests: { method: string; path: string; search: string; headers: Record<string, string>; body: unknown }[];
};

let tokenSeq = 0;
function newToken(): string {
  tokenSeq += 1;
  const random = Array.from({ length: 32 }, () => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[Math.floor(Math.random() * 64)]).join("");
  return `${random}${tokenSeq.toString().padStart(11, "0")}`;
}

const json = (route: Route, status: number, body: unknown) =>
  route.fulfill({
    status,
    contentType: status >= 400 ? "application/problem+json" : "application/json",
    headers: { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After" },
    body: JSON.stringify(body),
  });

/**
 * `joinTenant`: the tenant a successful POST /invitations/accept joins (default: the first membership). It is added to
 * the caller's memberships with the invitation's role, so the next GET /me lists it — as the API does.
 */
export async function mockTeam(
  page: Page,
  api: Api,
  options: { members?: MockMember[]; invitations?: MockInvitation[]; joinTenant?: MockMembership } = {},
): Promise<MockTeam> {
  const callerRole = () => api.memberships[0]?.role ?? "owner";
  const team: MockTeam = {
    members: options.members ?? defaultMembers(callerRole()),
    invitations: options.invitations ?? [],
    tokens: new Map(),
    acceptResult: null,
    failNext: null,
    requests: [],
  };
  const seats = () => team.members.filter((m) => m.status === "active").length + team.invitations.filter((i) => i.status === "pending").length;
  const syncSeats = () => {
    api.billing.activeUsers = seats();
  };
  syncSeats();
  const maxUsers = () => (api.billing.plan === "basic" ? 5 : 25);
  const limitProblem = () => problem(403, "entitlement.limit_exceeded", { feature: "max_users", limit: maxUsers(), current: seats(), suggestedPlan: "professional" });
  const issue = (inv: MockInvitation) => {
    for (const [token, invId] of team.tokens) if (invId === inv.id) team.tokens.delete(token);
    const token = newToken();
    team.tokens.set(token, inv.id);
    return { invitation: inv, token };
  };

  await page.route(`${API_URL}/api/v1/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    const isTeam = path.startsWith("/api/v1/members") || path === "/api/v1/invitations/accept";
    if (!isTeam || method === "OPTIONS") return route.fallback();
    const body = (request.postDataJSON?.() ?? null) as Record<string, unknown> | null;
    team.requests.push({ method, path, search: url.search, headers: request.headers(), body });

    const tenantHeader = request.headers()["x-tenant-id"];
    const role = api.memberships.find((m) => m.tenantId === tenantHeader)?.role ?? "viewer";
    const grants = grantsForRoleName(role, api.rules);
    const has = (permission: string) => grants.some((g) => g.permission === permission);
    const forbidden = (permission: string) => json(route, 403, problem(403, "auth.forbidden", { title: `Missing permission: ${permission}` }));
    const needsKey = () => !request.headers()["idempotency-key"];

    if (path === "/api/v1/invitations/accept" && method === "POST") {
      if (tenantHeader) return json(route, 400, problem(400, "request.malformed"));
      if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
      if (team.acceptResult) return json(route, team.acceptResult.status, team.acceptResult.body);
      const invId = team.tokens.get(String(body?.token ?? ""));
      const inv = team.invitations.find((i) => i.id === invId && i.status === "pending");
      if (!inv) return json(route, 404, problem(404, "invitation.not_found"));
      const joined = options.joinTenant ?? api.memberships[0];
      inv.status = "accepted";
      inv.acceptedAt = iso(0);
      team.tokens.delete(String(body?.token));
      if (!api.memberships.some((m) => m.tenantId === joined.tenantId)) api.memberships = [{ ...joined, role: inv.role, status: "active" }, ...api.memberships];
      return json(route, 200, { tenantId: joined.tenantId, tenantName: joined.tenantName, tenantSlug: joined.tenantSlug, role: inv.role, status: "active" });
    }

    if (path === "/api/v1/members" && method === "GET") {
      if (!has("members.read")) return forbidden("members.read");
      const roles = (url.searchParams.get("role") ?? "").split(",").filter(Boolean);
      const statuses = (url.searchParams.get("status") ?? "").split(",").filter(Boolean);
      const limit = Number(url.searchParams.get("limit") ?? 50);
      const start = Number(url.searchParams.get("cursor") ?? 0);
      const all = team.members.filter((m) => (!roles.length || roles.includes(m.role)) && (!statuses.length || statuses.includes(m.status)));
      const items = all.slice(start, start + limit);
      const hasMore = start + limit < all.length;
      return json(route, 200, { items, page: { nextCursor: hasMore ? String(start + limit) : null, hasMore, limit } });
    }

    if (path === "/api/v1/members/invitations" && method === "GET") {
      if (!has("members.read")) return forbidden("members.read");
      const statuses = (url.searchParams.get("status") ?? "").split(",").filter(Boolean);
      return json(route, 200, { items: team.invitations.filter((i) => !statuses.length || statuses.includes(i.status)) });
    }

    if (path === "/api/v1/members/invitations" && method === "POST") {
      if (!has("members.invite")) return forbidden("members.invite");
      const email = String(body?.email ?? "").trim().toLowerCase();
      const newRole = String(body?.role ?? "");
      if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) return json(route, 422, problem(422, "validation.failed", { errors: { email: ["Must be a single e-mail address (at most 254 characters)."] } }));
      if (rank(newRole) <= rank(role) || newRole === "owner") return json(route, 403, problem(403, "members.role_not_grantable", { role: newRole }));
      if (team.members.some((m) => m.email?.toLowerCase() === email)) return json(route, 409, problem(409, "members.already_member"));
      const pending = team.invitations.find((i) => i.email === email && i.status === "pending");
      if (pending) return json(route, 409, problem(409, "invitation.already_pending", { invitationId: pending.id }));
      if (seats() >= maxUsers()) return json(route, 403, limitProblem());
      const inv: MockInvitation = {
        id: id(0x1a0000 + team.invitations.length + 1),
        email,
        role: newRole,
        status: "pending",
        expiresAt: iso(7),
        tokenCount: 1,
        createdAt: iso(0),
        invitedBy: USER.id,
        acceptedAt: null,
        acceptedBy: null,
        revokedAt: null,
      };
      team.invitations.unshift(inv);
      syncSeats();
      return json(route, 201, issue(inv));
    }

    const invAction = path.match(/^\/api\/v1\/members\/invitations\/([^/]+)\/(resend|revoke)$/);
    if (invAction && method === "POST") {
      if (!has("members.invite")) return forbidden("members.invite");
      const inv = team.invitations.find((i) => i.id === invAction[1]);
      if (!inv) return json(route, 404, problem(404, "invitation.not_found"));
      if (rank(inv.role) <= rank(role)) return json(route, 403, problem(403, "members.role_not_grantable", { role: inv.role }));
      if (inv.status !== "pending" && inv.status !== "expired") return json(route, 409, problem(409, "invitation.not_pending", { status: inv.status }));
      if (invAction[2] === "revoke") {
        if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
        inv.status = "revoked";
        inv.revokedAt = iso(0);
        for (const [token, invId] of team.tokens) if (invId === inv.id) team.tokens.delete(token);
        syncSeats();
        return json(route, 200, inv);
      }
      inv.status = "pending";
      inv.expiresAt = iso(7);
      inv.tokenCount += 1;
      syncSeats();
      return json(route, 200, issue(inv));
    }

    if (path === "/api/v1/members/ownership-transfer" && method === "POST") {
      if (!has("ownership.transfer")) return forbidden("ownership.transfer");
      if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
      if (team.failNext) {
        const f = team.failNext;
        team.failNext = null;
        return json(route, f.status, problem(f.status, f.code));
      }
      const target = team.members.find((m) => m.userId === body?.userId);
      const me = team.members.find((m) => m.userId === USER.id);
      if (!target || !me) return json(route, 404, problem(404, "resource.not_found"));
      if (target.status !== "active") return json(route, 409, problem(409, "members.not_active"));
      target.role = "owner";
      me.role = "admin";
      api.memberships = api.memberships.map((m, i) => (i === 0 ? { ...m, role: "admin" } : m));
      return json(route, 200, { newOwner: target, previousOwner: me });
    }

    const memberAction = path.match(/^\/api\/v1\/members\/([^/]+)\/(role|disable|enable)$/);
    if (memberAction && (method === "PUT" || method === "POST")) {
      if (!has("members.manage")) return forbidden("members.manage");
      if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
      const target = team.members.find((m) => m.userId === memberAction[1]);
      if (!target) return json(route, 404, problem(404, "resource.not_found"));
      if (team.failNext) {
        const f = team.failNext;
        team.failNext = null;
        return json(route, f.status, problem(f.status, f.code));
      }
      if (role !== "owner" && rank(target.role) <= rank(role)) return json(route, 403, problem(403, "members.not_manageable"));
      const activeOwners = () => team.members.filter((m) => m.role === "owner" && m.status === "active").length;
      if (memberAction[2] === "role") {
        const newRole = String(body?.role ?? "");
        if (newRole === "owner" || rank(newRole) <= rank(role)) return json(route, 403, problem(403, "members.role_not_grantable", { role: newRole }));
        if (target.role === "owner" && activeOwners() <= 1) return json(route, 409, problem(409, "members.last_owner"));
        target.role = newRole;
        return json(route, 200, target);
      }
      if (memberAction[2] === "disable") {
        if (target.userId === USER.id) return json(route, 409, problem(409, "members.cannot_disable_self"));
        if (target.role === "owner" && activeOwners() <= 1) return json(route, 409, problem(409, "members.last_owner"));
        target.status = "disabled";
        syncSeats();
        return json(route, 200, target);
      }
      if (seats() >= maxUsers()) return json(route, 403, limitProblem());
      target.status = "active";
      syncSeats();
      return json(route, 200, target);
    }
    return route.fallback();
  });
  return team;
}

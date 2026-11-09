import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { grantsFor } from "@/test/me-fixtures";
import { SessionProvider, useSession } from "./session-context";

const replace = vi.fn();
// Stable like Next.js' router (a new object per render would re-run the session effect).
const router = { replace, push: vi.fn() };
vi.mock("next/navigation", () => ({ usePathname: () => "/pano", useRouter: () => router }));
vi.mock("@/lib/supabase/client", () => ({
  getAccessToken: async () => "token",
  getSupabaseBrowserClient: () => ({
    auth: {
      getSession: async () => ({ data: { session: { user: { id: "u-1", user_metadata: {} } } } }),
      onAuthStateChange: () => ({ data: { subscription: { unsubscribe: () => {} } } }),
      signOut: async () => ({}),
    },
  }),
}));

const membership = (tenantId: string, role = "sales") => ({ tenantId, tenantName: tenantId.toUpperCase(), tenantSlug: tenantId, role, status: "active" });

/** MOCK GET /me: memberships plus activeTenant for the X-Tenant-Id header (null for `inactive` tenants). */
function stubMe(memberships: ReturnType<typeof membership>[], inactive: string[] = []) {
  const calls: (string | null)[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      const tenant = (init.headers as Headers).get("X-Tenant-Id");
      calls.push(tenant);
      const m = memberships.find((x) => x.tenantId === tenant);
      const activeTenant = m && !inactive.includes(m.tenantId) ? { tenantId: m.tenantId, role: m.role, permissions: grantsFor(m.role as "sales") } : null;
      expect(url).toBe("http://api.test/api/v1/me");
      return Response.json({ userId: "u-1", email: "a@b.test", fullName: "Ayşe", memberships, activeTenant });
    }),
  );
  return calls;
}

function Probe() {
  const { state, access, role } = useSession();
  return (
    <p data-testid="probe">
      {state.status}|{role ?? "-"}|edit:{access.scope("orders.edit.draft") ?? "none"}|{state.status === "inactive" ? state.tenant.tenantName : ""}
    </p>
  );
}

const renderProvider = () =>
  render(
    <SessionProvider require="tenant">
      <Probe />
    </SessionProvider>,
  );

beforeEach(() => window.localStorage.clear());
afterEach(() => {
  vi.unstubAllGlobals();
  replace.mockReset();
});

describe("SessionProvider — GET /me effective permissions", () => {
  it("auto-selects the only membership and loads its permissions with X-Tenant-Id", async () => {
    const calls = stubMe([membership("t-1")]);
    renderProvider();
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("ready|sales|edit:own|"));
    expect(calls).toEqual([null, "t-1"]);
    expect(window.localStorage.getItem("bayios.activeTenant.u-1")).toBe("t-1");
  });

  it("asks for the stored selection's permissions on the first call", async () => {
    window.localStorage.setItem("bayios.activeTenant.u-1", "t-2");
    const calls = stubMe([membership("t-1", "owner"), membership("t-2", "sales")]);
    renderProvider();
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("ready|sales|edit:own|"));
    expect(calls).toEqual(["t-2"]);
  });

  it("an active membership the API will not open (suspended/closed business) shows the inactive state", async () => {
    window.localStorage.setItem("bayios.activeTenant.u-1", "t-1");
    stubMe([membership("t-1")], ["t-1"]);
    renderProvider();
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("inactive|-|edit:none|T-1"));
    expect(replace).not.toHaveBeenCalled();
  });

  it("several memberships and no selection → tenant picker", async () => {
    stubMe([membership("t-1"), membership("t-2")]);
    renderProvider();
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/isletme-sec"));
  });
});

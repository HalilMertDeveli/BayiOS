import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { INVITE_TOKEN_STORAGE_KEY } from "@/lib/members/invite-link";
import { AcceptInvitation } from "./accept-invitation";

const TOKEN = "U3ludGhldGljSW52aXRhdGlvblRva2VuLTAxMjM0NTY";
const router = { replace: vi.fn(), push: vi.fn() };
vi.mock("next/navigation", () => ({ useRouter: () => router }));

let session: { user: { id: string; email: string } } | null = null;
const signOut = vi.fn(async () => ({}));
vi.mock("@/lib/supabase/client", () => ({
  getAccessToken: async () => (session ? "access-token" : null),
  getSupabaseBrowserClient: () => ({
    auth: { getSession: async () => ({ data: { session } }), refreshSession: async () => ({}), signOut },
  }),
}));

type Call = { url: string; init: RequestInit };
function stubAccept(response: () => Response) {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return response();
    }),
  );
  return calls;
}

const logs: unknown[][] = [];
beforeEach(() => {
  window.sessionStorage.clear();
  window.localStorage.clear();
  window.history.replaceState(null, "", `/davet#${TOKEN}`);
  session = { user: { id: "u-2", email: "davetli@example.test" } };
  logs.length = 0;
  for (const level of ["log", "info", "warn", "error", "debug"] as const) {
    vi.spyOn(console, level).mockImplementation((...args: unknown[]) => {
      logs.push(args);
    });
  }
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  router.replace.mockReset();
  router.push.mockReset();
});

describe("AcceptInvitation", () => {
  it("strips the fragment, accepts with the token in the body only, switches tenant and clears the token", async () => {
    const calls = stubAccept(() =>
      Response.json({ tenantId: "t-9", tenantName: "Örnek Mağaza", tenantSlug: "ornek-magaza", role: "sales", status: "active" }),
    );
    render(<AcceptInvitation />);
    const button = await screen.findByRole("button", { name: "Daveti kabul et" });
    expect(window.location.hash).toBe("");
    expect(window.location.pathname).toBe("/davet");
    expect(window.sessionStorage.getItem(INVITE_TOKEN_STORAGE_KEY)).toBe(TOKEN);
    expect(screen.getByText("davetli@example.test")).toBeTruthy();

    await userEvent.click(button);
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/pano"));
    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe("http://api.test/api/v1/invitations/accept");
    expect(calls[0].url).not.toContain(TOKEN);
    expect(JSON.parse(String(calls[0].init.body))).toEqual({ token: TOKEN });
    const headers = calls[0].init.headers as Headers;
    expect(headers.get("X-Tenant-Id")).toBeNull();
    expect(headers.get("Idempotency-Key")).toBeTruthy();
    expect(window.sessionStorage.getItem(INVITE_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem("bayios.activeTenant.u-2")).toBe("t-9");
    expect(JSON.stringify(logs)).not.toContain(TOKEN);
  });

  it("signed out: offers sign-in / sign-up back to /davet without the token in any link", async () => {
    session = null;
    render(<AcceptInvitation />);
    const signIn = await screen.findByRole("link", { name: "Giriş yap" });
    const signUp = screen.getByRole("link", { name: "Hesap oluştur" });
    expect(signIn.getAttribute("href")).toBe("/giris?next=%2Fdavet");
    expect(signUp.getAttribute("href")).toBe("/kayit?next=%2Fdavet");
    expect(document.body.innerHTML).not.toContain(TOKEN);
    expect(window.sessionStorage.getItem(INVITE_TOKEN_STORAGE_KEY)).toBe(TOKEN);
  });

  it("email mismatch keeps the token and offers to switch account", async () => {
    stubAccept(() => Response.json({ status: 403, code: "invitation.email_mismatch" }, { status: 403 }));
    render(<AcceptInvitation />);
    await userEvent.click(await screen.findByRole("button", { name: "Daveti kabul et" }));
    expect(await screen.findByText("Bu davet başka bir e-posta adresi için")).toBeTruthy();
    expect(window.sessionStorage.getItem(INVITE_TOKEN_STORAGE_KEY)).toBe(TOKEN);
    await userEvent.click(screen.getByRole("button", { name: "Başka hesapla giriş yap" }));
    await waitFor(() => expect(router.push).toHaveBeenCalledWith("/giris?next=%2Fdavet"));
    expect(signOut).toHaveBeenCalledWith({ scope: "local" });
    expect(JSON.stringify(logs)).not.toContain(TOKEN);
  });

  it("expired invitation drops the token", async () => {
    stubAccept(() => Response.json({ status: 410, code: "invitation.expired" }, { status: 410 }));
    render(<AcceptInvitation />);
    await userEvent.click(await screen.findByRole("button", { name: "Daveti kabul et" }));
    expect(await screen.findByText("Davetin süresi dolmuş")).toBeTruthy();
    expect(window.sessionStorage.getItem(INVITE_TOKEN_STORAGE_KEY)).toBeNull();
    expect(screen.queryByRole("button", { name: "Tekrar dene" })).toBeNull();
  });

  it("no token at all: explains how to get back", async () => {
    window.history.replaceState(null, "", "/davet");
    render(<AcceptInvitation />);
    expect(await screen.findByText("Davet bağlantısı bulunamadı")).toBeTruthy();
  });
});

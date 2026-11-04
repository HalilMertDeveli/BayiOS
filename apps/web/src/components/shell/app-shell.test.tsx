import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { AppShell } from "./app-shell";
import type { ApiClient } from "@/lib/api/client";
import { SessionContext, type SessionContextValue } from "@/lib/session/session-context";
import type { Membership } from "@/lib/session/active-tenant";

const ownerMembership: Membership = { tenantId: "t-1", tenantName: "Örnek Motor", tenantSlug: "ornek-motor", role: "owner", status: "active" };

function session(overrides: Partial<SessionContextValue> = {}): SessionContextValue {
  return {
    state: { status: "ready", user: { id: "u-1", email: "ayse@example.test", fullName: "Ayşe Yılmaz" }, memberships: [ownerMembership], active: ownerMembership },
    api: {} as ApiClient,
    role: "owner",
    selectTenant: vi.fn(),
    reload: vi.fn(),
    signOut: vi.fn(),
    ...overrides,
  };
}

function renderShell(children: ReactNode = "x", value: SessionContextValue = session()) {
  return render(<SessionContext value={value}><AppShell>{children}</AppShell></SessionContext>);
}

vi.mock("next/navigation", () => ({ usePathname: () => "/urunler", useRouter: () => ({ replace: vi.fn() }) }));

beforeEach(() => {
  window.localStorage.clear();
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  })) as unknown as typeof window.matchMedia;
});

describe("AppShell", () => {
  it("renders the menu with the current page marked", () => {
    renderShell("İçerik");
    const nav = screen.getByRole("navigation", { name: "Ana menü" });
    expect(within(nav).getByRole("link", { name: "Ürünler" }).getAttribute("aria-current")).toBe("page");
    expect(within(nav).getByRole("link", { name: "Pano" }).getAttribute("aria-current")).toBeNull();
    expect(screen.getByRole("main").textContent).toBe("İçerik");
  });

  it("shows the signed-in user, the active tenant and the role", () => {
    renderShell();
    expect(screen.getByTestId("session-user").textContent).toBe("Ayşe Yılmaz");
    expect(screen.getByTitle("Aktif işletme").textContent).toBe("Örnek Motor");
    expect(screen.getByText("İşletme sahibi")).toBeDefined();
  });

  it("builds the menu from the active membership's role", () => {
    renderShell("x", session({ role: "warehouse_staff" }));
    const nav = screen.getByRole("navigation", { name: "Ana menü" });
    expect(within(nav).queryByRole("link", { name: "Ayarlar" })).toBeNull();
    expect(within(nav).queryByRole("link", { name: "Pano" })).toBeNull();
    expect(within(nav).getByRole("link", { name: "Depo" })).toBeDefined();
  });

  it("offers a tenant switcher when the user has several memberships", async () => {
    const other: Membership = { ...ownerMembership, tenantId: "t-2", tenantName: "Örnek LED", role: "viewer" };
    const selectTenant = vi.fn();
    renderShell("x", session({
      selectTenant,
      state: { status: "ready", user: { id: "u-1", email: "a@b.test", fullName: null }, memberships: [ownerMembership, other], active: ownerMembership },
    }));
    await userEvent.selectOptions(screen.getByLabelText("Aktif işletme"), "t-2");
    expect(selectTenant).toHaveBeenCalledWith("t-2");
  });

  it("signs out from the header", async () => {
    const signOut = vi.fn().mockResolvedValue(undefined);
    renderShell("x", session({ signOut }));
    await userEvent.click(screen.getByRole("button", { name: "Çıkış yap" }));
    expect(signOut).toHaveBeenCalled();
  });

  it("does not render the page before the session is ready", () => {
    renderShell("Gizli içerik", session({ state: { status: "loading" }, role: null }));
    expect(screen.queryByText("Gizli içerik")).toBeNull();
    expect(screen.getByRole("status").textContent).toContain("Oturum yükleniyor");
  });

  it("toggles the sidebar collapse state and persists it", async () => {
    renderShell();
    await userEvent.click(screen.getByRole("button", { name: "Menüyü daralt" }));
    const expand = screen.getByRole("button", { name: "Menüyü genişlet" });
    expect(expand.getAttribute("aria-expanded")).toBe("false");
    expect(window.localStorage.getItem("bayios.sidebar.collapsed")).toBe("1");
    // Collapsed links keep their accessible names.
    expect(screen.getByRole("link", { name: "Siparişler" })).toBeDefined();
  });

  it("opens and closes the mobile drawer", async () => {
    renderShell();
    const open = screen.getByRole("button", { name: "Menüyü aç" });
    expect(open.getAttribute("aria-expanded")).toBe("false");
    expect(open.getAttribute("aria-controls")).toBe("mobile-nav");

    await userEvent.click(open);
    expect(open.getAttribute("aria-expanded")).toBe("true");
    const drawer = document.getElementById("mobile-nav") as HTMLDialogElement;
    expect(drawer.open).toBe(true);
    expect(within(drawer).getByRole("link", { name: "Stok" })).toBeDefined();

    await userEvent.click(within(drawer).getByRole("button", { name: "Menüyü kapat" }));
    expect(drawer.open).toBe(false);
    expect(open.getAttribute("aria-expanded")).toBe("false");
  });

  it("closes the drawer when a link is chosen", async () => {
    renderShell();
    await userEvent.click(screen.getByRole("button", { name: "Menüyü aç" }));
    const drawer = document.getElementById("mobile-nav") as HTMLDialogElement;
    await userEvent.click(within(drawer).getByRole("link", { name: "Stok" }));
    expect(drawer.open).toBe(false);
  });

  it("offers a skip link to the main content", () => {
    renderShell();
    expect(screen.getByRole("link", { name: "İçeriğe geç" }).getAttribute("href")).toBe("#main");
  });
});

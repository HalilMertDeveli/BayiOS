import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { AppShell } from "./app-shell";
import { NO_ACCESS } from "@/lib/session/access";
import { SessionContext, type SessionContextValue } from "@/lib/session/session-context";
import type { Membership } from "@/lib/session/active-tenant";
import { TEST_USER, accessFor, membership, mockSession } from "@/test/session";

const ownerMembership: Membership = membership("owner");

/** MOCK session: permissions as GET /me returns them for the role (src/test/me-fixtures.ts). */
function session(overrides: Partial<SessionContextValue> = {}): SessionContextValue {
  return mockSession("owner", overrides);
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

  it("builds the menu from the /me effective permissions", () => {
    renderShell("x", session({ role: "warehouse_staff", access: accessFor("warehouse_staff") }));
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
      state: { status: "ready", user: { id: "u-1", email: "a@b.test", fullName: null }, memberships: [ownerMembership, other], active: ownerMembership, access: accessFor("owner") },
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
    renderShell("Gizli içerik", session({ state: { status: "loading" }, role: null, access: NO_ACCESS }));
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

  it("replaces the page with the suspended/closed notice (tenant.inactive) and offers switch + sign-out", async () => {
    const other: Membership = { ...ownerMembership, tenantId: "t-2", tenantName: "Örnek LED" };
    const signOut = vi.fn().mockResolvedValue(undefined);
    renderShell(
      "Gizli içerik",
      session({ state: { status: "inactive", user: TEST_USER, memberships: [ownerMembership, other], tenant: ownerMembership }, role: null, access: NO_ACCESS, signOut }),
    );
    expect(screen.queryByText("Gizli içerik")).toBeNull();
    const notice = screen.getByRole("alert", { name: "İşletme kullanılamıyor" });
    expect(within(notice).getByRole("heading", { name: "Bu işletme askıya alınmış veya kapatılmış" })).toBeDefined();
    expect(notice.textContent).toContain("Örnek Motor");
    expect(within(notice).getByRole("button", { name: "İşletme değiştir" })).toBeDefined();
    await userEvent.click(within(notice).getByRole("button", { name: "Çıkış yap" }));
    expect(signOut).toHaveBeenCalled();
    expect(screen.queryByRole("navigation", { name: "Ana menü" })?.querySelectorAll("a").length ?? 0).toBe(0);
  });

  it("without another business the notice offers sign-out only", () => {
    renderShell("x", session({ state: { status: "inactive", user: TEST_USER, memberships: [ownerMembership], tenant: ownerMembership }, role: null, access: NO_ACCESS }));
    expect(screen.queryByRole("button", { name: "İşletme değiştir" })).toBeNull();
    expect(screen.getByRole("button", { name: "Çıkış yap" })).toBeDefined();
  });

  it("the mobile menu of a suspended/closed business is not empty: it explains and offers switch + sign-out (#111)", async () => {
    const other: Membership = { ...ownerMembership, tenantId: "t-2", tenantName: "Örnek LED" };
    const signOut = vi.fn().mockResolvedValue(undefined);
    renderShell(
      "x",
      session({ state: { status: "inactive", user: TEST_USER, memberships: [ownerMembership, other], tenant: ownerMembership }, role: null, access: NO_ACCESS, signOut }),
    );
    // Desktop sidebar: the explanation only (the page notice holds the buttons).
    const sidebar = document.getElementById("sidebar") as HTMLElement;
    expect(sidebar.textContent).toContain("askıya alınmış veya kapatılmış; bu işletmenin menüsü kullanılamıyor");
    expect(within(sidebar).queryByRole("button", { name: "Çıkış yap" })).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Menüyü aç" }));
    const drawer = document.getElementById("mobile-nav") as HTMLDialogElement;
    expect(drawer.textContent).toContain("Örnek Motor");
    const actions = within(drawer).getByRole("list", { name: "İşletme işlemleri" });
    expect(within(actions).getByRole("button", { name: "İşletme değiştir" })).toBeDefined();
    await userEvent.click(within(actions).getByRole("button", { name: "Çıkış yap" }));
    expect(signOut).toHaveBeenCalled();
  });

  it("the sidebar column stretches to the page height; only its inner panel is sticky (#111)", () => {
    renderShell();
    const sidebar = document.getElementById("sidebar") as HTMLElement;
    expect(sidebar.className).toContain("self-stretch");
    expect(sidebar.className).toContain("bg-card");
    expect(sidebar.className).not.toContain("h-dvh");
    expect((sidebar.firstElementChild as HTMLElement).className).toContain("sticky");
  });

  it("offers a skip link to the main content", () => {
    renderShell();
    expect(screen.getByRole("link", { name: "İçeriğe geç" }).getAttribute("href")).toBe("#main");
  });
});

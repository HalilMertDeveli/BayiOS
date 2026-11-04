import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppShell } from "./app-shell";

vi.mock("next/navigation", () => ({ usePathname: () => "/urunler" }));

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
    render(<AppShell>İçerik</AppShell>);
    const nav = screen.getByRole("navigation", { name: "Ana menü" });
    expect(within(nav).getByRole("link", { name: "Ürünler" }).getAttribute("aria-current")).toBe("page");
    expect(within(nav).getByRole("link", { name: "Pano" }).getAttribute("aria-current")).toBeNull();
    expect(screen.getByRole("main").textContent).toBe("İçerik");
  });

  it("does not pretend a user is logged in", () => {
    render(<AppShell>x</AppShell>);
    expect(screen.getByText("Giriş yapılmadı")).toBeDefined();
    expect(screen.getByText(/örnek rol/)).toBeDefined();
  });

  it("toggles the sidebar collapse state and persists it", async () => {
    render(<AppShell>x</AppShell>);
    await userEvent.click(screen.getByRole("button", { name: "Menüyü daralt" }));
    const expand = screen.getByRole("button", { name: "Menüyü genişlet" });
    expect(expand.getAttribute("aria-expanded")).toBe("false");
    expect(window.localStorage.getItem("bayios.sidebar.collapsed")).toBe("1");
    // Collapsed links keep their accessible names.
    expect(screen.getByRole("link", { name: "Siparişler" })).toBeDefined();
  });

  it("opens and closes the mobile drawer", async () => {
    render(<AppShell>x</AppShell>);
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
    render(<AppShell>x</AppShell>);
    await userEvent.click(screen.getByRole("button", { name: "Menüyü aç" }));
    const drawer = document.getElementById("mobile-nav") as HTMLDialogElement;
    await userEvent.click(within(drawer).getByRole("link", { name: "Stok" }));
    expect(drawer.open).toBe(false);
  });

  it("offers a skip link to the main content", () => {
    render(<AppShell>x</AppShell>);
    expect(screen.getByRole("link", { name: "İçeriğe geç" }).getAttribute("href")).toBe("#main");
  });
});

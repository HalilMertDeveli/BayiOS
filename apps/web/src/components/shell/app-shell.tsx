"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import Link from "next/link";
import { Menu, PanelLeftClose, PanelLeftOpen, X } from "lucide-react";
import { useModalDialog } from "@/components/ui/dialog";
import { cn } from "@/lib/cn";
import { homeFor, navForRole } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";
import { usePersistentBoolean } from "@/lib/use-persistent-boolean";
import { NavMenu } from "./nav-menu";
import { SessionGate } from "./session-gate";
import { UserMenu } from "./user-menu";

const MOBILE_NAV_ID = "mobile-nav";

function Brand({ compact = false }: { compact?: boolean }) {
  const { role } = useSession();
  return (
    <Link href={homeFor(role)} className="flex items-center gap-2 rounded-md text-h3 text-fg">
      <span
        aria-hidden="true"
        className="flex size-8 shrink-0 items-center justify-center rounded-md bg-brand text-caption font-bold text-on-brand"
      >
        B
      </span>
      <span className={cn(compact && "sr-only")}>BayiOS</span>
    </Link>
  );
}

/**
 * Application shell: persistent, collapsible sidebar from md (768 px) up; off-canvas drawer below.
 * Menu items follow the role of the active membership (GET /api/v1/me) — visibility only, the API authorizes.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const [collapsed, setCollapsed] = usePersistentBoolean("bayios.sidebar.collapsed");
  const [drawerOpen, setDrawerOpen] = useState(false);
  const drawerRef = useRef<HTMLDialogElement>(null);
  const closeDrawer = () => setDrawerOpen(false);
  useModalDialog(drawerRef, drawerOpen, closeDrawer);

  // The drawer is hidden from md up; never leave an invisible modal open after a resize/rotation.
  useEffect(() => {
    const desktop = window.matchMedia("(min-width: 48rem)");
    const onChange = () => {
      if (desktop.matches) setDrawerOpen(false);
    };
    desktop.addEventListener("change", onChange);
    return () => desktop.removeEventListener("change", onChange);
  }, []);

  const { role } = useSession();
  const sections = role ? navForRole(role) : [];

  return (
    <div className="flex min-h-dvh w-full">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-(--bos-z-toast) focus:rounded-md focus:bg-raised focus:px-4 focus:py-2 focus:shadow-e2"
      >
        İçeriğe geç
      </a>

      {/* Desktop / tablet sidebar */}
      <aside
        id="sidebar"
        className={cn(
          "sticky top-0 hidden h-dvh shrink-0 flex-col border-r border-line bg-card transition-[width] md:flex",
          collapsed ? "w-18" : "w-64",
        )}
      >
        <div className={cn("flex h-14 items-center border-b border-line px-4", collapsed && "justify-center px-0")}>
          <Brand compact={collapsed} />
        </div>
        <div className="flex-1 overflow-y-auto px-3 py-4">
          <NavMenu sections={sections} collapsed={collapsed} idPrefix="sidebar" />
        </div>
        <div className="border-t border-line p-3">
          <button
            type="button"
            onClick={() => setCollapsed(!collapsed)}
            aria-expanded={!collapsed}
            aria-controls="sidebar"
            className={cn(
              "flex min-h-10 w-full items-center gap-3 rounded-md px-3 text-body font-medium text-fg-secondary hover:bg-sunken hover:text-fg",
              collapsed && "justify-center px-0",
            )}
          >
            {collapsed ? <PanelLeftOpen className="size-5" /> : <PanelLeftClose className="size-5" />}
            <span className={cn(collapsed && "sr-only")}>{collapsed ? "Menüyü genişlet" : "Menüyü daralt"}</span>
          </button>
        </div>
      </aside>

      {/* Mobile drawer: native modal dialog = focus trap, Escape, inert background. */}
      <dialog
        ref={drawerRef}
        id={MOBILE_NAV_ID}
        aria-label="Menü"
        onClick={(event) => {
          if (event.target === event.currentTarget) closeDrawer();
        }}
        className="m-0 h-dvh max-h-none w-72 max-w-[85vw] border-r border-line bg-card p-0 text-fg shadow-e3 md:hidden"
      >
        {drawerOpen ? (
          <div className="flex h-full flex-col">
            <div className="flex h-14 items-center justify-between border-b border-line px-4">
              <Brand />
              <button
                type="button"
                onClick={closeDrawer}
                aria-label="Menüyü kapat"
                className="inline-flex size-11 items-center justify-center rounded-md text-fg-muted hover:bg-sunken hover:text-fg"
              >
                <X className="size-5" />
              </button>
            </div>
            <div className="flex-1 overflow-y-auto px-3 py-4">
              <NavMenu sections={sections} onNavigate={closeDrawer} idPrefix="drawer" />
            </div>
          </div>
        ) : null}
      </dialog>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-(--bos-z-sticky) flex h-14 items-center gap-3 border-b border-line bg-card px-4">
          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            aria-label="Menüyü aç"
            aria-expanded={drawerOpen}
            aria-controls={MOBILE_NAV_ID}
            className="-ml-2 inline-flex size-11 items-center justify-center rounded-md text-fg-secondary hover:bg-sunken hover:text-fg md:hidden"
          >
            <Menu className="size-5" />
          </button>
          <div className="md:hidden">
            <Brand />
          </div>
          <div className="ml-auto flex min-w-0 items-center gap-2">
            <UserMenu />
          </div>
        </header>
        <main id="main" tabIndex={-1} className="flex w-full flex-1 flex-col gap-6 px-4 py-6 focus:outline-none md:px-8">
          <SessionGate>{children}</SessionGate>
        </main>
      </div>
    </div>
  );
}

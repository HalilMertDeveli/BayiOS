"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { LogOut, Menu, PanelLeftClose, PanelLeftOpen, Repeat, X } from "lucide-react";
import { useModalDialog } from "@/components/ui/dialog";
import { ROUTES } from "@/lib/auth/routes";
import { cn } from "@/lib/cn";
import { homeFor, navFor } from "@/lib/navigation";
import { clearActiveTenantId } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";
import { usePersistentBoolean } from "@/lib/use-persistent-boolean";
import { TrialBanner } from "@/components/billing/trial-banner";
import { NavMenu } from "./nav-menu";
import { SessionGate } from "./session-gate";
import { UserMenu } from "./user-menu";

const MOBILE_NAV_ID = "mobile-nav";

function Brand({ compact = false }: { compact?: boolean }) {
  const { access } = useSession();
  return (
    <Link href={homeFor(access)} className="flex items-center gap-2 rounded-md text-h3 text-fg">
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
 * Shown instead of an empty menu (#111): while the session loads nothing; for a suspended/closed business (no
 * permissions) an explanation — in the mobile drawer also "switch business" and "sign out" (on the desktop the notice in
 * the page offers them); for a role with no menu item a short note.
 */
function EmptyNav({ onNavigate, withActions = false }: { onNavigate?: () => void; withActions?: boolean }) {
  const { state, signOut } = useSession();
  const router = useRouter();
  if (state.status === "loading" || state.status === "error") return null;
  if (state.status === "ready") {
    return <p className="px-3 text-caption text-fg-muted">Rolünüz için menüde gösterilecek bir bölüm yok. Gerekirse işletme yöneticinizle görüşün.</p>;
  }
  const others = state.memberships.some((m) => m.tenantId !== state.tenant.tenantId);
  const itemClass = "flex min-h-10 w-full items-center gap-3 rounded-md px-3 text-body font-medium text-fg-secondary hover:bg-sunken hover:text-fg";
  return (
    <div className="flex flex-col gap-3">
      <p className="px-3 text-caption text-fg-muted">
        <strong className="text-fg">{state.tenant.tenantName}</strong> askıya alınmış veya kapatılmış; bu işletmenin menüsü kullanılamıyor.
      </p>
      {withActions ? (
        <ul aria-label="İşletme işlemleri" className="flex flex-col gap-0.5">
          {others ? (
            <li>
              <button
                type="button"
                className={itemClass}
                onClick={() => {
                  clearActiveTenantId(state.user.id);
                  onNavigate?.();
                  router.replace(ROUTES.tenantPicker);
                }}
              >
                <Repeat className="size-5 shrink-0" aria-hidden="true" />
                İşletme değiştir
              </button>
            </li>
          ) : null}
          <li>
            <button type="button" className={itemClass} onClick={() => void signOut()}>
              <LogOut className="size-5 shrink-0" aria-hidden="true" />
              Çıkış yap
            </button>
          </li>
        </ul>
      ) : null}
    </div>
  );
}

/**
 * Application shell: persistent, collapsible sidebar from md (768 px) up; off-canvas drawer below.
 * Menu items follow the effective permissions of the active membership (GET /api/v1/me) — visibility only, the API
 * authorizes.
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

  const { access } = useSession();
  const sections = navFor(access);

  return (
    <div className="flex min-h-dvh w-full">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-(--bos-z-toast) focus:rounded-md focus:bg-raised focus:px-4 focus:py-2 focus:shadow-e2"
      >
        İçeriğe geç
      </a>

      {/* Desktop / tablet sidebar. The aside stretches to the full page height (background and border reach the bottom of
          long pages); the sticky panel inside keeps the menu in view while the page scrolls. */}
      <aside
        id="sidebar"
        className={cn("hidden shrink-0 self-stretch border-r border-line bg-card transition-[width] md:block", collapsed ? "w-18" : "w-64")}
      >
        <div className="sticky top-0 flex h-dvh flex-col">
          <div className={cn("flex h-14 items-center border-b border-line px-4", collapsed && "justify-center px-0")}>
            <Brand compact={collapsed} />
          </div>
          <div className="flex-1 overflow-y-auto px-3 py-4">
            {sections.length > 0 ? <NavMenu sections={sections} collapsed={collapsed} idPrefix="sidebar" /> : collapsed ? null : <EmptyNav />}
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
              {sections.length > 0 ? <NavMenu sections={sections} onNavigate={closeDrawer} idPrefix="drawer" /> : <EmptyNav onNavigate={closeDrawer} withActions />}
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
          <TrialBanner />
          <SessionGate>{children}</SessionGate>
        </main>
      </div>
    </div>
  );
}

"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { cn } from "@/lib/cn";
import { activeHref, type NavSection } from "@/lib/navigation";

export type NavMenuProps = {
  sections: readonly NavSection[];
  /** Icon-only rail: labels stay available to assistive tech and as a tooltip. */
  collapsed?: boolean;
  /** Called after a link is chosen (used to close the mobile drawer). */
  onNavigate?: () => void;
  /** Prefix for heading ids, so two menus can coexist in the DOM. */
  idPrefix: string;
};

export function NavMenu({ sections, collapsed = false, onNavigate, idPrefix }: NavMenuProps) {
  const pathname = usePathname();
  const current = activeHref(
    pathname,
    sections.flatMap((s) => s.items.map((i) => i.href)),
  );

  return (
    <nav aria-label="Ana menü" className={cn("flex flex-col", collapsed ? "gap-3" : "gap-5")}>
      {sections.map((section, index) => {
        const headingId = `${idPrefix}-${section.id}`;
        return (
          <div key={section.id} className={cn("flex flex-col gap-1", collapsed && index > 0 && "border-t border-dashed border-nav-line pt-3")}>
            <p
              id={headingId}
              className={cn("px-3 pb-1 text-body text-nav-fg-muted", collapsed && "sr-only")}
              lang="tr"
            >
              {section.label}
            </p>
            <ul aria-labelledby={headingId} className="flex flex-col gap-0.5">
              {section.items.map(({ href, label, Icon }) => {
                const active = href === current;
                return (
                  <li key={href}>
                    <Link
                      href={href}
                      aria-current={active ? "page" : undefined}
                      title={collapsed ? label : undefined}
                      onClick={onNavigate}
                      className={cn(
                        // Active (UI kit): lighter panel, white label and a heavier weight (not colour alone).
                        "relative flex min-h-10 items-center gap-3 rounded-lg px-3 text-body transition-colors",
                        collapsed && "justify-center px-0",
                        active
                          ? "bg-nav-active font-semibold text-nav-fg-active"
                          : "text-nav-fg hover:bg-nav-hover hover:text-nav-fg-active",
                      )}
                    >
                      <Icon className="size-5 shrink-0" aria-hidden="true" />
                      <span className={cn(collapsed && "sr-only")}>{label}</span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}

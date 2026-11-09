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
    <nav aria-label="Ana menü" className="flex flex-col gap-5">
      {sections.map((section) => {
        const headingId = `${idPrefix}-${section.id}`;
        return (
          <div key={section.id} className="flex flex-col gap-1">
            <p
              id={headingId}
              className={cn("px-3 text-caption font-semibold tracking-wide text-fg-muted uppercase", collapsed && "sr-only")}
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
                        "flex min-h-10 items-center gap-3 rounded-md px-3 text-body font-medium transition-colors",
                        collapsed && "justify-center px-0",
                        active ? "bg-brand-subtle text-brand-fg" : "text-fg-secondary hover:bg-sunken hover:text-fg",
                      )}
                    >
                      <Icon className="size-5 shrink-0" />
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

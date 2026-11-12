"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight } from "@/components/ui/icons";
import { breadcrumbsFor, type NavSection } from "@/lib/navigation";

/** Header breadcrumbs (menu section › page › sub-page) derived from the menu; hidden on pages outside it. */
export function Breadcrumbs({ sections, className }: { sections: readonly NavSection[]; className?: string }) {
  const pathname = usePathname();
  const crumbs = breadcrumbsFor(pathname, sections);
  if (crumbs.length === 0) return null;
  return (
    <nav aria-label="Konum" className={className}>
      <ol className="flex min-w-0 items-center gap-1 text-body">
        {crumbs.map((crumb, index) => {
          const last = index === crumbs.length - 1;
          return (
            <li key={`${index}-${crumb.label}`} className="flex min-w-0 items-center gap-1">
              {index > 0 ? <ChevronRight className="size-4 shrink-0 text-fg-muted" aria-hidden="true" /> : null}
              {crumb.href ? (
                <Link href={crumb.href} className="block truncate rounded-sm py-0.5 text-fg-muted underline-offset-2 hover:text-fg hover:underline">
                  {crumb.label}
                </Link>
              ) : (
                <span className={last ? "truncate font-medium text-fg" : "truncate text-fg-muted"} aria-current={last ? "page" : undefined}>
                  {crumb.label}
                </span>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

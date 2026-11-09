import type { ReactNode } from "react";
import { Inbox } from "lucide-react";
import { cn } from "@/lib/cn";

export type EmptyStateProps = {
  title: string;
  description?: ReactNode;
  /** Decorative icon; defaults to an inbox. */
  icon?: ReactNode;
  /** Primary call to action, e.g. a Button or link. */
  action?: ReactNode;
  /** Heading level so the state fits the page outline. */
  headingLevel?: "h2" | "h3";
  className?: string;
};

export function EmptyState({
  title,
  description,
  icon,
  action,
  headingLevel: Heading = "h2",
  className,
}: EmptyStateProps) {
  return (
    <div className={cn("flex flex-col items-center gap-3 px-6 py-12 text-center", className)}>
      <div className="flex size-14 items-center justify-center rounded-2xl bg-brand-subtle text-brand-fg ring-8 ring-brand-subtle/40" aria-hidden="true">
        {icon ?? <Inbox className="size-6" />}
      </div>
      <Heading className="text-h3 text-fg">{title}</Heading>
      {description ? <div className="max-w-md text-body text-fg-muted">{description}</div> : null}
      {action ? <div className="mt-2 flex flex-wrap justify-center gap-2">{action}</div> : null}
    </div>
  );
}

import type { ReactNode } from "react";

export type PageHeaderProps = {
  title: string;
  description?: ReactNode;
  /** Page-level actions. Keep a single primary action. */
  actions?: ReactNode;
};

/**
 * Page title, one-line description and the page's actions. From sm up the actions sit on the title row (right-aligned,
 * wrapping onto further rows when they don't fit); below sm they follow the description. The header breadcrumbs (app
 * shell) carry the location, so the title stays short.
 */
export function PageHeader({ title, description, actions }: PageHeaderProps) {
  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-6">
      <div className="flex min-w-0 flex-col gap-1">
        <h1 className="text-h1 tracking-tight text-fg">{title}</h1>
        {description ? <p className="max-w-3xl text-body text-fg-muted">{description}</p> : null}
      </div>
      {actions ? <div className="flex flex-wrap items-center gap-2 sm:max-w-[60%] sm:shrink-0 sm:justify-end">{actions}</div> : null}
    </div>
  );
}

import type { ReactNode } from "react";
import { cn } from "@/lib/cn";

/** Shared label / hint / error layout for form controls (Input, Select). */
export type FieldBaseProps = {
  /** Visible label — required: placeholders are not labels. */
  label: string;
  /** Helper text shown under the control. */
  hint?: ReactNode;
  /** Error text ("ne oldu + ne yapmalı"). Sets aria-invalid on the control. */
  error?: ReactNode;
  /** Hides the label visually but keeps it for assistive tech. */
  hideLabel?: boolean;
};

export function fieldDescribedBy(id: string, hint: unknown, error: unknown): string | undefined {
  const ids = [error ? `${id}-error` : null, hint ? `${id}-hint` : null].filter(Boolean);
  return ids.length ? ids.join(" ") : undefined;
}

export const controlClasses = cn(
  "w-full rounded-md border bg-card text-fg text-body shadow-e1 placeholder:text-fg-muted transition-colors",
  "focus-visible:border-focus focus-visible:ring-4 focus-visible:ring-brand-subtle focus-visible:outline-none",
  "disabled:cursor-not-allowed disabled:bg-sunken disabled:text-fg-muted",
);

export function Field({
  id,
  label,
  hint,
  error,
  hideLabel,
  required,
  className,
  children,
}: FieldBaseProps & { id: string; required?: boolean; className?: string; children: ReactNode }) {
  return (
    <div className={cn("flex flex-col gap-1.5", className)}>
      <label htmlFor={id} className={cn("text-body font-medium text-fg", hideLabel && "sr-only")}>
        {label}
        {required && (
          <span className="text-danger-fg" aria-hidden="true">
            {" "}
            *
          </span>
        )}
      </label>
      {children}
      {error ? (
        <p id={`${id}-error`} className="text-caption text-danger-fg">
          {error}
        </p>
      ) : null}
      {hint ? (
        <p id={`${id}-hint`} className="text-caption text-fg-muted">
          {hint}
        </p>
      ) : null}
    </div>
  );
}

import { cn } from "@/lib/cn";

/**
 * The BayiOS mark: petrol tile with the copper accent bar (docs/design/brand-colors.md). Decorative — the visible or
 * screen-reader "BayiOS" text next to it carries the name, so the mark itself is hidden from assistive technology.
 */
export function BrandMark({ className }: { className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        "relative flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-md bg-brand text-caption font-bold text-on-brand",
        className,
      )}
    >
      B
      <span className="absolute inset-x-0 bottom-0 h-1 bg-accent" />
    </span>
  );
}

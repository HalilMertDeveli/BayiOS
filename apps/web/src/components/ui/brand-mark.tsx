import { cn } from "@/lib/cn";

/**
 * The BayiOS placeholder mark (#222): green tile with the initial, until the owner supplies a logo (the UI kit's own
 * logo is not used). Decorative — the visible or
 * screen-reader "BayiOS" text next to it carries the name, so the mark itself is hidden from assistive technology.
 */
export function BrandMark({ className }: { className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        "relative flex size-9 shrink-0 items-center justify-center overflow-hidden rounded-lg bg-linear-to-b from-accent to-brand text-body font-bold text-on-brand shadow-button",
        className,
      )}
    >
      B
    </span>
  );
}

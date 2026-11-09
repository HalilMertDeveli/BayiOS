import { cn } from "@/lib/cn";

/** A decorative placeholder bar; pair it with a visually hidden "Yükleniyor…" status for assistive technology. */
export function Skeleton({ className }: { className?: string }) {
  return <span aria-hidden="true" className={cn("block h-4 animate-pulse rounded-sm bg-sunken", className)} />;
}

/**
 * Card-list placeholder for the mobile layout of list pages (the desktop table has its own skeleton rows). Announces
 * "Yükleniyor…" once through a polite status region.
 */
export function SkeletonCards({ count = 3, label = "Yükleniyor…" }: { count?: number; label?: string }) {
  return (
    <div className="flex flex-col gap-2">
      <p role="status" className="sr-only">
        {label}
      </p>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} aria-hidden="true" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-3 shadow-e1">
          <div className="flex items-center justify-between gap-3">
            <Skeleton className="w-32" />
            <Skeleton className="w-16" />
          </div>
          <Skeleton className="h-5 w-24 rounded-full" />
          <Skeleton className="w-2/3" />
          <Skeleton className="w-1/2" />
        </div>
      ))}
    </div>
  );
}

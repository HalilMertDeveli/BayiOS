import { LoaderCircle } from "lucide-react";

/**
 * Static stand-in for the app shell while the pathname is not known yet (prerendered HTML of dynamic routes).
 * No menu items: they depend on the session's role, which is only known in the browser.
 */
export function ShellFallback() {
  return (
    <div className="flex min-h-dvh w-full">
      <div aria-hidden="true" className="sticky top-0 hidden h-dvh w-64 shrink-0 border-r border-line bg-card md:block" />
      <div className="flex min-w-0 flex-1 flex-col">
        <div aria-hidden="true" className="h-14 border-b border-line bg-card" />
        <main id="main" tabIndex={-1} className="flex w-full flex-1 flex-col gap-6 px-4 py-6 focus:outline-none md:px-8">
          <p role="status" className="flex items-center gap-2 text-body text-fg-muted">
            <LoaderCircle className="size-4 animate-spin" aria-hidden="true" />
            Yükleniyor…
          </p>
        </main>
      </div>
    </div>
  );
}

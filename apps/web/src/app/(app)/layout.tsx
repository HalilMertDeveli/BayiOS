import { Suspense } from "react";
import { AppShell } from "@/components/shell/app-shell";
import { ShellFallback } from "@/components/shell/shell-fallback";
import { ToastProvider } from "@/components/ui/toast";
import { SessionProvider } from "@/lib/session/session-context";

/**
 * The session layer and the shell read the pathname (usePathname). On static routes it is known at build time;
 * on dynamic segments without generateStaticParams (e.g. /urunler/[id]) it is request data and suspends, so the
 * shell sits behind this boundary (Cache Components). Static routes prerender exactly as before.
 */
export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <ToastProvider>
      <Suspense fallback={<ShellFallback />}>
        <SessionProvider require="tenant">
          <AppShell>{children}</AppShell>
        </SessionProvider>
      </Suspense>
    </ToastProvider>
  );
}

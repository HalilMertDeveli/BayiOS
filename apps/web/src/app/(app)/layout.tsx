import { AppShell } from "@/components/shell/app-shell";
import { ToastProvider } from "@/components/ui/toast";
import { SessionProvider } from "@/lib/session/session-context";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <ToastProvider>
      <SessionProvider require="tenant">
        <AppShell>{children}</AppShell>
      </SessionProvider>
    </ToastProvider>
  );
}

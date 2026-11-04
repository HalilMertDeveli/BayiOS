import { AuthLayout } from "@/components/auth/auth-layout";
import { ToastProvider } from "@/components/ui/toast";
import { SessionProvider } from "@/lib/session/session-context";

/** Signed-in pages that do not need an active tenant: onboarding and the tenant picker. */
export default function AccountLayout({ children }: { children: React.ReactNode }) {
  return (
    <ToastProvider>
      <SessionProvider require="user">
        <AuthLayout wide>{children}</AuthLayout>
      </SessionProvider>
    </ToastProvider>
  );
}

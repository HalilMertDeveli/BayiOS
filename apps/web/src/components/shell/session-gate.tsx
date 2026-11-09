"use client";

import { Fragment, useState, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { Ban, CircleAlert, LoaderCircle, LogOut, Repeat } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { ROUTES } from "@/lib/auth/routes";
import { clearActiveTenantId } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";

/**
 * Renders the page only once the session and the active tenant are known. Re-mounts the page when
 * the active tenant changes so no data from the previous tenant stays on screen.
 */
export function SessionGate({ children }: { children: ReactNode }) {
  const { state, reload } = useSession();
  const [retrying, setRetrying] = useState(false);

  if (state.status === "ready" && state.active) {
    return <Fragment key={state.active.tenantId}>{children}</Fragment>;
  }

  if (state.status === "inactive") return <TenantInactive />;

  if (state.status === "error") {
    return (
      <section role="alert" className="rounded-lg border border-line bg-card shadow-e1">
        <EmptyState
          icon={<CircleAlert className="size-6" />}
          title="Oturum bilgileri yüklenemedi"
          description={state.message}
          action={
            <Button
              loading={retrying}
              onClick={async () => {
                setRetrying(true);
                await reload();
                setRetrying(false);
              }}
            >
              Tekrar dene
            </Button>
          }
        />
      </section>
    );
  }

  return (
    <p role="status" className="flex items-center gap-2 text-body text-fg-muted">
      <LoaderCircle className="size-4 animate-spin" aria-hidden="true" />
      Oturum yükleniyor…
    </p>
  );
}

/**
 * 403 `tenant.inactive`: the selected business is suspended or closed. Nothing of it is shown; the user can switch to
 * another business (when they have one) or sign out. The API refuses every request for it, so this is not a lock.
 */
function TenantInactive() {
  const { state, signOut } = useSession();
  const router = useRouter();
  const [signingOut, setSigningOut] = useState(false);
  if (state.status !== "inactive") return null;
  const others = state.memberships.filter((m) => m.tenantId !== state.tenant.tenantId);

  return (
    <section role="alert" aria-label="İşletme kullanılamıyor" className="rounded-lg border border-line bg-card shadow-e1">
      <EmptyState
        icon={<Ban className="size-6" />}
        title="Bu işletme askıya alınmış veya kapatılmış"
        description={
          <>
            <p>
              <strong>{state.tenant.tenantName}</strong> şu anda kullanılamıyor; bu işletmenin kayıtları görüntülenemez ve işlem yapılamaz.
            </p>
            <p className="mt-1">Hesabın yeniden açılması için işletme sahibiyle veya BayiOS destek ekibiyle görüşün.</p>
          </>
        }
        action={
          <div className="flex flex-wrap justify-center gap-2">
            {others.length > 0 ? (
              <Button
                icon={<Repeat className="size-4" />}
                onClick={() => {
                  clearActiveTenantId(state.user.id);
                  router.replace(ROUTES.tenantPicker);
                }}
              >
                İşletme değiştir
              </Button>
            ) : null}
            <Button
              variant={others.length > 0 ? "secondary" : "primary"}
              icon={<LogOut className="size-4" />}
              loading={signingOut}
              onClick={async () => {
                setSigningOut(true);
                await signOut();
              }}
            >
              Çıkış yap
            </Button>
          </div>
        }
      />
    </section>
  );
}

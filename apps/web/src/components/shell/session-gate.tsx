"use client";

import { Fragment, useState, type ReactNode } from "react";
import { CircleAlert, LoaderCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
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

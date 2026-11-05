"use client";

import { useEffect } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Building2, ChevronRight, Plus } from "lucide-react";
import { FormMessage } from "@/components/auth/auth-layout";
import { Badge } from "@/components/ui/badge";
import { buttonClasses } from "@/components/ui/button";
import { ROUTES } from "@/lib/auth/routes";
import { roleLabels } from "@/lib/navigation";
import { cn } from "@/lib/cn";
import { tenantStatusLabel, toRole } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";

/** Shown to users with several memberships, and after the API answers 403 tenant.access_denied. */
export function TenantPicker() {
  const router = useRouter();
  const { state, selectTenant, signOut } = useSession();

  // Nothing to pick: no membership yet → onboarding.
  const empty = state.status === "ready" && state.memberships.length === 0;
  useEffect(() => {
    if (empty) router.replace(ROUTES.onboarding);
  }, [empty, router]);

  if (state.status === "loading" || empty) {
    return <p role="status" className="text-body text-fg-muted">Oturum yükleniyor…</p>;
  }
  if (state.status === "error") {
    return <FormMessage tone="error">{state.message}</FormMessage>;
  }

  return (
    <div className="flex flex-col gap-4">
      <ul className="flex flex-col gap-2" aria-label="İşletmeleriniz">
        {state.memberships.map((m) => {
          // Suspended / closed business (#111): greyed with its status; choosing it opens the "not available" notice.
          const inactive = tenantStatusLabel(m);
          const statusId = `tenant-status-${m.tenantId}`;
          return (
            <li key={m.tenantId}>
              <button
                type="button"
                aria-describedby={inactive ? statusId : undefined}
                onClick={() => {
                  selectTenant(m.tenantId);
                  router.replace(ROUTES.home);
                }}
                className={cn(
                  "flex w-full items-center gap-3 rounded-lg border p-4 text-left transition-colors hover:bg-sunken",
                  inactive ? "border-line bg-sunken" : "border-line-strong",
                )}
              >
                <Building2 className="size-5 shrink-0 text-fg-muted" aria-hidden="true" />
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className={cn("truncate text-body font-medium", inactive ? "text-fg-muted" : "text-fg")}>{m.tenantName}</span>
                  <span className="text-caption text-fg-muted">{roleLabels[toRole(m.role)]}</span>
                </span>
                {inactive ? (
                  <span id={statusId}>
                    <Badge tone="neutral">{inactive}</Badge>
                    <span className="sr-only"> — bu işletme şu anda kullanılamıyor</span>
                  </span>
                ) : null}
                <ChevronRight className="size-4 text-fg-muted" aria-hidden="true" />
              </button>
            </li>
          );
        })}
      </ul>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Link href={ROUTES.onboarding} className={buttonClasses("secondary", "md")}>
          <Plus className="size-4" aria-hidden="true" />
          Yeni işletme oluştur
        </Link>
        <button type="button" onClick={() => void signOut()} className="text-body text-fg-muted underline-offset-2 hover:underline">
          Çıkış yap
        </button>
      </div>
    </div>
  );
}

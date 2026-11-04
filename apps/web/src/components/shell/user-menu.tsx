"use client";

import { useState } from "react";
import Link from "next/link";
import { LogOut } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { ROUTES } from "@/lib/auth/routes";
import { roleLabels } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";

/** Header: active tenant (switcher when the user has several), signed-in user, role and sign-out. */
export function UserMenu() {
  const { state, role, selectTenant, signOut } = useSession();
  const [signingOut, setSigningOut] = useState(false);

  if (state.status !== "ready") return null;
  const { user, memberships, active } = state;
  const displayName = user.fullName || user.email || "Kullanıcı";

  return (
    <div className="flex min-w-0 items-center gap-3">
      {memberships.length > 1 && active ? (
        <Select
          label="Aktif işletme"
          hideLabel
          value={active.tenantId}
          onChange={(event) => selectTenant(event.target.value)}
          options={memberships.map((m) => ({ value: m.tenantId, label: m.tenantName }))}
          className="w-40 sm:w-56"
        />
      ) : active ? (
        <p className="hidden truncate text-body font-medium text-fg sm:block" title="Aktif işletme">
          {active.tenantName}
        </p>
      ) : (
        <Link href={ROUTES.tenantPicker} className="text-body font-medium text-brand-fg underline">
          İşletme seç
        </Link>
      )}
      <div className="hidden min-w-0 flex-col text-right md:flex">
        <span className="truncate text-body text-fg" data-testid="session-user">
          {displayName}
        </span>
        {role ? <span className="text-caption text-fg-muted">{roleLabels[role]}</span> : null}
      </div>
      <Button
        variant="ghost"
        size="sm"
        loading={signingOut}
        icon={<LogOut className="size-4" />}
        onClick={async () => {
          setSigningOut(true);
          await signOut();
        }}
        aria-label="Çıkış yap"
      >
        <span className="hidden sm:inline">Çıkış yap</span>
      </Button>
    </div>
  );
}

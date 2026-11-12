"use client";

import { useState } from "react";
import Link from "next/link";
import { LogOut } from "@/components/ui/icons";
import { Avatar } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { ROUTES } from "@/lib/auth/routes";
import { roleLabels } from "@/lib/navigation";
import { tenantStatusLabel } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";

export { initials } from "@/lib/initials";

/** Header: active tenant (switcher when the user has several), signed-in user, role and sign-out. */
export function UserMenu() {
  const { state, role, selectTenant, signOut } = useSession();
  const [signingOut, setSigningOut] = useState(false);

  if (state.status !== "ready") return null; // the tenant-inactive notice offers switch / sign-out itself
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
          options={memberships.map((m) => {
            const inactive = tenantStatusLabel(m);
            return { value: m.tenantId, label: inactive ? `${m.tenantName} (${inactive.toLocaleLowerCase("tr")})` : m.tenantName };
          })}
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
      <div className="hidden min-w-0 items-center gap-2 md:flex">
        <div className="flex min-w-0 flex-col text-right">
          <span className="truncate text-body font-medium text-fg" data-testid="session-user">
            {displayName}
          </span>
          {role ? <span className="text-caption text-fg-muted">{roleLabels[role]}</span> : null}
        </div>
        <Avatar name={displayName} size="md" />
      </div>
      <Button
        variant="secondary"
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

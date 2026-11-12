"use client";

import { useCallback, useState, useSyncExternalStore } from "react";
import Link from "next/link";
import { Clock, X } from "@/components/ui/icons";
import { PLAN_PAGE_HREF, planLabel } from "@/lib/billing/labels";
import { daysLeftText, trialNotice } from "@/lib/billing/trial";
import { useSession } from "@/lib/session/session-context";

const listeners = new Set<() => void>();
const memory = new Set<string>();

function isDismissed(key: string): boolean {
  try {
    return window.sessionStorage.getItem(key) === "1";
  } catch {
    return memory.has(key);
  }
}

function dismiss(key: string) {
  try {
    window.sessionStorage.setItem(key, "1");
  } catch {
    memory.add(key);
  }
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

const dateFormat = new Intl.DateTimeFormat("tr-TR", { dateStyle: "long", timeZone: "Europe/Istanbul" });

/**
 * Shell banner when the free trial ends within a few days (or has ended). Dismissible for this browser session
 * (sessionStorage, per business and notice). Free trial — no payment is taken; data is never deleted.
 */
export function TrialBanner() {
  const { state, entitlements, access } = useSession();
  const [now] = useState(() => new Date());
  const notice = trialNotice(entitlements, now);
  const tenantId = state.status === "ready" ? (state.active?.tenantId ?? "") : "";
  const key = notice ? `bayios.trialBanner.${tenantId}.${notice.kind}` : "";
  const dismissed = useSyncExternalStore(
    subscribe,
    () => (key ? isDismissed(key) : true),
    () => true,
  );
  const close = useCallback(() => dismiss(key), [key]);
  if (!notice || dismissed) return null;

  const link = access.can("billing.read") ? (
    <Link href={PLAN_PAGE_HREF} className="font-medium underline underline-offset-2">
      Paket ve modüller
    </Link>
  ) : null;

  return (
    <section aria-label="Deneme süresi" className="flex items-start gap-3 rounded-md border border-warning-border bg-warning-bg px-4 py-3 text-body text-warning-fg">
      <Clock className="mt-0.5 size-5 shrink-0" aria-hidden="true" />
      <p className="flex-1">
        {notice.kind === "ending" ? (
          <>
            Ücretsiz deneme süreniz {notice.daysLeft <= 0 ? "bugün bitiyor" : `${daysLeftText(notice.daysLeft)} sonra bitiyor`} (
            {dateFormat.format(new Date(notice.trialEndsAt))}). Ödeme alınmaz; süre bitince işletmeniz {planLabel("basic")} pakete geçer ve
            verileriniz silinmez.
          </>
        ) : (
          <>
            Ücretsiz deneme süreniz sona erdi; işletmeniz {planLabel("basic")} pakette. Verileriniz silinmedi, temel işler sürüyor.
          </>
        )}{" "}
        {link}
      </p>
      <button
        type="button"
        onClick={close}
        aria-label="Deneme bildirimini kapat"
        className="-my-1 -mr-2 inline-flex size-9 shrink-0 items-center justify-center rounded-md hover:bg-warning-border/40"
      >
        <X className="size-4" aria-hidden="true" />
      </button>
    </section>
  );
}

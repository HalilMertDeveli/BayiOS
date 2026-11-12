"use client";

import Link from "next/link";
import { Lock } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";
import type { Gate } from "@/lib/billing/entitlements";
import { PLAN_PAGE_HREF, featureLabel, limitLabel, moduleLabel, planLabel } from "@/lib/billing/labels";
import { cn } from "@/lib/cn";
import { useSession } from "@/lib/session/session-context";

type LockedGate = Extract<Gate, { locked: true }>;

/** "Profesyonel pakette" / "AI WhatsApp asistanı modülünde" — the short badge text of a locked gate. */
export function gateBadgeText(gate: LockedGate): string {
  if (gate.suggestedPlan) return `${planLabel(gate.suggestedPlan)} pakette`;
  if (gate.suggestedModule) return `${moduleLabel(gate.suggestedModule)} modülünde`;
  return "Paketinizde yok";
}

/** The sentence under a locked gate. */
export function gateDescription(gate: LockedGate): string {
  if (gate.kind === "limit") {
    const limit = gate.limit ?? 0;
    return `${limitLabel(gate.code)} sınırı paketinizde ${limit.toLocaleString("tr-TR")}; şu an ${(gate.current ?? 0).toLocaleString("tr-TR")} kullanılıyor.`;
  }
  return `${featureLabel(gate.code)} mevcut paketinizde yok.`;
}

/**
 * Locked state of an entitlement-gated action (#51/#52): what is missing, where it is available, and — for owner/admin —
 * a link to the plan page. Existing data is never hidden behind it; only new actions are locked. Visibility only: the
 * API enforces the entitlement again (403 entitlement.*).
 */
export function LockedFeature({ gate, title, className, compact = false }: { gate: LockedGate; title?: string; className?: string; compact?: boolean }) {
  const { access } = useSession();
  const canSeePlans = access.can("billing.read");
  return (
    <div role="note" className={cn("flex gap-3 rounded-md border border-line bg-sunken px-4 py-3 text-body", className)}>
      <Lock className="mt-0.5 size-5 shrink-0 text-fg-muted" aria-hidden="true" />
      <div className="flex flex-1 flex-col gap-1">
        <p className="flex flex-wrap items-center gap-2">
          {title ? <span className="font-semibold text-fg">{title}</span> : null}
          <Badge tone="brand">{gateBadgeText(gate)}</Badge>
        </p>
        <p className="text-fg-secondary">
          {gateDescription(gate)}
          {compact ? null : " Mevcut kayıtlarınız görünmeye devam eder."}
        </p>
        {canSeePlans ? (
          <Link href={PLAN_PAGE_HREF} className="inline-flex min-h-6 w-fit items-center text-body text-brand-fg underline underline-offset-2">
            Paket ve modülleri gör
          </Link>
        ) : (
          <p className="text-caption text-fg-muted">Paket değişikliği için işletme sahibiyle görüşün.</p>
        )}
      </div>
    </div>
  );
}

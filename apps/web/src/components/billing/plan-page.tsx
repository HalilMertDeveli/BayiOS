"use client";

import { useCallback, useEffect, useRef, useState, useId } from "react";
import Link from "next/link";
import { ArrowLeft, Check, CircleAlert, Gift, Lock, PackagePlus, Puzzle } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge, type BadgeTone } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/ui/toast";
import { activateModule, deactivateModule, getModules, getPlans, getSubscription, getUsage, type BillingCatalog, type Subscription, type TenantModule, type Usage } from "@/lib/billing/api";
import { billingErrorMessage, billingPermissions, isStaleModuleState } from "@/lib/billing/errors";
import { featureLabel, limitLabel, moduleStatusLabels, planLabel, subscriptionStatusLabels, type ModuleStatus } from "@/lib/billing/labels";
import { trialDaysLeft } from "@/lib/billing/trial";
import { cn } from "@/lib/cn";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";

type Data = { catalog: BillingCatalog; subscription: Subscription; modules: TenantModule[]; usage: Usage };
type LoadState = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: Data };

const dateFormat = new Intl.DateTimeFormat("tr-TR", { dateStyle: "long", timeZone: "Europe/Istanbul" });
const formatDate = (iso: string | null | undefined) => (iso ? dateFormat.format(new Date(iso)) : "—");
const num = (value: number | string | null | undefined) => (value === null || value === undefined ? null : Number(value));

const statusTone: Record<Subscription["status"], BadgeTone> = { trialing: "info", active: "success", expired: "warning", cancelled: "neutral" };
const moduleTone: Record<ModuleStatus, BadgeTone> = {
  included_in_trial: "info",
  trialing: "info",
  active: "success",
  expired: "warning",
  cancelled: "neutral",
  available: "brand",
  unavailable: "neutral",
};

function BackLink() {
  return (
    <Link href="/ayarlar" className="inline-flex min-h-6 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Ayarlar
    </Link>
  );
}

/**
 * /ayarlar/paket (#51/#52): current plan and trial, usage vs limits, the plan catalog and the add-on store. Reading is
 * billing.read (owner, admin); activating/deactivating add-ons is billing.manage (owner). No payment anywhere: add-on
 * activation is a free, time-limited trial, once per module; plan changes are not in the API.
 */
export function PlanPage() {
  const { access } = useSession();
  const permissions = billingPermissions(access);
  if (!permissions.read) {
    return (
      <>
        <BackLink />
        <PageHeader title="Paket ve modüller" />
        <section aria-label="Paket ve modüller durumu" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Paket bilgilerini görme yetkiniz yok"
            description="Paket, kullanım ve modülleri işletme sahibi ve yöneticiler görebilir."
          />
        </section>
      </>
    );
  }
  return <PlanPageView canManage={permissions.manage} />;
}

function PlanPageView({ canManage }: { canManage: boolean }) {
  const { api, reload: reloadSession } = useSession();
  const [state, setState] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    Promise.all([getPlans(api, signal), getSubscription(api, signal), getModules(api, signal), getUsage(api, signal)])
      .then(([catalog, subscription, modules, usage]) => {
        if (!signal.aborted) setState({ status: "ready", data: { catalog, subscription, modules, usage } });
      })
      .catch((error: unknown) => {
        if (!signal.aborted) setState({ status: "error", message: billingErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, attempt]);

  const refresh = useCallback(() => setAttempt((n) => n + 1), []);
  /** After a store change: reload this page and /me (the entitlements behind every gate). */
  const afterChange = useCallback(() => {
    refresh();
    void reloadSession();
  }, [refresh, reloadSession]);

  return (
    <>
      <BackLink />
      <PageHeader title="Paket ve modüller" description="Mevcut paketiniz, kullanımınız ve ek modüller." />
      <FormAlert tone="info" title="Ücretsiz deneme — ödeme yok">
        BayiOS şu an ücretsiz deneme sürecinde; kart bilgisi istenmez, hiçbir işlemde ödeme alınmaz. Paket veya modül süresi bitince verileriniz
        silinmez, yalnızca ilgili yeni işlemler kapanır.
      </FormAlert>
      {state.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Paket bilgileri yükleniyor…
        </p>
      ) : state.status === "error" ? (
        <div role="alert" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState
            headingLevel="h2"
            icon={<CircleAlert className="size-6" />}
            title="Paket bilgileri yüklenemedi"
            description={state.message}
            action={<Button onClick={refresh}>Tekrar dene</Button>}
          />
        </div>
      ) : (
        <>
          <CurrentPlan subscription={state.data.subscription} />
          <UsageSection usage={state.data.usage} />
          <ModuleStore data={state.data} canManage={canManage} onChanged={afterChange} />
          <PlanCatalog catalog={state.data.catalog} current={state.data.subscription.plan} />
        </>
      )}
    </>
  );
}

function CurrentPlan({ subscription }: { subscription: Subscription }) {
  const uid = useId();
  const daysLeft = subscription.status === "trialing" ? trialDaysLeft(subscription.trialEndsAt) : null;
  return (
    <section aria-labelledby={`${uid}-current-plan`} className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4 shadow-e1 sm:p-6">
      <h2 id={`${uid}-current-plan`} className="text-h3 text-fg">
        Mevcut paket
      </h2>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-h2 text-fg">{subscription.planName || planLabel(subscription.plan)}</span>
        <Badge tone={statusTone[subscription.status]}>{subscriptionStatusLabels[subscription.status]}</Badge>
      </div>
      {subscription.status === "trialing" && subscription.trialEndsAt ? (
        <p className="text-body text-fg-secondary">
          Deneme bitişi: <strong className="text-fg">{formatDate(subscription.trialEndsAt)}</strong>
          {daysLeft !== null ? ` · ${daysLeft <= 0 ? "bugün bitiyor" : `${daysLeft} gün kaldı`}` : ""}. Deneme bitince işletmeniz {planLabel("basic")} pakete
          geçer; verileriniz silinmez.
        </p>
      ) : null}
      {subscription.status === "expired" ? (
        <p className="text-body text-fg-secondary">
          Deneme veya abonelik süresi doldu; işletmeniz {planLabel("basic")} paket özellikleriyle çalışıyor. Verileriniz silinmedi; Profesyonel
          yapılandırmalar salt okunur kaldı.
        </p>
      ) : null}
      <p className="text-caption text-fg-muted">
        Paket değişikliği (Temel ↔ Profesyonel) şu an uygulama içinden yapılamıyor; ödeme altyapısı hazır olduğunda burada açılacak.
      </p>
    </section>
  );
}

function UsageSection({ usage }: { usage: Usage }) {
  const uid = useId();
  return (
    <section aria-labelledby={`${uid}-usage`} className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4 shadow-e1 sm:p-6">
      <h2 id={`${uid}-usage`} className="text-h3 text-fg">
        Kullanım ve sınırlar
      </h2>
      {usage.items.length === 0 ? (
        <p className="text-body text-fg-muted">Paketinizde sınırlı bir kullanım yok.</p>
      ) : (
        <ul className="flex flex-col gap-4">
          {usage.items.map((item) => {
            const limit = num(item.limit);
            const current = num(item.current) ?? 0;
            const ratio = limit && limit > 0 ? Math.min(1, current / limit) : 0;
            const full = limit !== null && current >= limit;
            return (
              <li key={item.code} className="flex flex-col gap-1">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <span className="font-medium text-fg">
                    {limitLabel(item.code)}
                    {item.period === "month" ? <span className="font-normal text-fg-muted"> (bu ay)</span> : null}
                  </span>
                  <span className={cn("tabular-nums", full ? "font-semibold text-danger-fg" : "text-fg-secondary")}>
                    {current.toLocaleString("tr-TR")} / {limit === null ? "sınırsız" : limit.toLocaleString("tr-TR")}
                    {full ? " · sınırda" : ""}
                  </span>
                </div>
                {limit !== null ? (
                  <div className="h-2 overflow-hidden rounded-full bg-sunken" aria-hidden="true">
                    <div className={cn("h-full rounded-full", full ? "bg-danger-solid" : "bg-brand")} style={{ width: `${ratio * 100}%` }} />
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

type Pending = { code: string; action: "activate" | "deactivate" };

function ModuleStore({ data, canManage, onChanged }: { data: Data; canManage: boolean; onChanged: () => void }) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [busy, setBusy] = useState<Pending | null>(null);
  const [confirming, setConfirming] = useState<TenantModule | null>(null);
  const [error, setError] = useState<{ code: string; message: string } | null>(null);
  const keys = useRef(new Map<string, string>());
  const errorRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const descriptions = new Map(data.catalog.modules.map((m) => [m.code, m] as const));

  /** One Idempotency-Key per module + action until it succeeds (a retry after a network error is replayed, not repeated). */
  const keyFor = (p: Pending) => {
    const id = `${p.code}|${p.action}`;
    let key = keys.current.get(id);
    if (!key) {
      key = crypto.randomUUID();
      keys.current.set(id, key);
    }
    return key;
  };

  const run = async (module: TenantModule, action: Pending["action"]) => {
    const pending = { code: module.code, action };
    setBusy(pending);
    setError(null);
    try {
      const updated = await (action === "activate" ? activateModule : deactivateModule)(api, module.code, keyFor(pending));
      keys.current.delete(`${pending.code}|${pending.action}`);
      toast({
        tone: "success",
        title: action === "activate" ? "Modül etkinleştirildi" : "Modül kapatıldı",
        description:
          action === "activate"
            ? `${module.name} ücretsiz olarak açıldı${updated.expiresAt ? `; ${formatDate(updated.expiresAt)} tarihine kadar` : ""}.`
            : `${module.name} kapatıldı. Verileriniz silinmedi.`,
      });
      setConfirming(null);
      onChanged();
    } catch (e) {
      setConfirming(null);
      setError({ code: module.code, message: billingErrorMessage(e) });
      focusAfterCommit(() => errorRef.current);
      // The store state changed meanwhile (already active, trial used, …): show the current list.
      if (isStaleModuleState(e)) onChanged();
    } finally {
      setBusy(null);
    }
  };

  return (
    <section aria-labelledby={`${uid}-modules`} className="flex flex-col gap-3">
      <div className="flex flex-col gap-1">
        <h2 id={`${uid}-modules`} className="text-h3 text-fg">
          Ek modüller
        </h2>
        <p className="text-caption text-fg-muted">
          Modüller ücretsiz ve süreli etkinleştirilir ({String(data.catalog.moduleTrialDays)} gün, modül başına bir kez). Deneme sırasında açılan modülün
          süresi deneme bitince başlar.
          {canManage ? "" : " Modülleri yalnızca işletme sahibi etkinleştirebilir veya kapatabilir."}
        </p>
      </div>
      {error ? (
        <FormAlert ref={errorRef} tone="error" title="Modül işlemi yapılamadı">
          {error.message}
        </FormAlert>
      ) : null}
      {data.modules.length === 0 ? (
        <div className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState headingLevel="h3" icon={<Puzzle className="size-6" />} title="Modül yok" description="Katalogda şu an ek modül bulunmuyor." />
        </div>
      ) : (
        <ul aria-label="Ek modüller" className="grid gap-3 md:grid-cols-2">
          {data.modules.map((module) => {
            const info = descriptions.get(module.code);
            const isBusy = busy?.code === module.code;
            return (
              <li key={module.code} className="flex flex-col gap-2 rounded-xl border border-line bg-card p-4 shadow-e1">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <h3 className="font-medium text-fg">{module.name}</h3>
                  <Badge tone={moduleTone[module.status]}>{moduleStatusLabels[module.status]}</Badge>
                </div>
                {info?.description ? <p className="text-body text-fg-secondary">{info.description}</p> : null}
                {info && info.features.length > 0 ? (
                  <p className="text-caption text-fg-muted">Açtığı özellikler: {info.features.map(featureLabel).join(", ")}</p>
                ) : null}
                {module.expiresAt && (module.status === "trialing" || module.status === "active" || module.status === "cancelled") ? (
                  <p className="text-caption text-fg-muted">
                    {module.status === "cancelled" ? "Süre bitişi" : "Bitiş"}: {formatDate(module.expiresAt)}
                  </p>
                ) : null}
                {module.status === "included_in_trial" ? <p className="text-caption text-fg-muted">Deneme süresince açık; deneme bitince kapanır.</p> : null}
                {canManage && (module.canActivate || module.canDeactivate) ? (
                  <div className="mt-auto flex flex-wrap gap-2 pt-1">
                    {module.canActivate ? (
                      <Button
                        size="sm"
                        icon={<Gift className="size-4" />}
                        loading={isBusy && busy?.action === "activate"}
                        disabled={busy !== null && !isBusy}
                        onClick={() => void run(module, "activate")}
                      >
                        Ücretsiz etkinleştir<span className="sr-only"> — {module.name}</span>
                      </Button>
                    ) : null}
                    {module.canDeactivate ? (
                      <Button size="sm" variant="secondary" disabled={busy !== null} onClick={() => setConfirming(module)}>
                        Kapat<span className="sr-only"> — {module.name}</span>
                      </Button>
                    ) : null}
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}
      {confirming ? (
        <Dialog
          open
          role="alertdialog"
          size="sm"
          title={`${confirming.name} kapatılsın mı?`}
          onClose={() => setConfirming(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setConfirming(null)} disabled={busy !== null}>
                Vazgeç
              </Button>
              <Button variant="danger" loading={busy?.action === "deactivate"} onClick={() => void run(confirming, "deactivate")}>
                Modülü kapat
              </Button>
            </>
          }
        >
          <p className="text-body text-fg-secondary">
            Modülün yeni işlemleri kapanır; mevcut verileriniz silinmez ve görünmeye devam eder.
            {confirming.expiresAt ? ` Süre bitene kadar (${formatDate(confirming.expiresAt)}) yeniden açabilirsiniz.` : ""}
          </p>
        </Dialog>
      ) : null}
    </section>
  );
}

function PlanCatalog({ catalog, current }: { catalog: BillingCatalog; current: string }) {
  const uid = useId();
  return (
    <section aria-labelledby={`${uid}-plans`} className="flex flex-col gap-3">
      <h2 id={`${uid}-plans`} className="text-h3 text-fg">
        Paketler
      </h2>
      <ul aria-label="Paketler" className="grid gap-3 md:grid-cols-2">
        {catalog.plans.map((plan) => {
          const isCurrent = plan.code === current;
          return (
            <li key={plan.code} className={cn("flex flex-col gap-2 rounded-lg border bg-card p-4 shadow-e1", isCurrent ? "border-brand-border" : "border-line")}>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h3 className="text-h3 text-fg">{plan.name}</h3>
                {isCurrent ? <Badge tone="brand">Mevcut paketiniz</Badge> : null}
              </div>
              <p className="text-body text-fg-secondary">{plan.description}</p>
              <ul className="flex flex-col gap-1 text-body text-fg">
                {plan.features.map((f) => (
                  <li key={f} className="flex items-center gap-2">
                    <Check className="size-4 shrink-0 text-success-fg" aria-hidden="true" />
                    {featureLabel(f)}
                  </li>
                ))}
                {plan.limits.map((l) => (
                  <li key={l.code} className="flex items-center gap-2">
                    <PackagePlus className="size-4 shrink-0 text-fg-muted" aria-hidden="true" />
                    {limitLabel(l.code)}: {l.value === null ? "sınırsız" : Number(l.value).toLocaleString("tr-TR")}
                  </li>
                ))}
              </ul>
            </li>
          );
        })}
      </ul>
      <p className="text-caption text-fg-muted">
        Dışa aktarma, denetim kaydı ve stok doğruluğu her pakette açıktır. Katalog sürümü {catalog.catalogVersion}.
      </p>
    </section>
  );
}

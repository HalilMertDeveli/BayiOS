"use client";

import { useEffect, useRef } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Check, ShieldAlert } from "@/components/ui/icons";
import { buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { ROUTES } from "@/lib/auth/routes";
import { cn } from "@/lib/cn";
import {
  SETUP_STEPS,
  SETUP_STEP_LABELS,
  STEP_PARAM,
  canRunSetup,
  nextStep,
  parseStep,
  previousStep,
  setupStepHref,
  type SetupFacts,
  type SetupStep,
} from "@/lib/onboarding/setup";
import { useSession } from "@/lib/session/session-context";
import { ChecklistRows, useSetupFacts, type FactsState } from "./setup-checklist";
import { StepFrame, type StepProps } from "./setup-frame";
import { ProductsStep, ProfileStep, StockStep, TeamStep, WarehouseStep } from "./setup-steps";

/** Which wizard step a data fact completes (the business-info step is optional and has no fact). */
const STEP_FACT: Partial<Record<SetupStep, keyof SetupFacts>> = { depo: "warehouse", urunler: "products", stok: "stock", ekip: "team" };

function stepDone(step: SetupStep, facts: FactsState): boolean {
  const key = STEP_FACT[step];
  return !!key && facts.status === "ready" && facts.facts[key] === true;
}

function SetupStepper({ current, facts }: { current: SetupStep; facts: FactsState }) {
  const index = SETUP_STEPS.indexOf(current);
  return (
    <nav aria-label="Kurulum adımları" className="flex flex-col gap-2">
      <p className="text-caption text-fg-muted sm:hidden" aria-hidden="true">
        Adım {index + 1}/{SETUP_STEPS.length}: <span className="font-semibold text-fg">{SETUP_STEP_LABELS[current]}</span>
      </p>
      <ol className="grid grid-cols-6 gap-1.5 sm:gap-2">
        {SETUP_STEPS.map((step, i) => {
          const done = stepDone(step, facts);
          const isCurrent = step === current;
          return (
            <li key={step}>
              <Link
                href={setupStepHref(step)}
                aria-current={isCurrent ? "step" : undefined}
                className={cn(
                  "flex min-h-11 flex-col gap-1 border-t-4 pt-2 text-caption focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand",
                  isCurrent ? "border-brand font-semibold text-fg" : done ? "border-brand text-fg" : "border-line text-fg-muted hover:text-fg",
                )}
              >
                <span className="flex items-center gap-1">
                  {done ? <Check className="size-3.5 shrink-0" aria-hidden="true" /> : null}
                  <span className="sr-only">
                    Adım {i + 1}
                    {done ? " (tamamlandı)" : ""}:{" "}
                  </span>
                  <span className="hidden truncate sm:inline">{SETUP_STEP_LABELS[step]}</span>
                  <span className="sr-only sm:hidden">{SETUP_STEP_LABELS[step]}</span>
                  <span aria-hidden="true" className="sm:hidden">
                    {i + 1}
                  </span>
                </span>
              </Link>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/**
 * /kurulum (#83): guided setup after the business is created — business info, first warehouse (+ simple shelf layout),
 * products (CSV template + import wizard, or a few by hand), opening stock (goods receipt), team invitation, finish.
 * Every step is skippable; the step lives in the URL (?adim=) and "done" comes from the tenant's data, so the setup
 * can be resumed later, also from the dashboard checklist. All writes go through the API (Idempotency-Key per attempt).
 */
export function SetupWizard() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const step = parseStep(searchParams.get(STEP_PARAM));
  const { access, state: session } = useSession();
  const allowed = canRunSetup(access);
  const { state: facts, refresh } = useSetupFacts(allowed);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const firstRender = useRef(true);

  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    headingRef.current?.focus();
  }, [step]);

  const tenantName = session.status === "ready" ? (session.active?.tenantName ?? "") : "";

  if (!allowed) {
    return (
      <>
        <PageHeader title="Kurulum" />
        <section aria-label="Kurulum" className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState
            icon={<ShieldAlert className="size-6" />}
            title="Kurulumu işletme sahibi veya yöneticisi yapar"
            description="Depo, ürün ve ekip kurulumu için yetkiniz yok. İşletme sahibinizle görüşebilirsiniz."
            action={
              <Link href={ROUTES.home} className={buttonClasses("secondary")}>
                Panoya dön
              </Link>
            }
          />
        </section>
      </>
    );
  }

  const go = (target: SetupStep) => router.push(setupStepHref(target), { scroll: true });
  const props: StepProps = {
    headingRef,
    onNext: () => go(nextStep(step)),
    onBack: step === SETUP_STEPS[0] ? null : () => go(previousStep(step)),
    onChanged: refresh,
    facts,
  };

  return (
    <>
      <PageHeader
        title="İşletmenizi kuralım"
        description={
          <>
            {tenantName ? <span className="font-medium text-fg">{tenantName}</span> : null}
            {tenantName ? " · " : null}
            Her adım isteğe bağlıdır; atladığınız adımlara panodaki “İlk adımlar” listesinden dönebilirsiniz.
          </>
        }
        actions={
          <Link href={ROUTES.home} className={buttonClasses("secondary")}>
            Daha sonra tamamla
          </Link>
        }
      />
      <SetupStepper current={step} facts={facts} />
      {step === "isletme" ? <ProfileStep {...props} /> : null}
      {step === "depo" ? <WarehouseStep {...props} /> : null}
      {step === "urunler" ? <ProductsStep {...props} /> : null}
      {step === "stok" ? <StockStep {...props} /> : null}
      {step === "ekip" ? <TeamStep {...props} /> : null}
      {step === "bitti" ? <FinishStep {...props} /> : null}
    </>
  );
}

function FinishStep({ headingRef, onBack, facts }: StepProps) {
  return (
    <StepFrame
      title="Kurulum özeti"
      headingRef={headingRef}
      description="Yaptıklarınız aşağıda işaretli. Eksik kalanları istediğiniz zaman tamamlayabilirsiniz; bu liste panoda da görünür."
    >
      {facts.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          İlerleme yükleniyor…
        </p>
      ) : facts.status === "error" ? (
        <p role="alert" className="text-body text-danger-fg">
          İlerleme yüklenemedi. Sayfayı yenileyip tekrar deneyin.
        </p>
      ) : (
        <ChecklistRows facts={facts.facts} />
      )}
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-between">
        {onBack ? (
          <button type="button" onClick={onBack} className={buttonClasses("secondary")}>
            Geri
          </button>
        ) : (
          <span />
        )}
        <Link href={ROUTES.home} className={buttonClasses("primary")}>
          Panoya git
        </Link>
      </div>
    </StepFrame>
  );
}

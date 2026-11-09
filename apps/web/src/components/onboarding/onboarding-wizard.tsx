"use client";

import { useEffect, useRef, useState, type FormEvent, useId } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Check } from "lucide-react";
import { FormMessage } from "@/components/auth/auth-layout";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import {
  applySectorTemplate,
  createTenant,
  getCurrentTenant,
  getTenantSettings,
  updateTenant,
  updateTenantSettings,
  type Sector,
  type TenantSettings,
} from "@/lib/api/identity";
import { ROUTES } from "@/lib/auth/routes";
import { cn } from "@/lib/cn";
import {
  FIELD_STEP,
  NEGATIVE_STOCK_RULE,
  currencyOptions,
  priceDisplayOptions,
  DEFAULT_SECTOR,
  sectorOptions,
  slugify,
  taxRateOptions,
  tenantFieldMessage,
  validateSlug,
  validateTenantName,
} from "@/lib/onboarding/tenant-form";
import { SETUP_HREF } from "@/lib/onboarding/setup";
import { writeActiveTenantId } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";

const STEPS = ["İşletme bilgileri", "Ürün şablonu", "Ticari ayarlar", "Onay"] as const;

type Values = {
  name: string;
  slug: string;
  slugEdited: boolean;
  sector: Sector | "";
  currency: string;
  taxRate: string;
  priceDisplay: "excluded" | "included";
};
type Errors = { name?: string; slug?: string; sector?: string };

/** Work done after POST /tenants succeeded; each part is retried on its own and never repeated once done. */
type FollowUp = "template" | "settings" | "currency";
const FOLLOW_UP_LABELS: Record<FollowUp, string> = {
  template: "ürün şablonu (birimler, kategoriler, özel alanlar)",
  settings: "ticari ayarlar (KDV, fiyat gösterimi, negatif stok)",
  currency: "para birimi",
};

class FollowUpError extends Error {
  constructor(
    readonly step: FollowUp,
    readonly cause: unknown,
  ) {
    super(step);
  }
}

const newIdempotencyKey = () => crypto.randomUUID();

/**
 * AF-01 onboarding: name/slug + sector → POST /api/v1/tenants, then — as the new owner, with the new tenant
 * as X-Tenant-Id — apply the sector template (Catalog), the commercial settings (PATCH /tenant/settings)
 * and the currency (PATCH /tenant). Identity cannot call Catalog, so the client applies the template.
 */
export function OnboardingWizard() {
  const uid = useId();
  const router = useRouter();
  const { state, api, reload } = useSession();
  const { toast } = useToast();

  const [step, setStep] = useState(0);
  const [values, setValues] = useState<Values>({
    name: "",
    slug: "",
    slugEdited: false,
    sector: DEFAULT_SECTOR,
    currency: "TRY",
    taxRate: "20",
    priceDisplay: "excluded",
  });
  const [errors, setErrors] = useState<Errors>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [createdTenantId, setCreatedTenantId] = useState<string | null>(null);
  // Stable keys per wizard attempt, reused for every retry (network error, double click, corrected input):
  // the API replays a stored 2xx, or — after a 4xx, which it rolls back — runs the corrected request.
  const createKey = useRef<string | null>(null);
  const templateKey = useRef<string | null>(null);
  const done = useRef(new Set<FollowUp>());
  const headingRef = useRef<HTMLHeadingElement>(null);
  const firstRender = useRef(true);

  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    headingRef.current?.focus();
  }, [step]);

  if (state.status === "loading") {
    return (
      <p role="status" className="text-body text-fg-muted">
        Oturum yükleniyor…
      </p>
    );
  }
  if (state.status === "error") {
    return <FormMessage tone="error">{state.message}</FormMessage>;
  }

  const validateStep = (index: number): Errors => {
    if (index === 0) {
      const result: Errors = {};
      const name = validateTenantName(values.name);
      const slug = validateSlug(values.slug);
      if (name) result.name = name;
      if (slug) result.slug = slug;
      return result;
    }
    if (index === 1 && !values.sector) return { sector: "Devam etmek için bir şablon seçin." };
    return {};
  };

  const goNext = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const stepErrors = validateStep(step);
    setErrors(stepErrors);
    if (Object.keys(stepErrors).length === 0) setStep((s) => s + 1);
  };

  const runFollowUp = async (key: FollowUp, work: () => Promise<unknown>) => {
    if (done.current.has(key)) return;
    try {
      await work();
      done.current.add(key);
    } catch (error) {
      throw new FollowUpError(key, error);
    }
  };

  /** Everything after the tenant exists. Throws FollowUpError on the first failing part. */
  const completeTenant = async (tenantId: string, sector: Sector) => {
    if (state.status !== "ready") return;
    writeActiveTenantId(state.user.id, tenantId);
    await reload(); // picks up the new owner membership and makes it the X-Tenant-Id

    await runFollowUp("template", () => {
      templateKey.current ??= newIdempotencyKey();
      return applySectorTemplate(api, sector, templateKey.current);
    });

    await runFollowUp("settings", async () => {
      const { settings, etag } = await getTenantSettings(api);
      const wanted: Partial<TenantSettings> = {
        defaultTaxRate: Number(values.taxRate),
        pricesIncludeTaxDisplay: values.priceDisplay === "included",
      };
      // Numbers may arrive as strings ("20.00"); compare numerically. Send only what differs from the defaults.
      const differs = (current: unknown, next: unknown) => (typeof next === "number" ? Number(current) !== next : current !== next);
      const patch = Object.fromEntries(
        Object.entries(wanted).filter(([field, value]) => differs(settings[field as keyof TenantSettings], value)),
      ) as Partial<TenantSettings>;
      if (Object.keys(patch).length > 0 && etag) await updateTenantSettings(api, patch, etag);
    });

    await runFollowUp("currency", async () => {
      const { tenant, etag } = await getCurrentTenant(api);
      if (tenant.defaultCurrency !== values.currency && etag) {
        await updateTenant(api, { defaultCurrency: values.currency }, etag);
      }
    });

    toast({ tone: "success", title: "İşletmeniz hazır", description: `${values.name.trim()} oluşturuldu. Şimdi kurulumu birlikte tamamlayalım.` });
    // Guided setup (#83): business info, warehouse, products, opening stock, team — every step skippable.
    router.replace(SETUP_HREF);
  };

  const submit = async () => {
    if (!values.sector) return;
    const sector = values.sector;
    setSubmitting(true);
    setSubmitError(null);

    let tenantId = createdTenantId;
    if (!tenantId) {
      createKey.current ??= newIdempotencyKey();
      try {
        const tenant = await createTenant(api, { name: values.name.trim(), slug: values.slug.trim(), sector }, createKey.current);
        tenantId = tenant.id;
        setCreatedTenantId(tenant.id);
      } catch (error) {
        setSubmitting(false);
        await handleCreateError(error);
        return;
      }
    }

    try {
      await completeTenant(tenantId, sector);
    } catch (error) {
      setSubmitting(false);
      const cause = error instanceof FollowUpError ? error.cause : error;
      if (cause instanceof ApiError && cause.status === 401) return; // the session layer redirects to sign-in
      const part = error instanceof FollowUpError ? FOLLOW_UP_LABELS[error.step] : "kurulum";
      const reason = cause instanceof ApiError ? ` ${cause.userMessage}` : "";
      setSubmitError(`İşletmeniz oluşturuldu, ancak ${part} kaydedilemedi.${reason}`);
    }
  };

  const handleCreateError = async (error: unknown) => {
    if (!(error instanceof ApiError)) {
      setSubmitError("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");
      return;
    }
    if (error.code === "tenant.slug_taken") {
      setErrors({ slug: error.userMessage });
      setStep(0);
      return;
    }
    if (error.code === "validation.failed") {
      const fieldErrors: Errors = {};
      for (const field of Object.keys(error.fieldErrors)) {
        if (field in FIELD_STEP) fieldErrors[field as keyof Errors] = tenantFieldMessage(field);
      }
      setErrors(fieldErrors);
      const steps = Object.keys(fieldErrors).map((f) => FIELD_STEP[f]);
      if (steps.length > 0) setStep(Math.min(...steps));
      setSubmitError(error.userMessage);
      return;
    }
    if (error.code === "idempotency.key_reused") {
      // An earlier attempt with this key already succeeded (e.g. its response was lost).
      const memberships = await reload();
      if (memberships.length > 0) {
        router.replace(memberships.length === 1 ? ROUTES.home : ROUTES.tenantPicker);
        return;
      }
      createKey.current = newIdempotencyKey();
    }
    if (error.status === 401) return; // the session layer redirects to sign-in
    setSubmitError(error.userMessage);
  };

  const label = <T extends { value: string; label: string }>(options: readonly T[], value: string) =>
    options.find((o) => o.value === value)?.label ?? "—";

  return (
    <div className="flex flex-col gap-6">
      <ol aria-label="Kurulum adımları" className="grid grid-cols-4 gap-2">
        {STEPS.map((stepLabel, index) => (
          <li
            key={stepLabel}
            aria-current={index === step ? "step" : undefined}
            className={cn(
              "flex flex-col gap-1 border-t-4 pt-2 text-caption",
              index < step ? "border-brand text-fg" : index === step ? "border-brand font-semibold text-fg" : "border-line text-fg-muted",
            )}
          >
            <span className="flex items-center gap-1">
              {index < step ? <Check className="size-3.5" aria-hidden="true" /> : null}
              <span className="sr-only">Adım {index + 1}: </span>
              <span className="hidden sm:inline">{stepLabel}</span>
              <span aria-hidden="true" className="sm:hidden">
                {index + 1}
              </span>
            </span>
          </li>
        ))}
      </ol>

      <form
        noValidate
        onSubmit={
          step < 3
            ? goNext
            : (e) => {
                e.preventDefault();
                void submit();
              }
        }
        className="flex flex-col gap-5"
      >
        <h2 ref={headingRef} tabIndex={-1} className="text-h2 text-fg focus:outline-none">
          {STEPS[step]}
        </h2>

        {submitError ? <FormMessage tone="error">{submitError}</FormMessage> : null}

        {step === 0 ? (
          <>
            <Input
              label="İşletme adı"
              name="name"
              required
              autoComplete="organization"
              value={values.name}
              onChange={(e) => {
                const name = e.target.value;
                setValues((v) => ({ ...v, name, slug: v.slugEdited ? v.slug : slugify(name) }));
              }}
              error={errors.name}
            />
            <Input
              label="İşletme adresi (kısa ad)"
              name="slug"
              required
              autoCapitalize="none"
              spellCheck={false}
              value={values.slug}
              onChange={(e) => setValues((v) => ({ ...v, slug: e.target.value.toLowerCase(), slugEdited: true }))}
              error={errors.slug}
              hint="İşletmenizi tanımlayan benzersiz kısa ad. Küçük harf, rakam ve tire; 3–63 karakter."
            />
          </>
        ) : null}

        {step === 1 ? (
          <fieldset className="flex flex-col gap-3" aria-describedby={errors.sector ? `${uid}-sector-error` : undefined}>
            <legend className="mb-2 text-body text-fg-muted">
              Başlangıç birimlerini ve isterseniz örnek kategori ve özel alanları belirler. Emin değilseniz sade başlangıçla devam edin; birim, kategori ve özel alanları sonradan değiştirebilirsiniz.
            </legend>
            {sectorOptions.map((option) => (
              <label
                key={option.value}
                className={cn(
                  "flex cursor-pointer gap-3 rounded-lg border p-4 transition-colors has-focus-visible:ring-2 has-focus-visible:ring-brand",
                  values.sector === option.value ? "border-brand bg-brand-subtle" : "border-line-strong hover:bg-sunken",
                )}
              >
                <input
                  type="radio"
                  name="sector"
                  value={option.value}
                  checked={values.sector === option.value}
                  onChange={() => setValues((v) => ({ ...v, sector: option.value }))}
                  className="mt-1 size-4 accent-brand"
                />
                <span className="flex flex-col gap-0.5">
                  <span className="text-body font-medium text-fg">{option.label}</span>
                  <span className="text-caption text-fg-muted">{option.description}</span>
                </span>
              </label>
            ))}
            {errors.sector ? (
              <p id={`${uid}-sector-error`} role="alert" className="text-caption text-danger-fg">
                {errors.sector}
              </p>
            ) : null}
          </fieldset>
        ) : null}

        {step === 2 ? (
          <>
            <Select
              label="Para birimi"
              value={values.currency}
              onChange={(e) => setValues((v) => ({ ...v, currency: e.target.value }))}
              options={currencyOptions}
            />
            <Select
              label="Varsayılan KDV oranı"
              value={values.taxRate}
              onChange={(e) => setValues((v) => ({ ...v, taxRate: e.target.value }))}
              options={taxRateOptions}
              hint="Yeni ürünlerde varsayılan oran; her ürün için ayrıca belirlenebilir."
            />
            <Select
              label="Fiyat gösterimi"
              value={values.priceDisplay}
              onChange={(e) => setValues((v) => ({ ...v, priceDisplay: e.target.value as Values["priceDisplay"] }))}
              options={priceDisplayOptions}
            />
            <div className="flex flex-col gap-1 rounded-lg border border-line p-3">
              <span className="text-body font-medium text-fg">{NEGATIVE_STOCK_RULE.title}</span>
              <p className="text-caption text-fg-secondary">{NEGATIVE_STOCK_RULE.text}</p>
            </div>
          </>
        ) : null}

        {step === 3 ? (
          <dl className="grid grid-cols-1 gap-x-6 gap-y-3 rounded-lg border border-line p-4 text-body sm:grid-cols-[auto_1fr]">
            <dt className="text-fg-muted">İşletme adı</dt>
            <dd className="font-medium text-fg">{values.name.trim()}</dd>
            <dt className="text-fg-muted">İşletme adresi</dt>
            <dd className="font-mono text-fg">{values.slug}</dd>
            <dt className="text-fg-muted">Ürün şablonu</dt>
            <dd className="text-fg">{label(sectorOptions, values.sector)}</dd>
            <dt className="text-fg-muted">Para birimi</dt>
            <dd className="text-fg">{label(currencyOptions, values.currency)}</dd>
            <dt className="text-fg-muted">Varsayılan KDV</dt>
            <dd className="text-fg">
              {label(taxRateOptions, values.taxRate)} · {label(priceDisplayOptions, values.priceDisplay)}
            </dd>
            <dt className="text-fg-muted">Negatif stok</dt>
            <dd className="text-fg">{NEGATIVE_STOCK_RULE.title} (sabit kural)</dd>
          </dl>
        ) : null}

        <div className="flex flex-wrap items-center justify-between gap-3">
          {createdTenantId ? (
            <Link href={ROUTES.home} className="text-body text-brand-fg underline-offset-2 hover:underline">
              Şimdilik atla, panoya geç
            </Link>
          ) : step > 0 ? (
            <Button variant="secondary" onClick={() => setStep((s) => s - 1)} disabled={submitting}>
              Geri
            </Button>
          ) : state.status === "ready" && state.memberships.length > 0 ? (
            <Link href={ROUTES.tenantPicker} className="text-body text-brand-fg underline-offset-2 hover:underline">
              Mevcut işletmelerime dön
            </Link>
          ) : (
            <span />
          )}
          <Button type="submit" loading={submitting}>
            {step < 3 ? "Devam" : createdTenantId ? "Kalan adımları tamamla" : "İşletmeyi oluştur"}
          </Button>
        </div>
      </form>
    </div>
  );
}

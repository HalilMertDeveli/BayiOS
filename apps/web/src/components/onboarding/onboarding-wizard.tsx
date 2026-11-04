"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
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
  createTenant,
  getTenantSettings,
  updateTenantSettings,
  type NegativeStockPolicy,
  type Sector,
} from "@/lib/api/identity";
import { ROUTES } from "@/lib/auth/routes";
import { cn } from "@/lib/cn";
import {
  FIELD_STEP,
  negativeStockOptions,
  sectorOptions,
  slugify,
  tenantFieldMessage,
  validateSlug,
  validateTenantName,
} from "@/lib/onboarding/tenant-form";
import { writeActiveTenantId } from "@/lib/session/active-tenant";
import { useSession } from "@/lib/session/session-context";

const STEPS = ["İşletme bilgileri", "Sektör", "Ticari ayarlar", "Onay"] as const;

type Values = { name: string; slug: string; slugEdited: boolean; sector: Sector | ""; negativeStock: NegativeStockPolicy };
type Errors = { name?: string; slug?: string; sector?: string };

function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

/**
 * AF-01 onboarding, limited to what the API supports today: name/slug + sector → POST /api/v1/tenants,
 * then the negative-stock rule via PATCH /api/v1/tenant/settings. Currency and VAT are shown as fixed
 * defaults because the API has no field for them yet.
 */
export function OnboardingWizard() {
  const router = useRouter();
  const { state, api, reload } = useSession();
  const { toast } = useToast();

  const [step, setStep] = useState(0);
  const [values, setValues] = useState<Values>({ name: "", slug: "", slugEdited: false, sector: "", negativeStock: "block" });
  const [errors, setErrors] = useState<Errors>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  // One key per creation attempt, reused for every retry (network error, double click, corrected input):
  // the API either replays the stored 201 or — after a 4xx, which it rolls back — runs the corrected request.
  const idempotencyKey = useRef<string | null>(null);
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
    return <p role="status" className="text-body text-fg-muted">Oturum yükleniyor…</p>;
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
    if (index === 1 && !values.sector) return { sector: "Devam etmek için bir sektör seçin." };
    return {};
  };

  const goNext = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const stepErrors = validateStep(step);
    setErrors(stepErrors);
    if (Object.keys(stepErrors).length === 0) setStep((s) => s + 1);
  };

  const finish = async (tenantId: string) => {
    if (state.status !== "ready") return;
    writeActiveTenantId(state.user.id, tenantId);
    await reload(); // picks up the new owner membership and makes it active for X-Tenant-Id
    try {
      const { settings, etag } = await getTenantSettings(api);
      if (etag && settings.negativeStockPolicy !== values.negativeStock) {
        await updateTenantSettings(api, { negativeStockPolicy: values.negativeStock }, etag);
      }
    } catch {
      toast({
        tone: "error",
        title: "Negatif stok ayarı kaydedilemedi",
        description: "İşletmeniz oluşturuldu. Bu ayarı daha sonra Ayarlar sayfasından değiştirebilirsiniz.",
      });
    }
    toast({ tone: "success", title: "İşletmeniz hazır", description: `${values.name.trim()} oluşturuldu.` });
    router.replace(ROUTES.home);
  };

  const submit = async () => {
    if (!values.sector) return;
    setSubmitting(true);
    setSubmitError(null);
    idempotencyKey.current ??= newIdempotencyKey();
    try {
      const tenant = await createTenant(
        api,
        { name: values.name.trim(), slug: values.slug.trim(), sector: values.sector },
        idempotencyKey.current,
      );
      await finish(tenant.id);
    } catch (error) {
      setSubmitting(false);
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
        idempotencyKey.current = newIdempotencyKey();
      }
      if (error.status === 401) return; // the session layer redirects to sign-in
      setSubmitError(error.userMessage);
    }
  };

  const sectorLabel = sectorOptions.find((s) => s.value === values.sector)?.label ?? "—";
  const policyLabel = negativeStockOptions.find((p) => p.value === values.negativeStock)?.label ?? "—";

  return (
    <div className="flex flex-col gap-6">
      <ol aria-label="Kurulum adımları" className="grid grid-cols-4 gap-2">
        {STEPS.map((label, index) => (
          <li
            key={label}
            aria-current={index === step ? "step" : undefined}
            className={cn(
              "flex flex-col gap-1 border-t-4 pt-2 text-caption",
              index < step ? "border-brand text-fg" : index === step ? "border-brand font-semibold text-fg" : "border-line text-fg-muted",
            )}
          >
            <span className="flex items-center gap-1">
              {index < step ? <Check className="size-3.5" aria-hidden="true" /> : null}
              <span className="sr-only">Adım {index + 1}: </span>
              <span className="hidden sm:inline">{label}</span>
              <span aria-hidden="true" className="sm:hidden">
                {index + 1}
              </span>
            </span>
          </li>
        ))}
      </ol>

      <form noValidate onSubmit={step < 3 ? goNext : (e) => (e.preventDefault(), void submit())} className="flex flex-col gap-5">
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
          <fieldset className="flex flex-col gap-3" aria-describedby={errors.sector ? "sector-error" : undefined}>
            <legend className="mb-2 text-body text-fg-muted">
              Sektörünüz ürün şablonunu ve özel alanları belirler. Daha sonra değiştirilebilir.
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
              <p id="sector-error" role="alert" className="text-caption text-danger-fg">
                {errors.sector}
              </p>
            ) : null}
          </fieldset>
        ) : null}

        {step === 2 ? (
          <>
            <Input label="Para birimi" value="Türk lirası (TRY)" readOnly hint="Şu an yalnızca Türk lirası destekleniyor." />
            <Input
              label="Varsayılan KDV oranı"
              value="%20"
              readOnly
              hint="Yeni ürünlerde varsayılan oran; her ürün için ayrıca belirlenebilir."
            />
            <Select
              label="Negatif stok"
              value={values.negativeStock}
              onChange={(e) => setValues((v) => ({ ...v, negativeStock: e.target.value as NegativeStockPolicy }))}
              options={negativeStockOptions}
              hint="Stokta olmayan bir ürünün sevkine izin verilip verilmeyeceği. Ayarlar'dan değiştirilebilir."
            />
          </>
        ) : null}

        {step === 3 ? (
          <dl className="grid grid-cols-1 gap-x-6 gap-y-3 rounded-lg border border-line p-4 text-body sm:grid-cols-[auto_1fr]">
            <dt className="text-fg-muted">İşletme adı</dt>
            <dd className="font-medium text-fg">{values.name.trim()}</dd>
            <dt className="text-fg-muted">İşletme adresi</dt>
            <dd className="font-mono text-fg">{values.slug}</dd>
            <dt className="text-fg-muted">Sektör</dt>
            <dd className="text-fg">{sectorLabel}</dd>
            <dt className="text-fg-muted">Para birimi</dt>
            <dd className="text-fg">TRY</dd>
            <dt className="text-fg-muted">Negatif stok</dt>
            <dd className="text-fg">{policyLabel}</dd>
          </dl>
        ) : null}

        <div className="flex flex-wrap items-center justify-between gap-3">
          {step > 0 ? (
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
            {step < 3 ? "Devam" : "İşletmeyi oluştur"}
          </Button>
        </div>
      </form>
    </div>
  );
}

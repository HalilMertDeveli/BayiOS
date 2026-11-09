"use client";

import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from "react";
import Link from "next/link";
import { Download, FileUp, PackagePlus, Send, Warehouse as WarehouseIcon } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { InviteLinkDialog, toIssuedLink, type IssuedLink } from "@/components/members/invite-dialogs";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/api/client";
import { getCurrentTenant, getTenantSettings, updateTenant, type TenantResponse, type UpdateTenantRequest } from "@/lib/api/identity";
import { validateEmail } from "@/lib/auth/validation";
import { createProduct, listProducts, listUnits, type Unit } from "@/lib/catalog/api";
import { catalogErrorMessage, codeConflictMessage, readCodeConflict } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { cn } from "@/lib/cn";
import { NEW_IMPORT_HREF } from "@/lib/imports/api";
import { createLocation, createReceipt, createWarehouse, listLocations, listWarehouses, type Location, type Warehouse } from "@/lib/inventory/api";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { inventoryErrorMessage } from "@/lib/inventory/errors";
import { addQuantities, formatQuantity, parseQuantity } from "@/lib/inventory/quantity";
import { validateWarehouse } from "@/lib/inventory/warehouse-form";
import { createInvitation, listInvitations, type Invitation } from "@/lib/members/api";
import { memberErrorMessage } from "@/lib/members/errors";
import { roleLabel } from "@/lib/members/labels";
import { invitableRoles } from "@/lib/members/permissions";
import { validateTenantName } from "@/lib/onboarding/tenant-form";
import {
  DEFAULT_WAREHOUSE,
  IMPORT_TEMPLATE_FILENAME,
  ROLE_PLAIN_TEXT,
  SHELF_COUNT_MAX,
  SHELF_DEFAULT_COUNT,
  importTemplateCsv,
  missingShelves,
  profileFieldMessage,
  profilePatch,
  setupStepHref,
  shelfCodes,
  suggestSku,
  validateProfile,
  validateQuickProduct,
  validateShelfLayout,
  type ProfileErrors,
  type QuickProductErrors,
} from "@/lib/onboarding/setup";
import { saveBlob } from "@/lib/reports/download";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";
import { StepFrame, type StepProps } from "./setup-frame";

type Load<T> = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: T };

/** Loads a step's data once (and again on retry); `load` may only depend on the session's stable api client. */
function useStepData<T>(load: () => Promise<T>, errorMessage: (error: unknown) => string) {
  const [state, setState] = useState<Load<T>>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let cancelled = false;
    load()
      .then((data) => {
        if (!cancelled) setState({ status: "ready", data });
      })
      .catch((error: unknown) => {
        if (!cancelled) setState({ status: "error", message: errorMessage(error) });
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt]);
  const retry = () => {
    setState({ status: "loading" });
    setAttempt((n) => n + 1);
  };
  return { state, setState, retry };
}

function Loading({ label }: { label: string }) {
  return (
    <div role="status" className="flex flex-col gap-2">
      <span className="sr-only">{label}</span>
      <span aria-hidden="true" className="block h-9 w-full animate-pulse rounded-md bg-sunken" />
      <span aria-hidden="true" className="block h-9 w-2/3 animate-pulse rounded-md bg-sunken" />
    </div>
  );
}

function LoadError({ message, retry }: { message: string; retry: () => void }) {
  return (
    <FormAlert
      tone="error"
      title="Bilgiler yüklenemedi"
      actions={
        <Button variant="secondary" onClick={retry}>
          Tekrar dene
        </Button>
      }
    >
      {message}
    </FormAlert>
  );
}

/** Back on the left; skip + primary on the right (stacked on phones, primary on top). */
function StepFooter({ onBack, skip, primary }: { onBack: (() => void) | null; skip?: { label?: string; onClick: () => void; disabled?: boolean }; primary: ReactNode }) {
  return (
    <div className="flex flex-col-reverse gap-2 border-t border-line pt-4 sm:flex-row sm:items-center sm:justify-between">
      {onBack ? (
        <Button variant="secondary" onClick={onBack}>
          Geri
        </Button>
      ) : (
        <span className="hidden sm:block" />
      )}
      <div className="flex flex-col-reverse gap-2 sm:flex-row">
        {skip ? (
          <Button variant="ghost" onClick={skip.onClick} disabled={skip.disabled}>
            {skip.label ?? "Bu adımı atla"}
          </Button>
        ) : null}
        {primary}
      </div>
    </div>
  );
}

function NoPermission({ children }: { children: ReactNode }) {
  return (
    <FormAlert tone="info" title="Bu adım için yetkiniz yok">
      {children}
    </FormAlert>
  );
}

const apiMessage = (error: unknown) => (error instanceof ApiError ? error.userMessage : "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");

// ---------------------------------------------------------------------------------------------------------------------
// 1. Business info (PATCH /tenant: name, phone, tax office, tax number)
// ---------------------------------------------------------------------------------------------------------------------

type ProfileForm = { name: string; phone: string; taxOffice: string; taxNumber: string };

export function ProfileStep({ headingRef, onNext, onBack }: StepProps) {
  const { api, reload } = useSession();
  const { state, setState, retry } = useStepData(() => getCurrentTenant(api), apiMessage);
  const [values, setValues] = useState<ProfileForm | null>(null);
  const [errors, setErrors] = useState<ProfileErrors & { name?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();

  const tenant: TenantResponse | null = state.status === "ready" ? state.data.tenant : null;
  const form: ProfileForm | null =
    values ?? (tenant ? { name: tenant.name, phone: tenant.phone ?? "", taxOffice: tenant.taxOffice ?? "", taxNumber: tenant.taxNumber ?? "" } : null);

  const set = (key: keyof ProfileForm, value: string) => form && setValues({ ...form, [key]: value });

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!form || !tenant || state.status !== "ready" || saving) return;
    const nameError = validateTenantName(form.name);
    const fieldErrors = { ...validateProfile(form), ...(nameError ? { name: nameError } : {}) };
    setErrors(fieldErrors);
    setFormError(null);
    if (Object.keys(fieldErrors).length > 0) return;

    const patch: Partial<UpdateTenantRequest> = profilePatch(tenant, form);
    if (form.name.trim() !== tenant.name) patch.name = form.name.trim();
    if (Object.keys(patch).length === 0 || !state.data.etag) return onNext();

    setSaving(true);
    try {
      const updated = await updateTenant(api, patch, state.data.etag);
      setState({ status: "ready", data: { tenant: updated, etag: `W/"${updated.version}"` } });
      setValues(null);
      if (patch.name) await reload(); // the header shows the business name
      onNext();
    } catch (error) {
      if (error instanceof ApiError && error.code === "validation.failed") {
        const next: Record<string, string> = {};
        for (const field of Object.keys(error.fieldErrors)) next[field] = field === "name" ? "İşletme adı 2–200 karakter olmalı." : profileFieldMessage(field);
        setErrors(next);
      }
      if (error instanceof ApiError && error.status === 412) {
        setFormError("Bilgiler bu arada başka bir yerden değiştirildi. Güncel bilgiler yüklendi; kontrol edip tekrar kaydedin.");
        setValues(null);
        retry();
      } else {
        setFormError(apiMessage(error));
      }
      focusAfterCommit(() => alertRef.current);
    } finally {
      setSaving(false);
    }
  };

  return (
    <StepFrame
      title="İşletme bilgileri"
      headingRef={headingRef}
      description="Fatura ve belgelerde kullanılacak bilgiler. Hepsi isteğe bağlıdır; boş bırakıp sonra doldurabilirsiniz."
    >
      {state.status === "loading" ? <Loading label="İşletme bilgileri yükleniyor…" /> : null}
      {state.status === "error" ? <LoadError message={state.message} retry={retry} /> : null}
      {form ? (
        <form noValidate onSubmit={submit} className="flex flex-col gap-4">
          {formError ? (
            <FormAlert ref={alertRef} tone="error" title="Kaydedilemedi">
              {formError}
            </FormAlert>
          ) : null}
          <Input label="İşletme adı" required autoComplete="organization" value={form.name} onChange={(e) => set("name", e.target.value)} error={errors.name} />
          <Input
            label="Telefon"
            type="tel"
            inputMode="tel"
            autoComplete="tel"
            value={form.phone}
            onChange={(e) => set("phone", e.target.value)}
            error={errors.phone}
            hint="İsteğe bağlı. Rakam, boşluk, +, -, ( ve ) kullanılabilir."
          />
          <div className="grid gap-4 sm:grid-cols-2">
            <Input label="Vergi dairesi" value={form.taxOffice} onChange={(e) => set("taxOffice", e.target.value)} error={errors.taxOffice} hint="İsteğe bağlı." />
            <Input
              label="Vergi no / T.C. kimlik no"
              inputMode="numeric"
              value={form.taxNumber}
              onChange={(e) => set("taxNumber", e.target.value.replace(/\s/g, ""))}
              error={errors.taxNumber}
              hint="İsteğe bağlı. VKN 10, TCKN 11 hane."
            />
          </div>
          {/*
            KVKK consent (#83 / #139): the approved legal text (aydınlatma metni + açık rıza) is not ready. The consent step
            belongs here once the owner supplies the text; no legal wording is invented in the meantime.
          */}
          <StepFooter onBack={onBack} skip={{ onClick: onNext, disabled: saving }} primary={<Button type="submit" loading={saving}>Kaydet ve devam et</Button>} />
        </form>
      ) : null}
    </StepFrame>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// 2. First warehouse + optional simple shelf layout
// ---------------------------------------------------------------------------------------------------------------------

type WarehouseData = { warehouse: Warehouse | null; locations: Location[] };

async function loadWarehouseData(api: ReturnType<typeof useSession>["api"]): Promise<WarehouseData> {
  const warehouses = await listWarehouses(api);
  const warehouse = warehouses.find((w) => w.isDefault) ?? warehouses[0] ?? null;
  const locations = warehouse?.usesLocations ? await listLocations(api, warehouse.id) : [];
  return { warehouse, locations };
}

export function WarehouseStep({ headingRef, onNext, onBack, onChanged }: StepProps) {
  const uid = useId();
  const { api, access } = useSession();
  const canManage = access.canSome("warehouses.manage");
  const { state, setState, retry } = useStepData(() => loadWarehouseData(api), inventoryErrorMessage);
  const [name, setName] = useState<string>(DEFAULT_WAREHOUSE.name);
  const [code, setCode] = useState<string>(DEFAULT_WAREHOUSE.code);
  const [layout, setLayout] = useState<"none" | "simple">("simple");
  const [prefix, setPrefix] = useState("A");
  const [count, setCount] = useState(String(SHELF_DEFAULT_COUNT));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const shelfKeys = useRef(new Map<string, string>());
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();

  const data = state.status === "ready" ? state.data : null;
  const existing = data?.warehouse ?? null;
  /** Shelves can still be laid out: a new warehouse, or an existing one that tracks shelves but has none yet. */
  const offerShelves = !existing || (existing.usesLocations && (data?.locations.length ?? 0) === 0);
  const preview = (() => {
    const n = Number(count);
    if (!Number.isInteger(n) || n < 1 || n > SHELF_COUNT_MAX || !prefix.trim()) return null;
    const codes = shelfCodes(prefix, n);
    return codes.length > 1 ? `${codes[0].code} … ${codes[codes.length - 1].code}` : codes[0].code;
  })();

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy || !data) return;
    const simple = layout === "simple" || (existing?.usesLocations ?? false);
    const fieldErrors: Record<string, string> = existing ? {} : validateWarehouse({ code, name, usesLocations: simple, active: true, makeDefault: false }, "create");
    if (simple) Object.assign(fieldErrors, validateShelfLayout(prefix, count));
    setErrors(fieldErrors);
    setFormError(null);
    if (Object.keys(fieldErrors).length > 0) return;

    setBusy(true);
    let warehouse = existing;
    try {
      if (!warehouse) {
        const body = { code: code.trim(), name: name.trim(), usesLocations: simple };
        warehouse = await createWarehouse(api, body, keys.keyFor(body));
        keys.reset();
        setState({ status: "ready", data: { warehouse, locations: [] } });
      }
      let created = 0;
      if (simple && warehouse.usesLocations) {
        const current = await listLocations(api, warehouse.id, { includeInactive: true });
        const missing = missingShelves(shelfCodes(prefix, Number(count)), current.map((l) => l.code));
        setProgress({ done: 0, total: missing.length });
        for (const shelf of missing) {
          const keyId = `${warehouse.id}:${shelf.code}`;
          if (!shelfKeys.current.has(keyId)) shelfKeys.current.set(keyId, crypto.randomUUID());
          await createLocation(api, warehouse.id, { code: shelf.code, barcode: null, pickSequence: shelf.pickSequence }, shelfKeys.current.get(keyId)!);
          created += 1;
          setProgress({ done: created, total: missing.length });
        }
      }
      onChanged();
      onNext();
    } catch (error) {
      if (error instanceof ApiError && error.code === "warehouse.code_taken") setErrors({ code: inventoryErrorMessage(error) });
      setFormError(
        warehouse && !existing
          ? `Depo oluşturuldu, ancak rafların bir kısmı eklenemedi. ${inventoryErrorMessage(error)} Tekrar denediğinizde yalnızca eksik raflar eklenir.`
          : inventoryErrorMessage(error),
      );
      focusAfterCommit(() => alertRef.current);
    } finally {
      setBusy(false);
      setProgress(null);
    }
  };

  const shelfFields = (
    <div className="grid gap-4 sm:grid-cols-2">
      <Input
        label="Raf kodu ön eki"
        value={prefix}
        maxLength={10}
        autoCapitalize="characters"
        onChange={(e) => setPrefix(e.target.value.toUpperCase())}
        error={errors.prefix}
        hint="Ör. A koridoru için A."
      />
      <Input
        label="Raf sayısı"
        inputMode="numeric"
        value={count}
        onChange={(e) => setCount(e.target.value)}
        error={errors.count}
        hint={preview ? `Oluşacak raflar: ${preview}` : `1 ile ${SHELF_COUNT_MAX} arası.`}
      />
    </div>
  );

  return (
    <StepFrame
      title="İlk deponuz"
      headingRef={headingRef}
      description="Stoğun tutulduğu yeri tanımlayın. Tek bir dükkân veya ambar da bir depodur; daha sonra yenilerini ekleyebilirsiniz."
    >
      {state.status === "loading" ? <Loading label="Depolar yükleniyor…" /> : null}
      {state.status === "error" ? <LoadError message={state.message} retry={retry} /> : null}
      {data && !canManage ? (
        <>
          <NoPermission>Depo ve raf tanımlamak için depo yönetimi yetkisi gerekir.</NoPermission>
          <StepFooter onBack={onBack} primary={<Button onClick={onNext}>Devam</Button>} />
        </>
      ) : null}
      {data && canManage ? (
        <form noValidate onSubmit={submit} className="flex flex-col gap-4">
          {formError ? (
            <FormAlert ref={alertRef} tone="error" title="Kaydedilemedi">
              {formError}
            </FormAlert>
          ) : null}
          {existing ? (
            <div className="flex items-start gap-3 rounded-md border border-line bg-sunken p-3">
              <WarehouseIcon className="mt-0.5 size-5 shrink-0 text-brand-fg" aria-hidden="true" />
              <div className="flex flex-col gap-0.5">
                <p className="text-body font-medium text-fg">
                  {existing.name} <span className="font-mono text-caption text-fg-muted">({existing.code})</span>
                </p>
                <p className="text-caption text-fg-secondary">
                  {existing.usesLocations
                    ? data.locations.length > 0
                      ? `Raf takibi açık · ${data.locations.length} raf tanımlı.`
                      : "Raf takibi açık · henüz raf tanımlanmadı."
                    : "Raf kullanmıyor: stok depo düzeyinde tutulur."}{" "}
                  Değişiklikler için{" "}
                  <Link href="/depo" className="text-brand-fg underline underline-offset-2">
                    Depo ekranı
                  </Link>
                  .
                </p>
              </div>
            </div>
          ) : (
            <>
              <div className="grid gap-4 sm:grid-cols-2">
                <Input label="Depo adı" required value={name} onChange={(e) => setName(e.target.value)} error={errors.name} />
                <Input
                  label="Depo kodu"
                  required
                  autoCapitalize="characters"
                  spellCheck={false}
                  value={code}
                  onChange={(e) => setCode(e.target.value.toUpperCase())}
                  error={errors.code}
                  hint="Kısa ve boşluksuz; belgelerde görünür."
                />
              </div>
              <fieldset className="flex flex-col gap-3">
                <legend className="mb-1 text-body font-medium text-fg">Raf düzeni</legend>
                {(
                  [
                    ["simple", "Basit raf düzeni oluştur", "Ürünleri raf koduyla bulursunuz; toplama raf sırasıyla ilerler. Önerilen."],
                    ["none", "Raf kullanmıyorum", "Stok yalnızca depo düzeyinde tutulur. Raf takibini sonra Depo ekranından açabilirsiniz."],
                  ] as const
                ).map(([value, label, description]) => (
                  <label
                    key={value}
                    className={cn(
                      "flex cursor-pointer gap-3 rounded-lg border p-4 transition-colors has-focus-visible:ring-2 has-focus-visible:ring-brand",
                      layout === value ? "border-brand bg-brand-bg" : "border-line-strong hover:bg-sunken",
                    )}
                  >
                    <input
                      type="radio"
                      name={`${uid}-layout`}
                      value={value}
                      checked={layout === value}
                      onChange={() => setLayout(value)}
                      className="mt-1 size-5 accent-(--bos-brand-primary)"
                    />
                    <span className="flex flex-col gap-0.5">
                      <span className="text-body font-medium text-fg">{label}</span>
                      <span className="text-caption text-fg-muted">{description}</span>
                    </span>
                  </label>
                ))}
              </fieldset>
            </>
          )}
          {offerShelves && (layout === "simple" || existing?.usesLocations) ? shelfFields : null}
          {progress ? (
            <p role="status" className="text-body text-fg-secondary">
              Raflar oluşturuluyor: {progress.done}/{progress.total}
            </p>
          ) : null}
          <StepFooter
            onBack={onBack}
            skip={existing && !offerShelves ? undefined : { onClick: onNext, disabled: busy }}
            primary={
              existing && !offerShelves ? (
                <Button onClick={onNext}>Devam</Button>
              ) : (
                <Button type="submit" loading={busy}>
                  {existing ? "Rafları oluştur" : "Depoyu oluştur"}
                </Button>
              )
            }
          />
        </form>
      ) : null}
    </StepFrame>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// 3. Products: CSV template + import wizard (#149), or a few by hand
// ---------------------------------------------------------------------------------------------------------------------

type ProductsData = { units: Unit[]; taxRate: number };

export function ProductsStep({ headingRef, onNext, onBack, onChanged, facts }: StepProps) {
  const uid = useId();
  const { api, access } = useSession();
  const canImport = access.canSome("imports.run") && access.can("catalog.write");
  const canWrite = access.can("catalog.write");
  const { state, retry } = useStepData<ProductsData>(async () => {
    const [units, taxRate] = await Promise.all([
      listUnits(api),
      getTenantSettings(api)
        .then(({ settings }) => Number(settings.defaultTaxRate))
        .catch(() => 20),
    ]);
    return { units, taxRate: Number.isFinite(taxRate) ? taxRate : 20 };
  }, catalogErrorMessage);
  const [values, setValues] = useState({ name: "", sku: "", barcode: "", unitId: "" });
  const [skuEdited, setSkuEdited] = useState(false);
  const [errors, setErrors] = useState<QuickProductErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [added, setAdded] = useState<{ id: string; name: string; sku: string }[]>([]);
  // Inline confirmations (no toast: it would cover the step footer on small screens).
  const [announcement, setAnnouncement] = useState("");
  const [templateSaved, setTemplateSaved] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const nameRef = useRef<HTMLInputElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();

  const units = state.status === "ready" ? state.data.units.filter((u) => !u.isArchived) : [];
  const unitId = values.unitId || (units.find((u) => u.code === "adet")?.id ?? units[0]?.id ?? "");
  const hasProducts = added.length > 0 || (facts.status === "ready" && facts.facts.products === true);

  const downloadTemplate = () => {
    saveBlob(new Blob([importTemplateCsv()], { type: "text/csv;charset=utf-8" }), IMPORT_TEMPLATE_FILENAME);
    setTemplateSaved(true);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving || state.status !== "ready") return;
    const current = { ...values, unitId };
    const fieldErrors = validateQuickProduct(current);
    setErrors(fieldErrors);
    setFormError(null);
    if (Object.keys(fieldErrors).length > 0) return;
    const body = {
      name: current.name.trim(),
      description: null,
      categoryId: null,
      brandId: null,
      defaultUnitId: current.unitId,
      taxRate: state.data.taxRate,
      status: "active",
      sku: current.sku.trim(),
      barcode: current.barcode.trim() || null,
      variants: null,
    };
    setSaving(true);
    try {
      const product = await createProduct(api, body, keys.keyFor(body));
      keys.reset();
      setAdded((list) => [...list, { id: product.id, name: product.name, sku: current.sku.trim() }]);
      setValues({ name: "", sku: "", barcode: "", unitId: current.unitId });
      setSkuEdited(false);
      onChanged();
      setAnnouncement(`${product.name} (${current.sku.trim()}) eklendi.`);
      focusAfterCommit(() => nameRef.current);
    } catch (error) {
      const taken = readCodeConflict(error);
      if (taken) setErrors({ [taken.field]: codeConflictMessage(taken) });
      else setFormError(catalogErrorMessage(error));
      if (!taken) focusAfterCommit(() => alertRef.current);
    } finally {
      setSaving(false);
    }
  };

  return (
    <StepFrame
      title="Ürünleriniz"
      headingRef={headingRef}
      description="Ürün listeniz Excel'de varsa toplu aktarın; yoksa ilk birkaç ürünü burada elle ekleyin. Fiyat ve ayrıntıları sonra Ürünler ekranından düzenlersiniz."
    >
      {!canWrite ? <NoPermission>Ürün eklemek için katalog yazma yetkisi gerekir.</NoPermission> : null}
      {canImport ? (
        <section aria-labelledby={`${uid}-import`} className="flex flex-col gap-3 rounded-md border border-line p-4">
          <h3 id={`${uid}-import`} className="flex items-center gap-2 text-h3 text-fg">
            <FileUp className="size-5 shrink-0 text-brand-fg" aria-hidden="true" />
            Excel veya CSV ile toplu aktarın
          </h3>
          <ol className="flex list-decimal flex-col gap-1 pl-5 text-body text-fg-secondary">
            <li>Şablonu indirin ve ürünlerinizle doldurun. Zorunlu kolonlar: Stok Kodu, Ürün Adı, Birim (ör. adet) ve KDV Oranı.</li>
            <li>İçe aktarma sihirbazında dosyayı yükleyin; kolonlar otomatik eşlenir.</li>
            <li>Önizlemeyi kontrol edin. Siz onaylamadan hiçbir kayıt yazılmaz; gerekirse aktarmayı geri alabilirsiniz.</li>
          </ol>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Button variant="secondary" icon={<Download className="size-4" />} onClick={downloadTemplate}>
              Şablonu indir (CSV)
            </Button>
            <Link href={NEW_IMPORT_HREF} className={buttonClasses("secondary")}>
              İçe aktarmayı başlat
            </Link>
          </div>
          <p role="status" className="text-caption text-success-fg">
            {templateSaved ? `Şablon indirildi: ${IMPORT_TEMPLATE_FILENAME}` : ""}
          </p>
          <p className="text-caption text-fg-muted">Aktarma bitince bu sayfaya panodaki “İlk adımlar” listesinden dönebilirsiniz.</p>
        </section>
      ) : null}
      {canWrite ? (
        <section aria-labelledby={`${uid}-manual`} className="flex flex-col gap-3 rounded-md border border-line p-4">
          <h3 id={`${uid}-manual`} className="flex items-center gap-2 text-h3 text-fg">
            <PackagePlus className="size-5 shrink-0 text-brand-fg" aria-hidden="true" />
            Elle ekleyin
          </h3>
          {state.status === "loading" ? <Loading label="Birimler yükleniyor…" /> : null}
          {state.status === "error" ? <LoadError message={state.message} retry={retry} /> : null}
          {state.status === "ready" ? (
            <form noValidate onSubmit={submit} aria-label="Hızlı ürün ekleme" className="flex flex-col gap-4">
              {formError ? (
                <FormAlert ref={alertRef} tone="error" title="Ürün eklenemedi">
                  {formError}
                </FormAlert>
              ) : null}
              <Input
                ref={nameRef}
                label="Ürün adı"
                required
                value={values.name}
                onChange={(e) => {
                  const name = e.target.value;
                  setValues((v) => ({ ...v, name, sku: skuEdited ? v.sku : suggestSku(name) }));
                }}
                error={errors.name}
              />
              <div className="grid gap-4 sm:grid-cols-3">
                <Input
                  label="Stok kodu (SKU)"
                  required
                  autoCapitalize="characters"
                  spellCheck={false}
                  value={values.sku}
                  onChange={(e) => {
                    setSkuEdited(true);
                    setValues((v) => ({ ...v, sku: e.target.value }));
                  }}
                  error={errors.sku}
                  hint="Addan önerilir; kendi kodunuzu yazabilirsiniz."
                />
                <Input
                  label="Barkod"
                  inputMode="numeric"
                  spellCheck={false}
                  value={values.barcode}
                  onChange={(e) => setValues((v) => ({ ...v, barcode: e.target.value }))}
                  error={errors.barcode}
                  hint="İsteğe bağlı."
                />
                <Select
                  label="Birim"
                  required
                  value={unitId}
                  onChange={(e) => setValues((v) => ({ ...v, unitId: e.target.value }))}
                  options={units.map((u) => ({ value: u.id, label: u.name }))}
                  placeholder={units.length === 0 ? "Birim yok" : undefined}
                  error={errors.unitId}
                  hint={units.length === 0 ? "Önce Ürünler › Birimler ekranında bir birim ekleyin." : `KDV: %${formatQuantity(state.data.taxRate)} (varsayılan oran)`}
                />
              </div>
              <div className="flex justify-end">
                <Button type="submit" variant="secondary" icon={<PackagePlus className="size-4" />} loading={saving} disabled={units.length === 0}>
                  Ürünü ekle
                </Button>
              </div>
            </form>
          ) : null}
          <p role="status" className="sr-only">
            {announcement}
          </p>
          {added.length > 0 ? (
            <div className="flex flex-col gap-1">
              <h4 className="text-body font-medium text-fg">Bu oturumda eklenenler</h4>
              <ul aria-label="Eklenen ürünler" className="flex flex-col divide-y divide-line rounded-md border border-line">
                {added.map((p) => (
                  <li key={p.id} className="flex items-center justify-between gap-3 px-3 py-2 text-body">
                    <span className="min-w-0 truncate text-fg">{p.name}</span>
                    <span className="shrink-0 font-mono text-caption text-fg-muted">{p.sku}</span>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
        </section>
      ) : null}
      <StepFooter
        onBack={onBack}
        skip={hasProducts ? undefined : { onClick: onNext, disabled: saving }}
        primary={hasProducts ? <Button onClick={onNext}>Devam</Button> : undefined}
      />
    </StepFrame>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// 4. Opening stock: a goods receipt (POST /inventory/receipts) — stock only goes up here, never below zero
// ---------------------------------------------------------------------------------------------------------------------

const OPENING_STOCK_PAGE = 50;
export const OPENING_STOCK_REFERENCE = "Açılış stoğu";

type StockRow = { variantId: string; sku: string; label: string };
type StockData = { warehouse: Warehouse | null; locations: Location[]; rows: StockRow[]; more: boolean };

export function StockStep({ headingRef, onNext, onBack, onChanged, facts }: StepProps) {
  const { api, access } = useSession();
  const canReceive = access.canSome("inventory.receive");
  const { state, retry } = useStepData<StockData>(async () => {
    const [{ warehouse, locations }, products] = await Promise.all([
      loadWarehouseData(api),
      listProducts(api, new URLSearchParams({ status: "active", limit: String(OPENING_STOCK_PAGE) }).toString()),
    ]);
    const rows = products.items.flatMap((p) =>
      p.variants
        .filter((v) => v.status === "active")
        .map((v) => ({ variantId: v.id, sku: v.sku, label: v.name ? `${p.name} — ${v.name}` : p.name })),
    );
    return { warehouse, locations: locations.filter((l) => l.isActive), rows, more: products.page.hasMore };
  }, inventoryErrorMessage);
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [shelves, setShelves] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [result, setResult] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();

  const data = state.status === "ready" ? state.data : null;
  const usesLocations = data?.warehouse?.usesLocations ?? false;
  const hasStock = result !== null || (facts.status === "ready" && facts.facts.stock === true);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving || !data?.warehouse) return;
    const fieldErrors: Record<string, string> = {};
    const lines: { variantId: string; locationId: string | null; quantity: number; unitCost: null }[] = [];
    for (const row of data.rows) {
      const text = quantities[row.variantId]?.trim() ?? "";
      if (!text) continue;
      const q = parseQuantity(text);
      if (!q.ok) {
        fieldErrors[row.variantId] = q.message;
        continue;
      }
      const locationId = usesLocations ? (shelves[row.variantId] ?? data.locations[0]?.id ?? "") : null;
      if (usesLocations && !locationId) fieldErrors[`${row.variantId}:shelf`] = "Raf seçin.";
      lines.push({ variantId: row.variantId, locationId: locationId || null, quantity: q.value, unitCost: null });
    }
    setFormError(null);
    setResult(null);
    if (Object.keys(fieldErrors).length > 0) {
      setErrors(fieldErrors);
      return;
    }
    setErrors({});
    if (lines.length === 0) {
      setFormError("En az bir ürün için miktar girin ya da bu adımı atlayın.");
      focusAfterCommit(() => alertRef.current);
      return;
    }
    const body = { warehouseId: data.warehouse.id, reference: OPENING_STOCK_REFERENCE, lines };
    setSaving(true);
    try {
      const key = keys.keyFor(body);
      await withBusyRetry(() => createReceipt(api, body, key));
      keys.reset();
      const total = lines.reduce((sum, l) => addQuantities(sum, l.quantity), 0);
      const message = `${lines.length} ürün, toplam ${formatQuantity(total)} birim stoğa girdi.`;
      setResult(message);
      setQuantities({});
      onChanged();
    } catch (error) {
      setFormError(inventoryErrorMessage(error));
      focusAfterCommit(() => alertRef.current);
    } finally {
      setSaving(false);
    }
  };

  return (
    <StepFrame
      title="Açılış stoğu"
      headingRef={headingRef}
      description="Şu an elinizde olan miktarları girin; mal kabul olarak kaydedilir ve stok hareketlerinde görünür. Stok hiçbir zaman eksiye düşmez."
    >
      {!canReceive ? <NoPermission>Stok girişi için mal kabul yetkisi gerekir.</NoPermission> : null}
      {canReceive && state.status === "loading" ? <Loading label="Ürünler ve depo yükleniyor…" /> : null}
      {canReceive && state.status === "error" ? <LoadError message={state.message} retry={retry} /> : null}
      {canReceive && data && !data.warehouse ? (
        <EmptyState
          headingLevel="h3"
          title="Önce bir depo tanımlayın"
          description="Stok bir depoda tutulur. Depo adımında ilk deponuzu oluşturun."
          action={
            <Link href={setupStepHref("depo")} className={buttonClasses("secondary")}>
              Depo adımına git
            </Link>
          }
        />
      ) : null}
      {canReceive && data?.warehouse && data.rows.length === 0 ? (
        <EmptyState
          headingLevel="h3"
          title="Henüz ürün yok"
          description="Stok girmek için önce ürün ekleyin."
          action={
            <Link href={setupStepHref("urunler")} className={buttonClasses("secondary")}>
              Ürünler adımına git
            </Link>
          }
        />
      ) : null}
      {canReceive && data?.warehouse && data.rows.length > 0 ? (
        <form noValidate onSubmit={submit} aria-label="Açılış stoğu" className="flex flex-col gap-4">
          <p className="text-body text-fg-secondary">
            Depo: <span className="font-medium text-fg">{data.warehouse.name}</span>. Boş bıraktığınız ürünlere stok girilmez.
          </p>
          {formError ? (
            <FormAlert ref={alertRef} tone="error" title="Stok girilemedi">
              {formError}
            </FormAlert>
          ) : null}
          {result ? (
            <FormAlert tone="success" title="Stok girildi">
              {result}
            </FormAlert>
          ) : null}
          <ul aria-label="Ürünler" className="flex flex-col divide-y divide-line rounded-md border border-line">
            {data.rows.map((row) => (
              <li key={row.variantId} className="flex flex-col gap-2 p-3 sm:flex-row sm:items-start sm:justify-between">
                <span className="flex min-w-0 flex-col pt-1 sm:pt-2">
                  <span className="truncate text-body font-medium text-fg">{row.label}</span>
                  <span className="font-mono text-caption text-fg-muted">{row.sku}</span>
                </span>
                <div className={cn("grid gap-2", usesLocations ? "grid-cols-2 sm:w-80" : "sm:w-40")}>
                  <Input
                    label={`${row.sku} miktarı`}
                    hideLabel
                    inputMode="decimal"
                    placeholder="Miktar"
                    value={quantities[row.variantId] ?? ""}
                    onChange={(e) => setQuantities((q) => ({ ...q, [row.variantId]: e.target.value }))}
                    error={errors[row.variantId]}
                  />
                  {usesLocations ? (
                    <Select
                      label={`${row.sku} rafı`}
                      hideLabel
                      value={shelves[row.variantId] ?? data.locations[0]?.id ?? ""}
                      onChange={(e) => setShelves((s) => ({ ...s, [row.variantId]: e.target.value }))}
                      options={data.locations.map((l) => ({ value: l.id, label: l.code }))}
                      placeholder={data.locations.length === 0 ? "Raf yok" : undefined}
                      error={errors[`${row.variantId}:shelf`]}
                    />
                  ) : null}
                </div>
              </li>
            ))}
          </ul>
          {data.more ? (
            <p className="text-caption text-fg-muted">
              İlk {OPENING_STOCK_PAGE} ürün gösteriliyor. Diğerleri için{" "}
              <Link href="/depo/mal-kabul" className="text-brand-fg underline underline-offset-2">
                Mal kabul
              </Link>{" "}
              ekranında barkod okutarak giriş yapın.
            </p>
          ) : null}
          <div className="flex justify-end">
            <Button type="submit" variant="secondary" loading={saving}>
              Stoğu kaydet
            </Button>
          </div>
        </form>
      ) : null}
      <StepFooter
        onBack={onBack}
        skip={hasStock ? undefined : { onClick: onNext, disabled: saving }}
        primary={hasStock ? <Button onClick={onNext}>Devam</Button> : undefined}
      />
    </StepFrame>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// 5. Team invitation (POST /members/invitations; the link is shown once, BayiOS sends no e-mail)
// ---------------------------------------------------------------------------------------------------------------------

export function TeamStep({ headingRef, onNext, onBack, onChanged, facts }: StepProps) {
  const uid = useId();
  const { api, access, role } = useSession();
  const roles = invitableRoles(role, access);
  const { state, setState, retry } = useStepData<Invitation[]>(
    () => (access.canSome("members.read") ? listInvitations(api, "pending") : Promise.resolve([])),
    memberErrorMessage,
  );
  const [email, setEmail] = useState("");
  const [inviteRole, setInviteRole] = useState<string>(roles.includes("sales") ? "sales" : (roles[0] ?? ""));
  const [errors, setErrors] = useState<{ email?: string; role?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [issued, setIssued] = useState<IssuedLink | null>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const invitations = state.status === "ready" ? state.data : [];
  const hasTeam = invitations.length > 0 || (facts.status === "ready" && facts.facts.team === true);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    const emailError = validateEmail(email);
    const roleError = inviteRole ? undefined : "Bir rol seçin.";
    setErrors({ email: emailError, role: roleError });
    setFormError(null);
    if (emailError || roleError) return;
    setBusy(true);
    try {
      const result = await createInvitation(api, { email: email.trim(), role: inviteRole });
      setIssued(toIssuedLink(result, false));
      setState({ status: "ready", data: [result.invitation, ...invitations.filter((i) => i.id !== result.invitation.id)] });
      setEmail("");
      onChanged();
    } catch (error) {
      setFormError(memberErrorMessage(error));
      focusAfterCommit(() => alertRef.current);
    } finally {
      setBusy(false);
    }
  };

  return (
    <StepFrame
      title="Ekibinizi davet edin"
      headingRef={headingRef}
      description="Çalışma arkadaşlarınıza bir rol seçip davet bağlantısı oluşturun. Bağlantıyı kişiye siz iletirsiniz (e-posta, mesaj); BayiOS e-posta göndermez."
    >
      {roles.length === 0 ? (
        <NoPermission>Ekip daveti için davet yetkisi gerekir.</NoPermission>
      ) : (
        <form noValidate onSubmit={submit} aria-label="Ekip daveti" className="flex flex-col gap-4">
          {formError ? (
            <FormAlert ref={alertRef} tone="error" title="Davet oluşturulamadı">
              {formError}
            </FormAlert>
          ) : null}
          <Input label="E-posta" type="email" autoComplete="off" inputMode="email" value={email} onChange={(e) => setEmail(e.target.value)} error={errors.email} />
          <fieldset className="flex flex-col gap-2" aria-describedby={errors.role ? `${uid}-role-error` : undefined}>
            <legend className="mb-1 text-body font-medium text-fg">Rol</legend>
            {roles.map((r) => (
              <label
                key={r}
                className={cn(
                  "flex cursor-pointer gap-3 rounded-lg border p-3 transition-colors has-focus-visible:ring-2 has-focus-visible:ring-brand",
                  inviteRole === r ? "border-brand bg-brand-bg" : "border-line-strong hover:bg-sunken",
                )}
              >
                <input
                  type="radio"
                  name={`${uid}-role`}
                  value={r}
                  checked={inviteRole === r}
                  onChange={() => setInviteRole(r)}
                  className="mt-1 size-5 accent-(--bos-brand-primary)"
                />
                <span className="flex flex-col gap-0.5">
                  <span className="text-body font-medium text-fg">{roleLabel(r)}</span>
                  <span className="text-caption text-fg-muted">{ROLE_PLAIN_TEXT[r] ?? ""}</span>
                </span>
              </label>
            ))}
            {errors.role ? (
              <p id={`${uid}-role-error`} role="alert" className="text-caption text-danger-fg">
                {errors.role}
              </p>
            ) : null}
          </fieldset>
          <div className="flex justify-end">
            <Button type="submit" variant="secondary" icon={<Send className="size-4" />} loading={busy}>
              Davet bağlantısı oluştur
            </Button>
          </div>
        </form>
      )}
      {state.status === "error" ? <LoadError message={state.message} retry={retry} /> : null}
      {invitations.length > 0 ? (
        <div className="flex flex-col gap-1">
          <h3 className="text-body font-medium text-fg">Bekleyen davetler</h3>
          <ul aria-label="Bekleyen davetler" className="flex flex-col divide-y divide-line rounded-md border border-line">
            {invitations.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 text-body">
                <span className="min-w-0 break-all text-fg">{i.email}</span>
                <span className="text-caption text-fg-muted">{roleLabel(i.role)}</span>
              </li>
            ))}
          </ul>
          <p className="text-caption text-fg-muted">
            Davetleri ve üyeleri{" "}
            <Link href="/ayarlar/ekip" className="text-brand-fg underline underline-offset-2">
              Ayarlar › Ekip
            </Link>{" "}
            ekranından yönetirsiniz.
          </p>
        </div>
      ) : null}
      <StepFooter
        onBack={onBack}
        skip={hasTeam ? undefined : { onClick: onNext, disabled: busy }}
        primary={hasTeam ? <Button onClick={onNext}>Devam</Button> : undefined}
      />
      {issued ? <InviteLinkDialog issued={issued} onClose={() => setIssued(null)} /> : null}
    </StepFrame>
  );
}

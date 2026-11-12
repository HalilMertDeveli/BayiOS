"use client";

import { useEffect, useMemo, useRef, useState, useId, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, ClipboardCheck, Lock, X } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import type { VariantRef } from "@/lib/inventory/receipt-form";
import { COUNTS_HREF, countHref, createCount, type CreateCountRequest } from "@/lib/stock-documents/api";
import { BUSY_FINAL_MESSAGE, documentErrorMessage, documentFieldErrors, isDocumentBusy } from "@/lib/stock-documents/errors";
import { COUNT_SCOPES, countScopeLabels, type CountScope } from "@/lib/stock-documents/labels";
import { countPermissions } from "@/lib/stock-documents/permissions";
import { useSession } from "@/lib/session/session-context";
import { useLocations, useWarehouses } from "@/components/inventory/use-inventory-data";
import { VariantScanner } from "@/components/inventory/variant-scanner";

const SCOPE_HINTS: Record<CountScope, string> = {
  full: "Depodaki stoklu tüm raflar ve ürünler. Sayım sırasında bulunan başka ürünler de eklenebilir.",
  locations: "Yalnızca seçtiğiniz raflar (raf/göz takibi açık depolarda).",
  variants: "Yalnızca seçtiğiniz ürünler, hangi rafta olursa olsun.",
};

/** /depo/sayimlar/yeni: warehouse, scope (full / locations / variants), blind mode. */
export function CountCreate() {
  const uid = useId();
  const router = useRouter();
  const { toast } = useToast();
  const { api, access } = useSession();
  const permissions = countPermissions(access);
  const { state: warehouses } = useWarehouses();
  const active = useMemo(() => (warehouses.status === "ready" ? warehouses.data.filter((w) => w.status === "active") : []), [warehouses]);
  const [warehouseId, setWarehouseId] = useState("");
  const [scope, setScope] = useState<CountScope>("full");
  const [locationIds, setLocationIds] = useState<string[]>([]);
  const [variants, setVariants] = useState<VariantRef[]>([]);
  const [blind, setBlind] = useState(false);
  const [note, setNote] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);

  const [initialized, setInitialized] = useState(false);
  if (!initialized && warehouses.status === "ready") {
    setInitialized(true);
    if (active.length > 0) setWarehouseId(active[0].id);
  }
  const warehouse = active.find((w) => w.id === warehouseId) ?? null;
  const { state: locations } = useLocations(warehouseId, { enabled: warehouse?.usesLocations ?? false });

  useEffect(() => {
    if (formError) alertRef.current?.focus();
  }, [formError]);

  if (!permissions.manage) {
    return (
      <>
        <BackLink />
        <PageHeader title="Yeni sayım" />
        <section className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Sayım başlatma yetkiniz yok" description="Sayımı işletme sahibi, yönetici veya operasyon müdürü başlatır; depo personeli açık sayımlarda sayım yapar." />
        </section>
      </>
    );
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setFormError(null);
    const next: Record<string, string> = {};
    if (!warehouseId) next.warehouseId = "Depo seçin.";
    if (scope === "locations" && locationIds.length === 0) next.locationIds = "En az bir raf seçin.";
    if (scope === "variants" && variants.length === 0) next.variantIds = "En az bir ürün ekleyin.";
    setErrors(next);
    if (Object.keys(next).length > 0) {
      setFormError("Bazı alanlar eksik. İşaretli alanları düzeltin.");
      return;
    }
    const body: CreateCountRequest = {
      warehouseId,
      scope,
      locationIds: scope === "locations" ? locationIds : null,
      variantIds: scope === "variants" ? variants.map((v) => v.id) : null,
      blind,
      note: note.trim() || null,
    };
    const key = keys.keyFor(body);
    setSubmitting(true);
    try {
      const created = await withBusyRetry(() => createCount(api, body, key), { isBusy: isDocumentBusy });
      keys.reset();
      toast({ tone: "success", title: `${created.count.count.number} başlatıldı` });
      router.push(countHref(created.count.count.id));
    } catch (error) {
      if (error instanceof ApiError && error.status === 422 && error.code === "validation.failed") setErrors(documentFieldErrors(error));
      setFormError(isDocumentBusy(error) ? BUSY_FINAL_MESSAGE : documentErrorMessage(error));
      setSubmitting(false);
    }
  };

  const scopes = COUNT_SCOPES.filter((s) => s !== "locations" || warehouse?.usesLocations);

  return (
    <>
      <BackLink />
      <PageHeader title="Yeni sayım" description="Başlattığınız anda kapsamdaki sistem stoğu anlık görüntü olarak alınır. Depo başına aynı anda tek açık sayım olabilir." />
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Sayım başlatılamadı">
          {formError}
        </FormAlert>
      ) : null}
      <form aria-label="Yeni sayım" noValidate onSubmit={submit} className="flex flex-col gap-4 rounded-xl border border-line bg-card p-4 shadow-e1">
        <Select
          label="Depo"
          size="lg"
          required
          placeholder={warehouses.status === "loading" ? "Yükleniyor…" : "Seçin"}
          options={active.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }))}
          value={warehouseId}
          error={errors.warehouseId}
          disabled={warehouses.status !== "ready"}
          onChange={(e) => {
            setWarehouseId(e.target.value);
            setLocationIds([]);
            if (scope === "locations") setScope("full");
          }}
        />
        <fieldset className="flex flex-col gap-1">
          <legend className="text-body font-medium text-fg">Kapsam</legend>
          {scopes.map((value) => (
            <label key={value} className="flex min-h-11 items-start gap-3 py-1 text-body text-fg">
              <input type="radio" name={`${uid}-scope`} className="mt-1 size-5 accent-(--bos-brand-primary)" checked={scope === value} onChange={() => setScope(value)} />
              <span className="flex flex-col">
                {countScopeLabels[value]}
                <span className="text-caption text-fg-muted">{SCOPE_HINTS[value]}</span>
              </span>
            </label>
          ))}
        </fieldset>

        {scope === "locations" ? (
          <fieldset className="flex flex-col gap-1 rounded-md border border-line p-3" aria-describedby={errors.locationIds ? `${uid}-loc-error` : undefined}>
            <legend className="px-1 text-body font-medium text-fg">Sayılacak raflar</legend>
            {locations.status === "loading" ? <p className="text-caption text-fg-muted">Raflar yükleniyor…</p> : null}
            {locations.status === "ready" && locations.data.length === 0 ? <p className="text-caption text-fg-muted">Bu depoda raf tanımlı değil.</p> : null}
            <div className="grid grid-cols-2 gap-x-4 sm:grid-cols-4">
              {locations.status === "ready"
                ? locations.data.map((l) => (
                    <CheckboxField
                      key={l.id}
                      label={l.code}
                      checked={locationIds.includes(l.id)}
                      onChange={(on) => setLocationIds((ids) => (on ? [...ids, l.id] : ids.filter((x) => x !== l.id)))}
                    />
                  ))
                : null}
            </div>
            {errors.locationIds ? (
              <p id={`${uid}-loc-error`} className="text-caption text-danger-fg">
                {errors.locationIds}
              </p>
            ) : null}
          </fieldset>
        ) : null}

        {scope === "variants" ? (
          <div className="flex flex-col gap-2 rounded-md border border-line p-3">
            <VariantScanner
              label="Sayılacak ürünü okutun veya arayın"
              error={errors.variantIds}
              onPick={(v) => setVariants((list) => (list.some((x) => x.id === v.id) ? list : [...list, v]))}
              pickedText={(v) => `${v.sku} eklendi.`}
            />
            {variants.length > 0 ? (
              <ul aria-label="Sayılacak ürünler" className="flex flex-wrap gap-2">
                {variants.map((v) => (
                  <li key={v.id} className="inline-flex items-center gap-1 rounded-md border border-line bg-sunken py-1 pl-3 pr-1 font-mono text-caption text-fg">
                    {v.sku}
                    <button
                      type="button"
                      className="inline-flex size-10 items-center justify-center rounded-md text-fg-muted hover:bg-card hover:text-danger-fg"
                      aria-label={`${v.sku} ürününü kaldır`}
                      onClick={() => setVariants((list) => list.filter((x) => x.id !== v.id))}
                    >
                      <X className="size-4" />
                    </button>
                  </li>
                ))}
              </ul>
            ) : null}
          </div>
        ) : null}

        <CheckboxField
          label="Kör sayım"
          checked={blind}
          onChange={setBlind}
          hint="Sayım yapan personel sistemdeki miktarı görmez; yalnızca saydığını girer. Yöneticiler fark raporunda sistem miktarını görür."
        />
        <Textarea label="Not" maxLength={500} value={note} error={errors.note} hint="İsteğe bağlı (ör. yıl sonu sayımı)." onChange={(e) => setNote(e.target.value)} />
        <Button type="submit" size="lg" className="w-full sm:w-auto sm:self-start" icon={<ClipboardCheck className="size-5" />} loading={submitting}>
          Sayımı başlat
        </Button>
      </form>
    </>
  );
}

function BackLink() {
  return (
    <Link href={COUNTS_HREF} className="inline-flex min-h-11 items-center gap-1 self-start text-body font-medium text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Sayımlar
    </Link>
  );
}

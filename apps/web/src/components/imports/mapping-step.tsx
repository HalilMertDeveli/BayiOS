"use client";

import { useEffect, useId, useMemo, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { CircleAlert, CircleCheck, Circle, Lightbulb, RotateCcw } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import {
  getMappingSuggestions,
  importHref,
  num,
  saveImportMapping,
  validateImport,
  type ImportField,
  type ImportJob,
  type MappingSuggestions,
} from "@/lib/imports/api";
import { importErrorMessage, mappingFieldErrors } from "@/lib/imports/errors";
import { formatCount, matchKeyOptions, modeOptions } from "@/lib/imports/labels";
import {
  PRICE_FIELD,
  applyTemplate,
  assignField,
  buildMappingRequest,
  clearMapping,
  confidencePercent,
  hasErrors,
  initialDraft,
  isAttributeField,
  priceListNeed,
  requiredFieldKeys,
  sampleValues,
  suggestionsByHeader,
  validateDraft,
  type MappingDraft,
  type MappingErrors,
} from "@/lib/imports/mapping";
import type { ImportPermissions } from "@/lib/imports/permissions";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";
import { priceListLabel, usePriceListOptions } from "./new-import";

const NOT_IMPORTED = "";

/** Step 3: map file columns to BayiOS fields. Suggestions are prefilled and marked, never saved without the user. */
export function MappingStep({
  job,
  permissions,
  initialPriceListId,
  onJob,
  onCancel,
}: {
  job: ImportJob;
  permissions: ImportPermissions;
  initialPriceListId: string | null;
  onJob: (job: ImportJob) => void;
  onCancel: () => void;
}) {
  const { api } = useSession();
  const [load, setLoad] = useState<{ status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: MappingSuggestions }>({
    status: "loading",
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    getMappingSuggestions(api, job.id, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", data });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setLoad({ status: "error", message: importErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, job.id, attempt]);

  if (load.status === "loading") {
    return (
      <p role="status" className="rounded-lg border border-line bg-card p-6 text-body text-fg-muted">
        Kolonlar ve öneriler yükleniyor…
      </p>
    );
  }
  if (load.status === "error") {
    return (
      <div role="alert" className="rounded-lg border border-line bg-card">
        <EmptyState
          headingLevel="h3"
          icon={<CircleAlert className="size-6" />}
          title="Kolon önerileri yüklenemedi"
          description={load.message}
          action={<Button onClick={() => setAttempt((n) => n + 1)}>Tekrar dene</Button>}
        />
      </div>
    );
  }
  return <MappingForm job={job} data={load.data} permissions={permissions} initialPriceListId={initialPriceListId} onJob={onJob} onCancel={onCancel} />;
}

function fieldOptionLabel(field: ImportField, required: Set<string>): string {
  const prefix = isAttributeField(field.key) ? "Özel alan: " : "";
  return `${prefix}${field.label}${required.has(field.key) ? " (zorunlu)" : ""}`;
}

function MappingForm({
  job,
  data,
  permissions,
  initialPriceListId,
  onJob,
  onCancel,
}: {
  job: ImportJob;
  data: MappingSuggestions;
  permissions: ImportPermissions;
  initialPriceListId: string | null;
  onJob: (job: ImportJob) => void;
  onCancel: () => void;
}) {
  const { api } = useSession();
  const headers = data.headers;
  const [draft, setDraft] = useState<MappingDraft>(() => initialDraft({ ...job, headers }, data.suggestions));
  const [priceListId, setPriceListId] = useState(job.priceListId ?? initialPriceListId ?? "");
  const [templateId, setTemplateId] = useState("");
  const [saveTemplate, setSaveTemplate] = useState(false);
  const [templateName, setTemplateName] = useState("");
  const [errors, setErrors] = useState<MappingErrors | null>(null);
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [announcement, setAnnouncement] = useState("");
  const [validateKeys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const priceLists = usePriceListOptions(permissions.prices);
  const formId = useId();

  const suggestions = useMemo(() => suggestionsByHeader(data.suggestions), [data.suggestions]);
  const fieldsByKey = useMemo(() => new Map(data.fields.map((f) => [f.key, f])), [data.fields]);
  const required = useMemo(() => new Set(requiredFieldKeys(data.fields, draft.mode, draft.matchKey)), [data.fields, draft.mode, draft.matchKey]);
  const mapped = new Set(Object.values(draft.byHeader).filter(Boolean));
  const need = priceListNeed(job.type, draft.byHeader);
  const fieldOptions = [
    { value: NOT_IMPORTED, label: "— Aktarılmayacak —" },
    ...data.fields.map((f) => ({
      value: f.key,
      label: fieldOptionLabel(f, required),
      // No prices.write: the price column cannot be chosen at all (the API would refuse the whole mapping).
      disabled: f.key === PRICE_FIELD && !permissions.prices,
    })),
  ];
  const label = (key: string) => fieldsByKey.get(key)?.label ?? key;

  const update = (next: MappingDraft) => {
    setDraft(next);
    setErrors(null);
    setServerErrors({});
  };

  const assign = (header: string, field: string) => {
    const previous = Object.entries(draft.byHeader).find(([h, f]) => f === field && h !== header && field);
    update({ ...draft, byHeader: assignField(draft.byHeader, header, field), source: draft.source });
    setAnnouncement(previous ? `“${label(field)}” alanı “${previous[0]}” kolonundan alınıp “${header}” kolonuna eşlendi.` : "");
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    setFormError(null);
    setServerErrors({});
    const clientErrors = validateDraft({ type: job.type, fields: data.fields, draft, priceListId, saveTemplate, templateName, canWritePrices: permissions.prices });
    setErrors(clientErrors);
    if (hasErrors(clientErrors)) {
      focusAfterCommit(() => alertRef.current);
      return;
    }
    setSaving(true);
    let saved: ImportJob | null = null;
    try {
      saved = await saveImportMapping(api, job.id, buildMappingRequest({ type: job.type, draft, priceListId, saveTemplate, templateName }));
      onJob(saved);
      const validating = await validateImport(api, job.id, validateKeys.keyFor({ id: job.id, version: saved.version }));
      validateKeys.reset();
      onJob(validating);
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) setServerErrors(mappingFieldErrors(error));
      setFormError(saved ? `Eşleme kaydedildi ancak önizleme başlatılamadı: ${importErrorMessage(error)}` : importErrorMessage(error));
      setSaving(false);
      focusAfterCommit(() => alertRef.current);
    }
  };

  const missing = errors?.missing ?? [];
  const serverColumnErrors = Object.entries(serverErrors).filter(([k]) => k.startsWith("columns"));
  const template = data.templates.find((t) => t.id === templateId);

  return (
    <form id={formId} noValidate onSubmit={submit} className="flex flex-col gap-5 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      <div className="flex flex-col gap-1">
        <h2 className="text-h2 text-fg">Kolonları eşleyin</h2>
        <p className="text-body text-fg-secondary">
          {job.fileName} · {formatCount(num(job.totalRows))} satır · {formatCount(headers.length)} kolon
        </p>
      </div>

      {job.duplicateOfImportId ? (
        <FormAlert tone="warning" title="Bu dosya daha önce aktarılmış">
          Aynı içerikli bir dosya bu işletmede zaten tamamlanmış bir aktarmada kullanıldı.{" "}
          <Link href={importHref(job.duplicateOfImportId)} className="font-medium underline underline-offset-2">
            Önceki aktarmayı aç
          </Link>
          . Yine de devam ederseniz önizleme hangi satırların değişeceğini gösterir.
        </FormAlert>
      ) : null}

      {formError || (errors && hasErrors(errors)) ? (
        <FormAlert ref={alertRef} tone="error" title="Eşleme kaydedilemedi">
          {formError ? <p>{formError}</p> : null}
          <ul className="list-disc pl-5">
            {missing.map((k) => (
              <li key={k}>“{label(k)}” alanı zorunlu; bir kolonla eşleyin.</li>
            ))}
            {errors?.priceList ? <li>{errors.priceList}</li> : null}
            {errors?.permission ? <li>{errors.permission}</li> : null}
            {errors?.template ? <li>{errors.template}</li> : null}
            {serverColumnErrors.map(([k, m]) => (
              <li key={k}>
                {k.startsWith("columns.") ? `${label(k.slice("columns.".length))}: ` : ""}
                {m}
              </li>
            ))}
          </ul>
        </FormAlert>
      ) : null}

      {draft.source === "suggested" ? (
        <FormAlert tone="info" title="Öneriler ön seçili">
          BayiOS kolon başlıklarına bakarak eşleme önerdi; öneriler “Öneri” etiketiyle işaretli. Kaydetmeden önce her kolonu kontrol edin, gerekirse
          değiştirin.
        </FormAlert>
      ) : draft.source === "saved" ? (
        <p className="text-caption text-fg-muted">Daha önce kaydettiğiniz eşleme yüklendi.</p>
      ) : null}

      <section aria-labelledby={`${formId}-settings`} className="grid gap-4 md:grid-cols-2">
        <h3 id={`${formId}-settings`} className="sr-only">
          Eşleşme ayarları
        </h3>
        <Select
          label="Eşleşme anahtarı"
          value={draft.matchKey}
          onChange={(e) => update({ ...draft, matchKey: e.target.value })}
          options={matchKeyOptions}
          hint="Dosyadaki satır, bu alana göre mevcut kayıtla eşleştirilir."
        />
        <Select
          label="Aktarma modu"
          value={draft.mode}
          onChange={(e) => update({ ...draft, mode: e.target.value })}
          options={modeOptions}
          hint={job.type === "prices" ? "Fiyat aktarma ürün oluşturmaz; yalnızca listedeki fiyatları ekler/günceller." : undefined}
        />
        {need === "required" ? (
          priceLists.status === "error" ? (
            <FormAlert tone="error" title="Fiyat listeleri yüklenemedi" actions={<Button variant="secondary" onClick={priceLists.retry}>Tekrar dene</Button>}>
              {priceLists.message}
            </FormAlert>
          ) : (
            <Select
              label="Hedef fiyat listesi"
              required
              placeholder={priceLists.status === "loading" ? "Yükleniyor…" : "Liste seçin"}
              disabled={!permissions.prices || priceLists.status === "loading"}
              value={priceListId}
              onChange={(e) => {
                setPriceListId(e.target.value);
                setErrors(null);
              }}
              options={priceLists.lists.map((l) => ({ value: l.id, label: priceListLabel(l) }))}
              error={errors?.priceList ?? serverErrors.priceListId}
              hint={job.type === "products" ? "Fiyat kolonu eşlendiği için fiyatlar bu listeye yazılır." : "Dosyadaki fiyatlar bu listeye yazılır."}
            />
          )
        ) : null}
      </section>

      {data.templates.length > 0 ? (
        <section aria-labelledby={`${formId}-templates`} className="flex flex-col gap-2 rounded-md border border-line p-3">
          <h3 id={`${formId}-templates`} className="text-body font-semibold text-fg">
            Kayıtlı şablonlar
          </h3>
          <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
            <Select
              className="sm:flex-1"
              label="Bu dosyaya uyan şablon"
              placeholder="Şablon seçin"
              value={templateId}
              onChange={(e) => setTemplateId(e.target.value)}
              options={data.templates.map((t) => ({ value: t.id, label: t.name }))}
            />
            <Button
              variant="secondary"
              disabled={!template}
              onClick={() => {
                if (!template) return;
                update(applyTemplate(template, headers));
                setAnnouncement(`“${template.name}” şablonu uygulandı.`);
              }}
            >
              Şablonu uygula
            </Button>
          </div>
        </section>
      ) : null}

      <section aria-labelledby={`${formId}-columns`} className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 id={`${formId}-columns`} className="text-h3 text-fg">
            Dosyadaki kolonlar
          </h3>
          <Button
            variant="ghost"
            size="sm"
            icon={<RotateCcw className="size-4" />}
            onClick={() => {
              update(clearMapping(headers, draft));
              setAnnouncement("Tüm eşlemeler temizlendi.");
            }}
          >
            Tümünü temizle
          </Button>
        </div>
        <p aria-live="polite" className="sr-only">
          {announcement}
        </p>
        <ul className="flex flex-col divide-y divide-line rounded-md border border-line">
          {headers.map((header, index) => {
            const field = draft.byHeader[header] ?? NOT_IMPORTED;
            const suggestion = suggestions[header];
            const followsSuggestion = suggestion !== undefined && suggestion.field === field;
            const samples = sampleValues(headers, job.sampleRows, header);
            const serverError = field ? serverErrors[`columns.${field}`] : undefined;
            return (
              <li key={header} className="grid gap-3 p-3 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] md:items-start">
                <div className="flex min-w-0 flex-col gap-1">
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="font-medium break-all text-fg">{header}</span>
                    {followsSuggestion && draft.source !== "saved" ? (
                      <Badge tone="ai" icon={<Lightbulb className="size-3" aria-hidden="true" />}>
                        Öneri {confidencePercent(suggestion.confidence)}
                      </Badge>
                    ) : null}
                  </span>
                  <span className="text-caption break-words text-fg-muted">
                    {samples.length ? `Örnek: ${samples.join(" · ")}` : "Örnek değer yok"}
                  </span>
                </div>
                <div className="flex flex-col gap-1">
                  <Select
                    id={`${formId}-col-${index}`}
                    label={`“${header}” kolonu hangi alana aktarılsın?`}
                    hideLabel
                    value={field}
                    onChange={(e) => assign(header, e.target.value)}
                    options={fieldOptions}
                    error={serverError}
                    hint={
                      suggestion && !followsSuggestion
                        ? `Sistem önerisi: ${label(suggestion.field)} (${confidencePercent(suggestion.confidence)} güven)`
                        : undefined
                    }
                  />
                </div>
              </li>
            );
          })}
        </ul>
      </section>

      <section aria-labelledby={`${formId}-required`} className="flex flex-col gap-2">
        <h3 id={`${formId}-required`} className="text-body font-semibold text-fg">
          Zorunlu alanlar
        </h3>
        <ul className="flex flex-wrap gap-x-4 gap-y-1">
          {[...required].map((key) => {
            const ok = mapped.has(key);
            return (
              <li key={key} className={ok ? "flex items-center gap-1 text-body text-success-fg" : "flex items-center gap-1 text-body text-fg-secondary"}>
                {ok ? <CircleCheck className="size-4" aria-hidden="true" /> : <Circle className="size-4" aria-hidden="true" />}
                {label(key)}
                <span className="sr-only">{ok ? ": eşlendi" : ": henüz eşlenmedi"}</span>
              </li>
            );
          })}
        </ul>
        {job.type === "products" && !permissions.prices ? (
          <p className="text-caption text-fg-muted">Fiyat kolonu seçilemez: fiyat yazma yetkiniz yok. Fiyatları yetkili bir kullanıcı aktarabilir.</p>
        ) : null}
      </section>

      <section className="flex flex-col gap-3 rounded-md border border-line p-3">
        <label className="inline-flex min-h-11 items-center gap-2 text-body text-fg">
          <input type="checkbox" className="size-5 accent-(--bos-brand-primary)" checked={saveTemplate} onChange={(e) => setSaveTemplate(e.target.checked)} />
          Bu eşlemeyi şablon olarak kaydet
        </label>
        {saveTemplate ? (
          <Input
            label="Şablon adı"
            required
            maxLength={100}
            value={templateName}
            onChange={(e) => setTemplateName(e.target.value)}
            error={errors?.template ?? serverErrors.saveAsTemplate}
            hint="Aynı adlı şablon varsa güncellenir. Sonraki dosyalarda uyan şablonlar burada listelenir."
          />
        ) : null}
      </section>

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-between">
        <Button variant="ghost" onClick={onCancel} disabled={saving}>
          Aktarmayı iptal et
        </Button>
        <Button type="submit" loading={saving}>
          Kaydet ve önizle
        </Button>
      </div>
    </form>
  );
}

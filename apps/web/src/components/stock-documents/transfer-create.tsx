"use client";

import { useEffect, useMemo, useRef, useState, useId, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, Lock, Save, Trash2 } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { fieldId, partitionErrors } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { balanceQuery, listBalances } from "@/lib/inventory/api";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { formatQuantity } from "@/lib/inventory/quantity";
import { TRANSFERS_HREF, createTransfer, transferHref } from "@/lib/stock-documents/api";
import { BUSY_FINAL_MESSAGE, documentErrorMessage, documentFieldErrors, isDocumentBusy } from "@/lib/stock-documents/errors";
import { transferPermissions } from "@/lib/stock-documents/permissions";
import {
  NOTE_MAX,
  addTransferLine,
  availabilityFrom,
  bestShelf,
  buildTransferRequest,
  emptyTransfer,
  transferTotal,
  validateTransfer,
  type Availability,
  type TransferErrors,
  type TransferFormValues,
} from "@/lib/stock-documents/transfer-form";
import { useSession } from "@/lib/session/session-context";
import { useLocations, useWarehouses } from "@/components/inventory/use-inventory-data";
import { VariantScanner } from "@/components/inventory/variant-scanner";

/** (warehouse id|variant id) → availability per source shelf; absent = loading. */
type AvailState = Record<string, Availability | "error">;

/** /depo/transferler/yeni: a draft transfer — source and destination warehouse, lines with source shelf and quantity. */
export function TransferCreate() {
  const uid = useId();
  const router = useRouter();
  const { toast } = useToast();
  const { api, access } = useSession();
  const permissions = transferPermissions(access);
  const { state: warehouses } = useWarehouses();
  const active = useMemo(() => (warehouses.status === "ready" ? warehouses.data.filter((w) => w.status === "active") : []), [warehouses]);

  const [values, setValues] = useState<TransferFormValues>(emptyTransfer());
  const [errors, setErrors] = useState<TransferErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const [avail, setAvail] = useState<AvailState>({});
  const requested = useRef(new Set<string>());
  const nextKey = useRef(1);
  const alertRef = useRef<HTMLDivElement>(null);
  const scannerRef = useRef<HTMLInputElement>(null);

  const [initialized, setInitialized] = useState(false);
  if (!initialized && warehouses.status === "ready") {
    setInitialized(true);
    if (active.length > 0) setValues((v) => ({ ...v, sourceWarehouseId: active[0].id }));
  }

  const source = active.find((w) => w.id === values.sourceWarehouseId) ?? null;
  const destination = active.find((w) => w.id === values.destinationWarehouseId) ?? null;
  const sourceUsesLocations = source?.usesLocations ?? false;
  const destinationUsesLocations = destination?.usesLocations ?? false;
  const { state: sourceLocations } = useLocations(values.sourceWarehouseId, { enabled: sourceUsesLocations });
  const { state: destinationLocations } = useLocations(values.destinationWarehouseId, { enabled: destinationUsesLocations });
  const sourceCode = (id: string) => (sourceLocations.status === "ready" ? (sourceLocations.data.find((l) => l.id === id)?.code ?? "…") : "…");
  const destinationOptions = destinationLocations.status === "ready" ? destinationLocations.data.map((l) => ({ value: l.id, label: l.code })) : [];

  // Available stock of every line's variant in the source warehouse (GET /inventory/balances?variantId&warehouseId),
  // cached per (warehouse, variant); absent = still loading.
  const availKey = (warehouseId: string, variantId: string) => `${warehouseId}|${variantId}`;
  const availRaw = (variantId: string) => avail[availKey(values.sourceWarehouseId, variantId)];
  const availability = (variantId: string): Availability | undefined => {
    const value = availRaw(variantId);
    return value instanceof Map ? value : undefined;
  };
  const variantIdsKey = [...new Set(values.lines.map((l) => l.variant.id))].sort().join(",");
  useEffect(() => {
    const warehouseId = values.sourceWarehouseId;
    if (!warehouseId || !variantIdsKey) return;
    const missing = variantIdsKey.split(",").filter((id) => !requested.current.has(availKey(warehouseId, id)));
    for (const variantId of missing) {
      const key = availKey(warehouseId, variantId);
      requested.current.add(key);
      listBalances(api, balanceQuery({ variantId, warehouseId }, null, 200))
        .then((page) => {
          const map = availabilityFrom(page.items, warehouseId);
          setAvail((prev) => ({ ...prev, [key]: map }));
          // A new line of this variant gets the shelf with the most available stock (located source only).
          const shelf = bestShelf(map);
          if (!shelf) return;
          setValues((v) => {
            if (v.sourceWarehouseId !== warehouseId) return v;
            let changed = false;
            const lines = v.lines.map((l) => {
              if (l.variant.id !== variantId || l.sourceLocationId || v.lines.some((x) => x.variant.id === variantId && x.sourceLocationId === shelf)) return l;
              changed = true;
              return { ...l, sourceLocationId: shelf };
            });
            return changed ? { ...v, lines } : v;
          });
        })
        .catch(() => {
          requested.current.delete(key);
          setAvail((prev) => ({ ...prev, [key]: "error" }));
        });
    }
  }, [api, values.sourceWarehouseId, variantIdsKey]);

  useEffect(() => {
    if (formError) alertRef.current?.focus();
  }, [formError]);

  if (!permissions.manage) {
    return (
      <>
        <BackLink />
        <PageHeader title="Yeni transfer" />
        <section className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Transfer oluşturma yetkiniz yok" description="Transferleri işletme sahibi, yönetici veya operasyon müdürü oluşturur." />
        </section>
      </>
    );
  }

  const updateLine = (index: number, patch: Partial<{ quantity: string; sourceLocationId: string; destinationLocationId: string }>) =>
    setValues((v) => ({ ...v, lines: v.lines.map((l, i) => (i === index ? { ...l, ...patch } : l)) }));
  const removeLine = (index: number) => {
    setValues((v) => ({ ...v, lines: v.lines.filter((_, i) => i !== index) }));
    setErrors((e) => Object.fromEntries(Object.entries(e).filter(([k]) => !k.startsWith("lines["))));
    scannerRef.current?.focus();
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setFormError(null);
    const clientErrors = validateTransfer(values, { sourceUsesLocations, availability });
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) {
      setFormError("Bazı alanlar eksik veya hatalı. İşaretli alanları düzeltin.");
      return;
    }
    const body = buildTransferRequest(values, { sourceUsesLocations, destinationUsesLocations });
    const key = keys.keyFor(body);
    setSubmitting(true);
    try {
      const created = await withBusyRetry(() => createTransfer(api, body, key), { isBusy: isDocumentBusy });
      keys.reset();
      toast({ tone: "success", title: `${created.transfer.transfer.number} taslak olarak kaydedildi` });
      router.push(transferHref(created.transfer.transfer.id));
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) {
        setErrors(documentFieldErrors(error));
        setFormError(documentErrorMessage(error));
      } else {
        setFormError(isDocumentBusy(error) ? BUSY_FINAL_MESSAGE : documentErrorMessage(error));
      }
      setSubmitting(false);
    }
  };

  const knownKeys = new Set<string>(["sourceWarehouseId", "destinationWarehouseId", "note", "lines"]);
  values.lines.forEach((_, i) => {
    knownKeys.add(`lines[${i}].quantity`);
    knownKeys.add(`lines[${i}].sourceLocationId`);
    knownKeys.add(`lines[${i}].destinationLocationId`);
  });
  const { onFields, unmatched } = partitionErrors(errors, knownKeys);
  const warehouseOptions = active.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }));

  return (
    <>
      <BackLink />
      <PageHeader title="Yeni transfer" description="Taslak olarak kaydedilir; stok ancak “Gönder” ile kaynaktan çıkar ve teslim alınana kadar yolda görünür." />
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Transfer kaydedilemedi">
          <p>{formError}</p>
          {unmatched.length > 0 ? (
            <ul className="mt-1 list-disc pl-5">
              {unmatched.map((m, i) => (
                <li key={i}>{m}</li>
              ))}
            </ul>
          ) : null}
        </FormAlert>
      ) : null}

      <form aria-label="Yeni transfer" noValidate onSubmit={submit} className="flex flex-col gap-4 pb-24 md:pb-0">
        <section aria-label="Depolar" className="grid grid-cols-1 gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 md:grid-cols-2">
          <Select
            id={fieldId("sourceWarehouseId")}
            label="Kaynak depo"
            size="lg"
            required
            placeholder={warehouses.status === "loading" ? "Yükleniyor…" : "Seçin"}
            options={warehouseOptions}
            value={values.sourceWarehouseId}
            disabled={warehouses.status !== "ready"}
            error={onFields.sourceWarehouseId}
            onChange={(e) => {
              const id = e.target.value;
              setValues((v) => ({ ...v, sourceWarehouseId: id, lines: v.lines.map((l) => ({ ...l, sourceLocationId: "" })) }));
              setErrors({});
            }}
          />
          <Select
            id={fieldId("destinationWarehouseId")}
            label="Hedef depo"
            size="lg"
            required
            placeholder="Seçin"
            options={warehouseOptions.filter((o) => o.value !== values.sourceWarehouseId)}
            value={values.destinationWarehouseId}
            disabled={warehouses.status !== "ready"}
            error={onFields.destinationWarehouseId}
            hint="Aynı depoda raf değiştirmek için Stok → Raf taşıma kullanın."
            onChange={(e) => {
              const id = e.target.value;
              setValues((v) => ({ ...v, destinationWarehouseId: id, lines: v.lines.map((l) => ({ ...l, destinationLocationId: "" })) }));
            }}
          />
          <Textarea
            id={fieldId("note")}
            label="Not"
            className="md:col-span-2"
            maxLength={NOTE_MAX}
            value={values.note}
            error={onFields.note}
            hint="İsteğe bağlı (ör. araç plakası veya irsaliye numarası)."
            onChange={(e) => setValues((v) => ({ ...v, note: e.target.value }))}
          />
          {warehouses.status === "ready" && active.length < 2 ? (
            <p className="text-body text-warning-fg md:col-span-2">Transfer için en az iki aktif depo gerekir. Depo sayfasından yeni depo ekleyin.</p>
          ) : null}
          {warehouses.status === "error" ? <p className="text-caption text-danger-fg md:col-span-2">Depolar yüklenemedi: {warehouses.message}</p> : null}
        </section>

        <section aria-labelledby={`${uid}-transfer-lines`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
          <h2 id={`${uid}-transfer-lines`} className="text-h3 text-fg">
            Ürünler
          </h2>
          <VariantScanner
            inputRef={scannerRef}
            label="Barkod veya SKU okutun"
            hint="Her okutma 1 ekler; miktarı satırda değiştirebilirsiniz."
            error={onFields.lines}
            disabled={!values.sourceWarehouseId}
            onPick={(variant) => {
              setValues((v) => ({
                ...v,
                lines: addTransferLine(v.lines, variant, sourceUsesLocations ? bestShelf(availability(variant.id)) : "", () => `line-${nextKey.current++}`).lines,
              }));
              setErrors((e) => Object.fromEntries(Object.entries(e).filter(([k]) => k !== "lines")));
            }}
            pickedText={(variant) => `${variant.sku} eklendi.`}
          />
          {values.lines.length === 0 ? (
            <p className="rounded-md border border-dashed border-line-strong p-4 text-center text-body text-fg-muted">Henüz ürün eklenmedi.</p>
          ) : (
            <ol aria-label="Transfer satırları" className="flex flex-col gap-3">
              {values.lines.map((line, i) => {
                const raw = availRaw(line.variant.id);
                const map = raw instanceof Map ? raw : undefined;
                const shelfOptions = map
                  ? [...map.entries()].filter(([loc, qty]) => loc && qty > 0).map(([loc, qty]) => ({ value: loc, label: `${sourceCode(loc)} · satılabilir ${formatQuantity(qty)}` }))
                  : [];
                const availableHere = map ? (map.get(sourceUsesLocations ? line.sourceLocationId : "") ?? 0) : null;
                return (
                  <li key={line.key} data-testid={`transfer-line-${line.variant.sku}`} className="flex flex-col gap-3 rounded-md border border-line p-3">
                    <div className="flex items-start justify-between gap-2">
                      <div className="flex min-w-0 flex-col">
                        <span className="font-mono text-body font-semibold text-fg">{line.variant.sku}</span>
                        <span className="text-caption text-fg-muted">
                          {[line.variant.name, raw === undefined ? "Stok kontrol ediliyor…" : raw === "error" ? "Satılabilir stok okunamadı" : availableHere !== null && (!sourceUsesLocations || line.sourceLocationId) ? `Kaynakta satılabilir ${formatQuantity(availableHere)}` : null]
                            .filter(Boolean)
                            .join(" · ")}
                        </span>
                      </div>
                      <button
                        type="button"
                        onClick={() => removeLine(i)}
                        aria-label={`${line.variant.sku} satırını kaldır`}
                        className="inline-flex size-12 shrink-0 items-center justify-center rounded-md text-fg-muted hover:bg-sunken hover:text-danger-fg"
                      >
                        <Trash2 className="size-5" />
                      </button>
                    </div>
                    <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                      {sourceUsesLocations ? (
                        <Select
                          id={fieldId(`lines[${i}].sourceLocationId`)}
                          label={`${line.variant.sku} kaynak rafı`}
                          size="lg"
                          placeholder={map && shelfOptions.length === 0 ? "Satılabilir stok yok" : "Raf seçin"}
                          options={shelfOptions}
                          value={line.sourceLocationId}
                          error={onFields[`lines[${i}].sourceLocationId`]}
                          onChange={(e) => updateLine(i, { sourceLocationId: e.target.value })}
                        />
                      ) : null}
                      <Input
                        id={fieldId(`lines[${i}].quantity`)}
                        label={`${line.variant.sku} miktarı`}
                        size="lg"
                        inputMode="decimal"
                        autoComplete="off"
                        value={line.quantity}
                        error={onFields[`lines[${i}].quantity`]}
                        onChange={(e) => updateLine(i, { quantity: e.target.value })}
                        onKeyDown={(e) => {
                          if (e.key === "Enter") {
                            e.preventDefault();
                            scannerRef.current?.focus();
                          }
                        }}
                      />
                      {destinationUsesLocations ? (
                        <Select
                          id={fieldId(`lines[${i}].destinationLocationId`)}
                          label={`${line.variant.sku} planlanan hedef rafı`}
                          size="lg"
                          placeholder="Teslim alırken seçilecek"
                          options={destinationOptions}
                          value={line.destinationLocationId}
                          error={onFields[`lines[${i}].destinationLocationId`]}
                          onChange={(e) => updateLine(i, { destinationLocationId: e.target.value })}
                        />
                      ) : null}
                    </div>
                  </li>
                );
              })}
            </ol>
          )}
        </section>

        <div className="sticky bottom-0 z-(--bos-z-sticky) -mx-4 flex flex-col gap-2 border-t border-line bg-card px-4 py-3 sm:flex-row sm:items-center sm:justify-between md:static md:mx-0 md:rounded-lg md:border md:shadow-e1">
          <p role="status" className="text-body text-fg-secondary">
            {values.lines.length} satır · toplam {formatQuantity(transferTotal(values.lines))}
          </p>
          <Button type="submit" size="lg" loading={submitting} icon={<Save className="size-5" />} className="w-full sm:w-auto">
            Taslağı kaydet
          </Button>
        </div>
      </form>
    </>
  );
}

function BackLink() {
  return (
    <Link href={TRANSFERS_HREF} className="inline-flex min-h-11 items-center gap-1 self-start text-body font-medium text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Transferler
    </Link>
  );
}

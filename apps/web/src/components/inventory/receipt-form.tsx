"use client";

import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { ArrowLeft, Lock, PackagePlus, Trash2 } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/api/client";
import { fieldId, partitionErrors } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { createReceipt } from "@/lib/inventory/api";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { BUSY_FINAL_MESSAGE, inventoryErrorMessage, inventoryFieldErrors, isBusy } from "@/lib/inventory/errors";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import { formatQuantity } from "@/lib/inventory/quantity";
import {
  REFERENCE_MAX,
  addVariantLine,
  buildReceiptRequest,
  emptyReceipt,
  receiptTotal,
  validateReceipt,
  type ReceiptErrors,
  type ReceiptFormValues,
} from "@/lib/inventory/receipt-form";
import { useSession } from "@/lib/session/session-context";
import { useLocations, useWarehouses } from "./use-inventory-data";
import { VariantScanner } from "./variant-scanner";


type Done = { lines: number; total: number; warehouseId: string };

/** W09 / M05 goods receipt: scan or search variants into lines, set quantities and locations, post once. */
export function ReceiptForm() {
  const { api, role } = useSession();
  const permissions = inventoryPermissions(role);
  const { state: warehouses } = useWarehouses();
  const activeWarehouses = useMemo(() => (warehouses.status === "ready" ? warehouses.data.filter((w) => w.status === "active") : []), [warehouses]);

  const [values, setValues] = useState<ReceiptFormValues>(emptyReceipt());
  const [defaultLocationId, setDefaultLocationId] = useState("");
  const [errors, setErrors] = useState<ReceiptErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [done, setDone] = useState<Done | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const nextKey = useRef(1);
  const alertRef = useRef<HTMLDivElement>(null);
  const scannerRef = useRef<HTMLInputElement>(null);

  // Pre-select the default (first active) warehouse once the list is known.
  const [initialized, setInitialized] = useState(false);
  if (!initialized && warehouses.status === "ready") {
    setInitialized(true);
    if (activeWarehouses.length > 0) setValues((v) => ({ ...v, warehouseId: activeWarehouses[0].id }));
  }

  const warehouse = activeWarehouses.find((w) => w.id === values.warehouseId) ?? null;
  const usesLocations = warehouse?.usesLocations ?? false;
  const { state: locations } = useLocations(values.warehouseId, { enabled: usesLocations });
  const locationOptions = locations.status === "ready" ? locations.data.map((l) => ({ value: l.id, label: l.barcode ? `${l.code} · ${l.barcode}` : l.code })) : [];

  useEffect(() => {
    if (formError || done) alertRef.current?.focus();
  }, [formError, done]);

  if (!permissions.receive) {
    return (
      <>
        <BackLink />
        <PageHeader title="Mal kabul" />
        <section className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Mal kabul yetkiniz yok"
            description="Mal kabulü işletme sahibi, yönetici, operasyon müdürü ve depo personeli yapabilir."
          />
        </section>
      </>
    );
  }

  const setWarehouse = (warehouseId: string) => {
    // Locations belong to a warehouse: a new warehouse clears every line's location.
    setValues((v) => ({ ...v, warehouseId, lines: v.lines.map((l) => ({ ...l, locationId: "" })) }));
    setDefaultLocationId("");
    setErrors({});
  };

  const updateLine = (index: number, patch: Partial<{ quantity: string; locationId: string }>) =>
    setValues((v) => ({ ...v, lines: v.lines.map((l, i) => (i === index ? { ...l, ...patch } : l)) }));

  const removeLine = (index: number) => {
    setValues((v) => ({ ...v, lines: v.lines.filter((_, i) => i !== index) }));
    // Line indexes shift: stale line errors would point at the wrong rows.
    setErrors((e) => Object.fromEntries(Object.entries(e).filter(([k]) => !k.startsWith("lines["))));
    scannerRef.current?.focus();
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setDone(null);
    setFormError(null);
    const clientErrors = validateReceipt(values, { usesLocations });
    if (!values.warehouseId && activeWarehouses.length > 0) clientErrors.warehouseId = "Depo seçin.";
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) {
      setFormError("Bazı alanlar eksik veya hatalı. İşaretli alanları düzeltin.");
      return;
    }
    const body = buildReceiptRequest(values, { usesLocations });
    const key = keys.keyFor(body);
    setSubmitting(true);
    try {
      await withBusyRetry(() => createReceipt(api, body, key), { onRetry: () => setRetrying(true) });
      keys.reset();
      setDone({ lines: values.lines.length, total: receiptTotal(values.lines), warehouseId: values.warehouseId });
      setValues((v) => ({ ...emptyReceipt(v.warehouseId) }));
      setErrors({});
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) {
        setErrors(inventoryFieldErrors(error));
        setFormError(error.userMessage);
      } else {
        setFormError(isBusy(error) ? BUSY_FINAL_MESSAGE : inventoryErrorMessage(error));
      }
    } finally {
      setSubmitting(false);
      setRetrying(false);
    }
  };

  const knownKeys = new Set<string>(["warehouseId", "reference", "lines"]);
  values.lines.forEach((_, i) => {
    knownKeys.add(`lines[${i}].quantity`);
    if (usesLocations) knownKeys.add(`lines[${i}].locationId`);
  });
  const { onFields, unmatched } = partitionErrors(errors, knownKeys);
  const total = receiptTotal(values.lines);

  return (
    <>
      <BackLink />
      <PageHeader title="Mal kabul" description="Gelen ürünleri okutun veya arayın, miktarı girin ve stoğa alın." />

      {done ? (
        <FormAlert
          ref={alertRef}
          tone="success"
          title="Mal kabul kaydedildi"
          actions={
            <Link href={`/stok?warehouseId=${encodeURIComponent(done.warehouseId)}`} className="text-body font-semibold text-link underline-offset-2 hover:underline">
              Stokta gör
            </Link>
          }
        >
          {done.lines} satır, toplam {formatQuantity(done.total)} birim stoğa girdi. Sıradaki mal kabul için okutmaya devam edebilirsiniz.
        </FormAlert>
      ) : null}
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Mal kabul kaydedilemedi">
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

      <form aria-label="Mal kabul" noValidate onSubmit={submit} className="flex flex-col gap-4 pb-24 md:pb-0">
        <section aria-label="Depo ve belge" className="grid grid-cols-1 gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 md:grid-cols-2">
          <Select
            id={fieldId("warehouseId")}
            label="Depo"
            size="lg"
            required
            options={activeWarehouses.map((w) => ({ value: w.id, label: `${w.name} (${w.code})${w.isDefault ? " — varsayılan" : ""}` }))}
            placeholder={activeWarehouses.length === 0 ? (warehouses.status === "loading" ? "Yükleniyor…" : "Varsayılan depo (otomatik oluşturulur)") : undefined}
            value={values.warehouseId}
            disabled={warehouses.status !== "ready"}
            error={onFields.warehouseId}
            hint={warehouses.status === "ready" && activeWarehouses.length === 0 ? "Henüz depo yok: ilk mal kabulde \"Ana Depo\" otomatik oluşturulur." : undefined}
            onChange={(e) => setWarehouse(e.target.value)}
          />
          <Input
            id={fieldId("reference")}
            label="Referans (irsaliye / fatura no)"
            size="lg"
            maxLength={REFERENCE_MAX}
            value={values.reference}
            error={onFields.reference}
            hint="İsteğe bağlı. Hareket geçmişinde görünür."
            onChange={(e) => setValues((v) => ({ ...v, reference: e.target.value }))}
          />
          {warehouses.status === "error" ? <p className="text-caption text-danger-fg md:col-span-2">Depolar yüklenemedi: {warehouses.message}</p> : null}
          {usesLocations ? (
            <Select
              label="Okutulan ürünlerin rafı"
              size="lg"
              placeholder="Satırda seçilecek"
              options={locationOptions}
              value={defaultLocationId}
              disabled={locations.status !== "ready"}
              hint="Okuttuğunuz her yeni satıra bu raf/göz atanır; satırda değiştirebilirsiniz."
              onChange={(e) => setDefaultLocationId(e.target.value)}
            />
          ) : null}
        </section>

        <section aria-labelledby="receipt-lines" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
          <h2 id="receipt-lines" className="text-h3 text-fg">
            Ürünler
          </h2>
          <VariantScanner
            inputRef={scannerRef}
            label="Barkod veya SKU okutun"
            hint="El terminali veya barkod okuyucu ile okutun; elle yazıp Enter'a da basabilirsiniz."
            error={onFields.lines}
            onPick={(variant) => {
              setValues((v) => ({
                ...v,
                lines: addVariantLine(v.lines, variant, usesLocations ? defaultLocationId : "", () => `line-${nextKey.current++}`).lines,
              }));
              setErrors((e) => Object.fromEntries(Object.entries(e).filter(([k]) => k !== "lines")));
              setDone(null);
            }}
            pickedText={(variant) => `${variant.sku} eklendi.`}
          />

          {values.lines.length === 0 ? (
            <p className="rounded-md border border-dashed border-line-strong p-4 text-center text-body text-fg-muted">Henüz ürün eklenmedi.</p>
          ) : (
            <ol aria-label="Mal kabul satırları" className="flex flex-col gap-3">
              {values.lines.map((line, i) => {
                const label = line.variant.name ? `${line.variant.sku} — ${line.variant.name}` : line.variant.sku;
                return (
                  <li key={line.key} className="flex flex-col gap-3 rounded-md border border-line p-3">
                    <div className="flex items-start justify-between gap-2">
                      <div className="flex min-w-0 flex-col">
                        <span className="font-mono text-body font-semibold text-fg">{line.variant.sku}</span>
                        {line.variant.name || line.variant.barcode ? (
                          <span className="text-caption text-fg-muted">{[line.variant.name, line.variant.barcode].filter(Boolean).join(" · ")}</span>
                        ) : null}
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
                    <div className={usesLocations ? "grid grid-cols-1 gap-3 sm:grid-cols-2" : "grid grid-cols-1 gap-3 sm:max-w-xs"}>
                      <Input
                        id={fieldId(`lines[${i}].quantity`)}
                        label={`${label} miktarı`}
                        size="lg"
                        inputMode="decimal"
                        autoComplete="off"
                        value={line.quantity}
                        error={onFields[`lines[${i}].quantity`]}
                        onChange={(e) => updateLine(i, { quantity: e.target.value })}
                        onKeyDown={(e) => {
                          // Enter in a quantity goes back to the scanner instead of submitting the receipt.
                          if (e.key === "Enter") {
                            e.preventDefault();
                            scannerRef.current?.focus();
                          }
                        }}
                      />
                      {usesLocations ? (
                        <Select
                          id={fieldId(`lines[${i}].locationId`)}
                          label={`${line.variant.sku} rafı`}
                          size="lg"
                          placeholder="Raf/göz seçin"
                          options={locationOptions}
                          value={line.locationId}
                          error={onFields[`lines[${i}].locationId`]}
                          onChange={(e) => updateLine(i, { locationId: e.target.value })}
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
            {retrying ? "Stok kayıtları meşgul, otomatik olarak yeniden deneniyor…" : `${values.lines.length} satır · toplam ${formatQuantity(total)} birim`}
          </p>
          <Button type="submit" size="lg" loading={submitting} icon={<PackagePlus className="size-5" />} className="w-full sm:w-auto">
            Mal kabulü kaydet
          </Button>
        </div>
      </form>
    </>
  );
}

function BackLink() {
  return (
    <Link href="/depo" className="inline-flex min-h-11 items-center gap-1 self-start text-body font-medium text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Depoya dön
    </Link>
  );
}

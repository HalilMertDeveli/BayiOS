"use client";

import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { ArrowLeft, Lock, SlidersHorizontal, X } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { ApiError } from "@/lib/api/client";
import { fieldId, partitionErrors } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { REASON_MAX, buildAdjustmentRequest, emptyAdjustment, validateAdjustment, type AdjustmentFormValues } from "@/lib/inventory/adjustment-form";
import { balanceQuery, createAdjustment, getVariant, listBalances, type Balance, type CreateAdjustmentRequest } from "@/lib/inventory/api";
import { variantStockHref } from "@/lib/inventory/balance-query";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import {
  inventoryErrorMessage,
  inventoryFieldErrors,
  BUSY_FINAL_MESSAGE,
  isBusy,
  needsNegativeStockConfirmation,
  readShortages,
  type Shortage,
} from "@/lib/inventory/errors";
import { ADJUSTMENT_TYPES, adjustmentTypeLabels, type AdjustmentType } from "@/lib/inventory/labels";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import { formatQuantity } from "@/lib/inventory/quantity";
import { useSession } from "@/lib/session/session-context";
import { NegativeStockDialog } from "./negative-stock-dialog";
import { AvailableQuantity } from "./quantity-cells";
import { useLocations, useWarehouses } from "./use-inventory-data";
import { VariantScanner } from "./variant-scanner";

type Done = { sku: string; balanceAfter: number | string };

/**
 * Manual stock adjustment with a mandatory reason (§4.2). Reserved stock cannot be adjusted away; the tenant's
 * negative-stock policy decides what happens below available: `block` refuses, `warn` asks for confirmation here.
 */
export function AdjustmentForm() {
  const { api, access } = useSession();
  const permissions = inventoryPermissions(access);
  const searchParams = useSearchParams();
  const prefill = useMemo(
    () => ({
      variantId: searchParams.get("variantId") ?? "",
      warehouseId: searchParams.get("warehouseId") ?? "",
      locationId: searchParams.get("locationId") ?? "",
    }),
    [searchParams],
  );
  const { state: warehouses } = useWarehouses();
  const activeWarehouses = useMemo(() => (warehouses.status === "ready" ? warehouses.data.filter((w) => w.status === "active") : []), [warehouses]);

  const [values, setValues] = useState<AdjustmentFormValues>(() => ({ ...emptyAdjustment(prefill.warehouseId), locationId: prefill.locationId }));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [confirmation, setConfirmation] = useState<Shortage[] | null>(null);
  const [done, setDone] = useState<Done | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);

  // Pre-fill the variant from ?variantId= (row action on /stok).
  useEffect(() => {
    if (!prefill.variantId) return;
    const controller = new AbortController();
    getVariant(api, prefill.variantId, controller.signal)
      .then((v) => {
        if (!controller.signal.aborted)
          setValues((prev) => (prev.variant ? prev : { ...prev, variant: { id: v.id, sku: v.sku, name: v.name ?? null, barcode: v.barcode ?? null } }));
      })
      .catch(() => undefined);
    return () => controller.abort();
  }, [api, prefill.variantId]);

  // No warehouse in the URL: take the default (first active) one.
  const [initialized, setInitialized] = useState(false);
  if (!initialized && warehouses.status === "ready") {
    setInitialized(true);
    if (!values.warehouseId && activeWarehouses.length > 0) setValues((v) => ({ ...v, warehouseId: activeWarehouses[0].id }));
  }

  const warehouse = activeWarehouses.find((w) => w.id === values.warehouseId) ?? null;
  const usesLocations = warehouse?.usesLocations ?? false;
  const { state: locations } = useLocations(values.warehouseId, { enabled: usesLocations });

  // Current balance of the selected variant/warehouse/location, so the user sees what they are correcting.
  const [current, setCurrent] = useState<{ key: string; balance: Balance | null } | null>(null);
  const [balanceVersion, setBalanceVersion] = useState(0);
  const balanceKey = values.variant && values.warehouseId && (!usesLocations || values.locationId)
    ? `${values.variant.id}|${values.warehouseId}|${usesLocations ? values.locationId : ""}|${balanceVersion}`
    : "";
  useEffect(() => {
    if (!balanceKey) return;
    const [variantId, warehouseId, locationId] = balanceKey.split("|");
    const controller = new AbortController();
    listBalances(api, balanceQuery({ variantId, warehouseId, locationId: locationId || null }, null, 10), controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        // Without locations the balance row is the one with location_id = NULL.
        const row = page.items.find((b) => (locationId ? b.locationId === locationId : !b.locationId)) ?? null;
        setCurrent({ key: balanceKey, balance: row });
      })
      .catch(() => {
        if (!controller.signal.aborted) setCurrent({ key: balanceKey, balance: null });
      });
    return () => controller.abort();
  }, [api, balanceKey]);
  const currentBalance = current && current.key === balanceKey ? current : null;

  useEffect(() => {
    if (formError || done) alertRef.current?.focus();
  }, [formError, done]);

  if (!permissions.adjust) {
    return (
      <>
        <BackLink />
        <PageHeader title="Stok düzeltme" />
        <section className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Stok düzeltme yetkiniz yok"
            description="Rolünüz manuel stok düzeltmeye izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
          />
        </section>
      </>
    );
  }

  const set = (patch: Partial<AdjustmentFormValues>) => {
    setValues((v) => ({ ...v, ...patch }));
    setDone(null);
  };

  const send = async (body: CreateAdjustmentRequest) => {
    const key = keys.keyFor(body);
    return withBusyRetry(() => createAdjustment(api, body, key), { onRetry: () => setRetrying(true) });
  };

  const handleError = (error: unknown) => {
    if (needsNegativeStockConfirmation(error)) {
      setConfirmation(readShortages(error));
      return;
    }
    if (error instanceof ApiError && error.status === 422) {
      setErrors(inventoryFieldErrors(error));
      setFormError(error.userMessage);
      return;
    }
    if (isBusy(error)) {
      setFormError(BUSY_FINAL_MESSAGE);
      return;
    }
    const shortage = readShortages(error)[0];
    const base = inventoryErrorMessage(error);
    setFormError(shortage ? `${base} Satılabilir: ${formatQuantity(shortage.available)}, istenen: ${formatQuantity(shortage.requested)}.` : base);
  };

  const succeed = (balanceAfter: number | string) => {
    keys.reset();
    setDone({ sku: values.variant?.sku ?? "", balanceAfter });
    setValues((v) => ({ ...v, quantity: "", reason: "" }));
    setErrors({});
    setBalanceVersion((n) => n + 1);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setDone(null);
    setFormError(null);
    const clientErrors = validateAdjustment(values, { usesLocations });
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) {
      setFormError("Bazı alanlar eksik veya hatalı. İşaretli alanları düzeltin.");
      return;
    }
    setSubmitting(true);
    try {
      const movement = await send(buildAdjustmentRequest(values, { usesLocations, confirmNegativeStock: false }));
      succeed(movement.balanceAfter);
    } catch (error) {
      handleError(error);
    } finally {
      setSubmitting(false);
      setRetrying(false);
    }
  };

  const confirmNegative = async () => {
    setSubmitting(true);
    try {
      // Same values, now with the explicit confirmation: a different body, so a new Idempotency-Key.
      const movement = await send(buildAdjustmentRequest(values, { usesLocations, confirmNegativeStock: true }));
      setConfirmation(null);
      succeed(movement.balanceAfter);
    } catch (error) {
      setConfirmation(null);
      handleError(error);
    } finally {
      setSubmitting(false);
      setRetrying(false);
    }
  };

  const knownKeys = new Set(["variantId", "warehouseId", "type", "quantity", "reason", ...(usesLocations ? ["locationId"] : [])]);
  const { onFields, unmatched } = partitionErrors(errors, knownKeys);
  const locationOptions = locations.status === "ready" ? locations.data.map((l) => ({ value: l.id, label: l.code })) : [];

  return (
    <>
      <BackLink />
      <PageHeader title="Stok düzeltme" description="Sayım farkı, hasar veya iade gibi durumlarda stoğu sebebiyle birlikte düzeltin." />

      {done ? (
        <FormAlert
          ref={alertRef}
          tone="success"
          title="Stok düzeltildi"
          actions={
            values.variant ? (
              <Link href={variantStockHref(values.variant.id)} className="text-body font-semibold text-link underline-offset-2 hover:underline">
                Hareket geçmişini gör
              </Link>
            ) : undefined
          }
        >
          {done.sku} için yeni rafta bakiye: {formatQuantity(done.balanceAfter)}.
        </FormAlert>
      ) : null}
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Düzeltme kaydedilemedi">
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

      <form aria-label="Stok düzeltme" noValidate onSubmit={submit} className="flex max-w-3xl flex-col gap-4">
        <section aria-label="Ürün" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
          {values.variant ? (
            <div className="flex items-start justify-between gap-2" id={fieldId("variantId")}>
              <div className="flex flex-col">
                <span className="text-caption text-fg-muted">Ürün</span>
                <span className="font-mono text-body font-semibold text-fg">{values.variant.sku}</span>
                {values.variant.name ? <span className="text-caption text-fg-secondary">{values.variant.name}</span> : null}
              </div>
              <Button variant="ghost" size="lg" icon={<X className="size-5" />} onClick={() => set({ variant: null })}>
                Ürünü değiştir
              </Button>
            </div>
          ) : (
            <VariantScanner
              id={fieldId("variantId")}
              label="Ürün (barkod veya SKU)"
              hint="Okutun veya yazıp Enter'a basın."
              error={onFields.variantId}
              onPick={(variant) => set({ variant })}
            />
          )}
        </section>

        <section aria-label="Düzeltme" className="grid grid-cols-1 gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 md:grid-cols-2">
          <Select
            id={fieldId("warehouseId")}
            label="Depo"
            size="lg"
            required
            placeholder={activeWarehouses.length === 0 ? "Depo yok" : "Depo seçin"}
            options={activeWarehouses.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }))}
            value={values.warehouseId}
            disabled={warehouses.status !== "ready"}
            error={onFields.warehouseId}
            onChange={(e) => set({ warehouseId: e.target.value, locationId: "" })}
          />
          {usesLocations ? (
            <Select
              id={fieldId("locationId")}
              label="Raf/göz"
              size="lg"
              required
              placeholder="Raf/göz seçin"
              options={locationOptions}
              value={values.locationId}
              disabled={locations.status !== "ready"}
              error={onFields.locationId}
              onChange={(e) => set({ locationId: e.target.value })}
            />
          ) : null}
          <Select
            id={fieldId("type")}
            label="Düzeltme türü"
            size="lg"
            required
            placeholder="Seçin"
            options={ADJUSTMENT_TYPES.map((t) => ({ value: t, label: adjustmentTypeLabels[t] }))}
            value={values.type}
            error={onFields.type}
            onChange={(e) => set({ type: e.target.value as AdjustmentType | "" })}
          />
          <Input
            id={fieldId("quantity")}
            label="Miktar"
            size="lg"
            required
            inputMode="decimal"
            autoComplete="off"
            value={values.quantity}
            error={onFields.quantity}
            hint="Pozitif girin; yönü düzeltme türü belirler."
            onChange={(e) => set({ quantity: e.target.value })}
          />
          <Textarea
            id={fieldId("reason")}
            label="Sebep"
            required
            className="md:col-span-2"
            maxLength={REASON_MAX}
            value={values.reason}
            error={onFields.reason}
            hint="Zorunlu. Hareket geçmişinde görünür (ör. “Sayım farkı, raf A-01”)."
            onChange={(e) => set({ reason: e.target.value })}
          />
          {currentBalance ? (
            <div role="status" className="rounded-md bg-sunken p-3 text-body text-fg-secondary md:col-span-2">
              {currentBalance.balance ? (
                <>
                  Mevcut: rafta {formatQuantity(currentBalance.balance.onHand)}, ayrılmış {formatQuantity(currentBalance.balance.reserved)}, satılabilir{" "}
                  <AvailableQuantity value={currentBalance.balance.available} />
                </>
              ) : (
                "Bu depoda/rafta henüz stok kaydı yok."
              )}
            </div>
          ) : null}
        </section>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <p role="status" className="text-body text-fg-secondary">
            {retrying ? "Stok kayıtları meşgul, otomatik olarak yeniden deneniyor…" : ""}
          </p>
          <Button type="submit" size="lg" loading={submitting && !confirmation} icon={<SlidersHorizontal className="size-5" />} className="w-full sm:w-auto">
            Düzeltmeyi kaydet
          </Button>
        </div>
      </form>

      <NegativeStockDialog
        open={confirmation !== null}
        shortages={confirmation ?? []}
        skuOf={(variantId, fallback) => (values.variant?.id === variantId ? values.variant.sku : (fallback ?? "Ürün"))}
        confirming={submitting}
        onCancel={() => {
          setConfirmation(null);
          setFormError("Düzeltme kaydedilmedi: eksi stok onaylanmadı.");
        }}
        onConfirm={confirmNegative}
      />
    </>
  );
}

function BackLink() {
  return (
    <Link href="/stok" className="inline-flex min-h-11 items-center gap-1 self-start text-body font-medium text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Stok listesine dön
    </Link>
  );
}

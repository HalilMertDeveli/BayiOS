"use client";

import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { ArrowLeft, ArrowRightLeft, Lock } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { balanceQuery, getVariant, listBalances, type Balance } from "@/lib/inventory/api";
import { withBusyRetry } from "@/lib/inventory/busy-retry";
import { readShortages, type Shortage } from "@/lib/inventory/errors";
import { formatQuantity, parseQuantity, toNumber } from "@/lib/inventory/quantity";
import type { VariantRef } from "@/lib/inventory/receipt-form";
import { createRelocation, summariseReservationMoves } from "@/lib/stock-documents/api";
import { BUSY_FINAL_MESSAGE, documentErrorMessage, documentFieldErrors, isDocumentBusy } from "@/lib/stock-documents/errors";
import { transferPermissions } from "@/lib/stock-documents/permissions";
import { useSession } from "@/lib/session/session-context";
import { useLocations, useWarehouses } from "@/components/inventory/use-inventory-data";
import { VariantScanner } from "@/components/inventory/variant-scanner";
import { ShortageList } from "./shortage-list";

type Errors = Partial<Record<"warehouseId" | "variant" | "from" | "to" | "quantity" | "reference", string>>;
type Done = {
  sku: string;
  quantity: number;
  from: string;
  to: string;
  warehouseId: string;
  /** Reserved quantity carried along with its reservations (#98 includeReserved) and the orders it belongs to. */
  reservedMoved: number;
  reservedOrders: number;
};

const RESERVED_SHORTAGE_MESSAGE =
  "Kaynak rafta taşınabilecek stok yetersiz (satılabilir + rafta duran ayrılmış ürün). Toplanmış, paketlenmiş veya eksik bildirilmiş ürün taşınmaz; miktarı azaltın.";

/** API error keys of POST /inventory/relocations (one line) → this form's fields. */
const FIELD_OF: Record<string, keyof Errors> = {
  warehouseId: "warehouseId",
  "lines[0].variantId": "variant",
  "lines[0].fromLocationId": "from",
  "lines[0].from": "from",
  "lines[0].toLocationId": "to",
  "lines[0].to": "to",
  "lines[0].quantity": "quantity",
  reference: "reference",
};

/** /stok/raf-tasima: instant shelf-to-shelf move inside one warehouse (transfer_out + transfer_in in one transaction). */
export function RelocationForm() {
  const { api, access } = useSession();
  const permissions = transferPermissions(access);
  const params = useSearchParams();
  const { state: warehouses } = useWarehouses();
  const located = useMemo(() => (warehouses.status === "ready" ? warehouses.data.filter((w) => w.status === "active" && w.usesLocations) : []), [warehouses]);

  const [warehouseId, setWarehouseId] = useState(params.get("warehouseId") ?? "");
  const [variant, setVariant] = useState<VariantRef | null>(null);
  const [from, setFrom] = useState(params.get("fromLocationId") ?? "");
  const [to, setTo] = useState("");
  const [quantity, setQuantity] = useState("");
  const [reference, setReference] = useState("");
  const [includeReserved, setIncludeReserved] = useState(false);
  const [errors, setErrors] = useState<Errors>({});
  const [formError, setFormError] = useState<{ text: string; shortages: Shortage[] } | null>(null);
  const [done, setDone] = useState<Done | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [moves, setMoves] = useState(0);
  const [balances, setBalances] = useState<{ key: string; items: Balance[] } | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);

  // Pre-fill (from a balance row): the variant by id; otherwise the first located warehouse.
  const prefillVariant = params.get("variantId");
  useEffect(() => {
    if (!prefillVariant) return;
    const controller = new AbortController();
    getVariant(api, prefillVariant, controller.signal)
      .then((v) => !controller.signal.aborted && setVariant({ id: v.id, sku: v.sku, name: v.name ?? null, barcode: v.barcode ?? null }))
      .catch(() => undefined);
    return () => controller.abort();
  }, [api, prefillVariant]);
  const [initialized, setInitialized] = useState(false);
  if (!initialized && warehouses.status === "ready") {
    setInitialized(true);
    if (!located.some((w) => w.id === warehouseId)) setWarehouseId(located[0]?.id ?? "");
  }

  const { state: locations } = useLocations(warehouseId);
  const codeOf = (id: string) => (locations.status === "ready" ? (locations.data.find((l) => l.id === id)?.code ?? "…") : "…");
  const balanceKey = variant && warehouseId ? `${variant.id}|${warehouseId}|${moves}` : "";
  useEffect(() => {
    if (!variant || !warehouseId) return;
    const controller = new AbortController();
    listBalances(api, balanceQuery({ variantId: variant.id, warehouseId }, null, 200), controller.signal)
      .then((page) => !controller.signal.aborted && setBalances({ key: balanceKey, items: page.items }))
      .catch(() => !controller.signal.aborted && setBalances({ key: balanceKey, items: [] }));
    return () => controller.abort();
  }, [api, variant, warehouseId, balanceKey]);
  // With "move reserved too" a shelf holding only reserved goods is a valid source as well.
  const movableOf = (b: Balance) => toNumber(b.available) + (includeReserved ? Math.max(toNumber(b.reserved), 0) : 0);
  const rows = balances?.key === balanceKey ? balances.items.filter((b) => b.locationId && movableOf(b) > 0) : [];
  const rowAt = (locationId: string) => rows.find((b) => b.locationId === locationId);
  const availableAt = (locationId: string) => toNumber(rowAt(locationId)?.available ?? 0);
  const reservedAt = (locationId: string) => Math.max(toNumber(rowAt(locationId)?.reserved ?? 0), 0);
  /** Upper bound shown before sending; the API also leaves out reserved goods already picked, packed or reported missing. */
  const movableAt = (locationId: string) => {
    const row = rowAt(locationId);
    return row ? movableOf(row) : 0;
  };

  useEffect(() => {
    if (formError || done) alertRef.current?.focus();
  }, [formError, done]);

  if (!permissions.relocate) {
    return (
      <>
        <BackLink />
        <PageHeader title="Raf taşıma" />
        <section className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Raf taşıma yetkiniz yok"
            description="Raflar arası taşımayı işletme sahibi, yönetici, operasyon müdürü veya depo personeli yapar."
          />
        </section>
      </>
    );
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setDone(null);
    setFormError(null);
    const next: Errors = {};
    if (!warehouseId) next.warehouseId = "Depo seçin.";
    if (!variant) next.variant = "Taşınacak ürünü okutun veya arayın.";
    if (!from) next.from = "Kaynak rafı seçin.";
    if (!to) next.to = "Hedef rafı seçin.";
    else if (to === from) next.to = "Kaynak ve hedef raf farklı olmalı.";
    const parsed = parseQuantity(quantity);
    if (!parsed.ok) next.quantity = parsed.message;
    else if (from && parsed.value > movableAt(from))
      next.quantity = includeReserved
        ? `Bu rafta en fazla ${formatQuantity(movableAt(from))} taşınabilir (satılabilir ${formatQuantity(availableAt(from))} + ayrılmış ${formatQuantity(reservedAt(from))}).`
        : `Bu rafta satılabilir yalnızca ${formatQuantity(availableAt(from))}. Ayrılmış stoğu da taşımak için “Ayrılmış (rezerve) ürünleri de taşı” seçeneğini işaretleyin.`;
    setErrors(next);
    if (Object.keys(next).length > 0 || !variant || !parsed.ok) return;
    const body = {
      warehouseId,
      reference: reference.trim() || null,
      lines: [{ variantId: variant.id, fromLocationId: from, toLocationId: to, quantity: parsed.value, includeReserved }],
    };
    const key = keys.keyFor(body);
    setSubmitting(true);
    try {
      const result = await withBusyRetry(() => createRelocation(api, body, key), { isBusy: isDocumentBusy });
      keys.reset();
      setMoves((n) => n + 1);
      const carried = summariseReservationMoves(result.reservationMoves);
      setDone({ sku: variant.sku, quantity: parsed.value, from: codeOf(from), to: codeOf(to), warehouseId, reservedMoved: carried.quantity, reservedOrders: carried.orders });
      setQuantity("");
      setFrom("");
      setTo("");
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) {
        const mapped: Errors = {};
        for (const [k, v] of Object.entries(documentFieldErrors(error))) mapped[FIELD_OF[k] ?? "quantity"] = v;
        setErrors(mapped);
      }
      const text = isDocumentBusy(error)
        ? BUSY_FINAL_MESSAGE
        : includeReserved && error instanceof ApiError && error.code === "inventory.insufficient_available"
          ? RESERVED_SHORTAGE_MESSAGE
          : documentErrorMessage(error);
      setFormError({ text, shortages: readShortages(error) });
    } finally {
      setSubmitting(false);
    }
  };

  const fromOptions = rows.map((b) => ({
    value: b.locationId!,
    label: `${codeOf(b.locationId!)} · satılabilir ${formatQuantity(b.available)}${includeReserved && toNumber(b.reserved) > 0 ? ` · ayrılmış ${formatQuantity(b.reserved)}` : ""}`,
  }));
  const toOptions = locations.status === "ready" ? locations.data.filter((l) => l.id !== from).map((l) => ({ value: l.id, label: l.code })) : [];

  return (
    <>
      <BackLink />
      <PageHeader title="Raf taşıma" description="Aynı depoda bir raftan diğerine anında taşıma. Depolar arası gönderim için transfer kullanın." />
      {done ? (
        <FormAlert
          ref={alertRef}
          tone="success"
          title="Taşındı"
          actions={
            <Link href={`/stok?warehouseId=${encodeURIComponent(done.warehouseId)}`} className="text-body font-semibold text-link underline-offset-2 hover:underline">
              Stokta gör
            </Link>
          }
        >
          <p>
            {done.sku}: {formatQuantity(done.quantity)} birim {done.from} rafından {done.to} rafına taşındı.
          </p>
          {done.reservedMoved > 0 ? (
            <p>
              Bunun {formatQuantity(done.reservedMoved)} birimi {done.reservedOrders} siparişe ayrılmıştı; rezervasyon ve açık toplama görevleri {done.to} rafına
              yönlendirildi.
            </p>
          ) : null}
        </FormAlert>
      ) : null}
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Taşıma yapılamadı">
          <p>{formError.text}</p>
          <ShortageList shortages={formError.shortages} shelfCode={codeOf} />
        </FormAlert>
      ) : null}
      {warehouses.status === "ready" && located.length === 0 ? (
        <FormAlert tone="info" title="Raf/göz kullanan depo yok">
          Raf taşıma yalnızca raf/göz takibi açık depolarda yapılır. Depo sayfasından bir depoda raf/göz takibini açın.
        </FormAlert>
      ) : null}
      <form aria-label="Raf taşıma" noValidate onSubmit={submit} className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1">
        <Select
          label="Depo"
          size="lg"
          required
          placeholder={warehouses.status === "loading" ? "Yükleniyor…" : "Seçin"}
          options={located.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }))}
          value={warehouseId}
          error={errors.warehouseId}
          disabled={warehouses.status !== "ready"}
          onChange={(e) => {
            setWarehouseId(e.target.value);
            setFrom("");
            setTo("");
          }}
        />
        <VariantScanner
          label="Ürün (barkod veya SKU)"
          hint="Okutun veya yazıp Enter'a basın."
          error={errors.variant}
          onPick={(v) => {
            setVariant(v);
            setFrom("");
            setDone(null);
          }}
          pickedText={(v) => `${v.sku} seçildi.`}
        />
        {variant ? (
          <p className="text-body text-fg">
            Seçili ürün: <span className="font-mono font-semibold">{variant.sku}</span>
            {variant.name ? <span className="text-fg-muted"> · {variant.name}</span> : null}
          </p>
        ) : null}
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <Select
            label="Kaynak raf"
            size="lg"
            required
            placeholder={variant && balances?.key === balanceKey && rows.length === 0 ? "Bu üründe satılabilir stok yok" : "Seçin"}
            options={fromOptions}
            value={from}
            error={errors.from}
            disabled={!variant}
            onChange={(e) => setFrom(e.target.value)}
          />
          <Select label="Hedef raf" size="lg" required placeholder="Seçin" options={toOptions} value={to} error={errors.to} disabled={!variant} onChange={(e) => setTo(e.target.value)} />
          <Input
            label="Miktar"
            size="lg"
            required
            inputMode="decimal"
            autoComplete="off"
            value={quantity}
            error={errors.quantity}
            hint={
              from
                ? includeReserved
                  ? `En fazla ${formatQuantity(movableAt(from))} (satılabilir ${formatQuantity(availableAt(from))} + ayrılmış ${formatQuantity(reservedAt(from))})`
                  : `Satılabilir ${formatQuantity(availableAt(from))}`
                : undefined
            }
            onChange={(e) => setQuantity(e.target.value)}
          />
        </div>
        <CheckboxField
          label="Ayrılmış (rezerve) ürünleri de taşı"
          checked={includeReserved}
          onChange={(value) => {
            setIncludeReserved(value);
            // A shelf with only reserved goods is no longer a source once the option is off.
            if (!value && from && availableAt(from) <= 0) setFrom("");
            setErrors((e) => ({ ...e, quantity: undefined }));
          }}
          hint={
            includeReserved
              ? "Satılabilir stok yetmezse kalan, siparişlere ayrılmış ve hâlâ rafta duran üründen alınır; rezervasyon ve açık toplama görevleri hedef rafa yönlendirilir. Toplanmış, paketlenmiş veya eksik bildirilmiş ürün taşınmaz."
              : "Kapalıyken yalnızca satılabilir stok taşınır; siparişlere ayrılmış ürün rafında kalır."
          }
        />
        <Input label="Not" maxLength={200} value={reference} error={errors.reference} hint="İsteğe bağlı; hareket geçmişinde görünür." onChange={(e) => setReference(e.target.value)} />
        <Button type="submit" size="lg" className="w-full sm:w-auto sm:self-start" icon={<ArrowRightLeft className="size-5" />} loading={submitting}>
          Taşı
        </Button>
      </form>
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

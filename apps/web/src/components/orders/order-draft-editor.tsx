"use client";

import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { RotateCcw, Save, X } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { CustomerPicker } from "@/components/customers/customer-picker";
import { VariantScanner } from "@/components/inventory/variant-scanner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { partitionErrors } from "@/lib/catalog/errors";
import { createOrder, orderHref, updateOrder, type LoadedOrder, type OrderLine } from "@/lib/orders/api";
import {
  buildCreateRequest,
  buildUpdatePatch,
  discountKey,
  draftValuesFromDetail,
  isDirty,
  newDraftLine,
  newDraftValues,
  validateDraft,
  type DiscountMode,
  type DraftLine,
  type DraftValues,
  type TaxMode,
} from "@/lib/orders/draft-form";
import { isOrderVersionConflict, isStaleState, orderErrorMessage, orderFieldErrors } from "@/lib/orders/errors";
import type { OrderPermissions } from "@/lib/orders/permissions";
import { formatMoney } from "@/lib/pricing/money";
import { useSession } from "@/lib/session/session-context";
import { LinePrice, lineDiscountText } from "./order-lines";
import { useCustomerNames, useWarehouses } from "./use-order-reference";

const TAX_MODE_OPTIONS: { value: TaxMode; label: string }[] = [
  { value: "", label: "Fiyat listesine göre" },
  { value: "excluded", label: "KDV hariç" },
  { value: "included", label: "KDV dahil" },
];

const DISCOUNT_OPTIONS: { value: DiscountMode; label: string }[] = [
  { value: "none", label: "İskonto yok / grup iskontosu" },
  { value: "percent", label: "Oran (%)" },
  { value: "amount", label: "Tutar" },
];

/**
 * Draft order form for /siparisler/yeni (no `loaded`) and the draft on /siparisler/[id]. Lines are added with the
 * VariantScanner; prices, discounts, VAT and totals are resolved by the API on save and shown from its response — a
 * line without a price stays "Fiyat yok" and blocks submit. Manual prices and discounts only with orders.price.override.
 */
export function OrderDraftEditor({
  loaded,
  permissions,
  unpricedLineIndexes = [],
  onSaved,
  onDirtyChange,
  onReload,
}: {
  loaded: LoadedOrder | null;
  permissions: OrderPermissions;
  /** 0-based indexes the API reported without a price (order.price_missing on submit). */
  unpricedLineIndexes?: readonly number[];
  onSaved?: (next: LoadedOrder) => void;
  onDirtyChange?: (dirty: boolean) => void;
  onReload?: () => void;
}) {
  const { api } = useSession();
  const router = useRouter();
  const { toast } = useToast();
  const warehouses = useWarehouses();
  const options = useMemo(() => ({ overridePrice: permissions.overridePrice }), [permissions.overridePrice]);
  const customerNames = useCustomerNames(loaded ? [loaded.detail.order.customerId] : []);

  const original = useMemo(() => (loaded ? draftValuesFromDetail(loaded.detail) : null), [loaded]);
  const [values, setValues] = useState<DraftValues>(() => original ?? newDraftValues());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [formError, setFormError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);

  // New draft: preselect the default warehouse once the list is in.
  const defaultWarehouse = warehouses.status === "ready" ? (warehouses.items.find((w) => w.isDefault && w.status === "active") ?? null) : null;
  if (!loaded && !values.warehouseId && defaultWarehouse) setValues((v) => ({ ...v, warehouseId: defaultWarehouse.id }));

  const dirty = original ? isDirty(original, values, options) : values.lines.length > 0 || values.customer !== null;
  useEffect(() => onDirtyChange?.(dirty), [dirty, onDirtyChange]);

  const serverLines = useMemo(() => new Map((loaded?.detail.lines ?? []).map((l) => [l.id, l] as const)), [loaded]);
  const currency = loaded?.detail.order.currency ?? "";
  const customerShown = values.customer
    ? {
        id: values.customer.id,
        name: values.customer.name ?? customerNames.get(values.customer.id)?.name ?? null,
        code: values.customer.code ?? customerNames.get(values.customer.id)?.code ?? null,
      }
    : null;

  const set = (patch: Partial<DraftValues>) => setValues((v) => ({ ...v, ...patch }));
  const setLine = (key: string, patch: Partial<DraftLine>) => setValues((v) => ({ ...v, lines: v.lines.map((l) => (l.key === key ? { ...l, ...patch } : l)) }));

  const lineKeys = values.lines.flatMap((_, i) => [`lines[${i}].quantity`, `lines[${i}].unitPrice`, `lines[${i}].discountPercent`, `lines[${i}].discountAmount`, `lines[${i}].variantId`]);
  const knownKeys = new Set(["customerId", "warehouseId", "notes", "requestedDeliveryDate", ...lineKeys]);

  const showErrors = (fieldErrors: Record<string, string>) => {
    const { onFields, unmatched } = partitionErrors(fieldErrors, knownKeys);
    setErrors(onFields);
    setSummary(unmatched);
  };

  const fail = (message: string) => {
    setFormError(message);
    requestAnimationFrame(() => alertRef.current?.focus());
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    setFormError(null);
    setSummary([]);
    const local = validateDraft(values, options);
    showErrors(local);
    if (Object.keys(local).length > 0) {
      fail("Bazı alanlar eksik veya hatalı. İşaretli alanları düzeltin.");
      return;
    }
    setSaving(true);
    try {
      if (!loaded) {
        const body = buildCreateRequest(values, options);
        const created = await createOrder(api, body, keys.keyFor(body));
        keys.reset();
        // The route may stay mounted (hidden) after navigating away: start the next new order from an empty form.
        setValues(newDraftValues());
        setErrors({});
        toast({ tone: "success", title: `${created.detail.order.number} taslağı oluşturuldu`, description: "Fiyatlar müşterinin fiyat listesinden çözüldü." });
        router.push(orderHref(created.detail.order.id));
        return;
      }
      const patch = buildUpdatePatch(original!, values, options);
      if (Object.keys(patch).length === 0) return;
      const next = await updateOrder(api, loaded.detail.order.id, patch, loaded.etag);
      setErrors({});
      toast({ tone: "success", title: "Taslak kaydedildi", description: "Fiyatlar ve toplamlar yeniden hesaplandı." });
      onSaved?.(next);
    } catch (error) {
      if (isOrderVersionConflict(error)) {
        setConflict(true);
      } else if (isStaleState(error)) {
        fail(orderErrorMessage(error));
        onReload?.();
      } else if (error instanceof ApiError && error.status === 422) {
        showErrors(orderFieldErrors(error));
        fail(orderErrorMessage(error));
      } else {
        fail(orderErrorMessage(error));
      }
    } finally {
      setSaving(false);
    }
  };

  const addVariant = (variant: DraftLine["variant"]) => {
    if (values.lines.some((l) => l.variant.id === variant.id)) return false;
    setValues((v) => ({ ...v, lines: [...v.lines, newDraftLine(variant)] }));
    return true;
  };

  const warehouseOptions =
    warehouses.status === "ready"
      ? warehouses.items
          .filter((w) => w.status === "active" || w.id === values.warehouseId)
          .map((w) => ({ value: w.id, label: `${w.name} (${w.code})${w.status === "active" ? "" : " — pasif"}` }))
      : [];

  return (
    <form noValidate onSubmit={submit} aria-label={loaded ? "Taslak sipariş" : "Yeni sipariş"} className="flex flex-col gap-4">
      {conflict ? (
        <FormAlert
          tone="warning"
          title="Sipariş başkası tarafından değiştirildi"
          actions={
            <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={() => onReload?.()}>
              Yeniden yükle
            </Button>
          }
        >
          Bu taslak siz düzenlerken güncellendi; değişiklikleriniz kaydedilmedi. Güncel hâlini yeniden yükleyin, ardından değişikliklerinizi tekrar yapın.
        </FormAlert>
      ) : null}
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title={loaded ? "Taslak kaydedilemedi" : "Sipariş oluşturulamadı"}>
          {formError}
          {summary.length > 0 ? (
            <ul className="mt-1 list-disc pl-5">
              {summary.map((m, i) => (
                <li key={i}>{m}</li>
              ))}
            </ul>
          ) : null}
        </FormAlert>
      ) : null}

      <section aria-labelledby="draft-header" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
        <h2 id="draft-header" className="text-h3 text-fg">
          Müşteri ve teslimat
        </h2>
        <CustomerPicker
          id="f-customerId"
          label="Müşteri"
          pickedLabel="Müşteri"
          hint="Ad veya kodun bir kısmı; tam vergi no / telefon / WhatsApp. Yalnızca aktif müşteriler."
          status="active"
          value={customerShown}
          error={errors.customerId}
          onChange={(c) => set({ customer: c ? { id: c.id, name: c.name, code: c.code } : null })}
        />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <Select
            label="Depo"
            options={warehouseOptions}
            placeholder={warehouses.status === "loading" ? "Depolar yükleniyor…" : "Varsayılan depo"}
            value={values.warehouseId}
            error={errors.warehouseId ?? (warehouses.status === "error" ? "Depolar yüklenemedi; varsayılan depo kullanılır." : undefined)}
            onChange={(e) => set({ warehouseId: e.target.value })}
          />
          <Select
            label="Fiyatlarda KDV"
            options={TAX_MODE_OPTIONS}
            value={values.taxMode}
            hint="Toplamlar sunucuda bu moda göre hesaplanır."
            onChange={(e) => set({ taxMode: e.target.value as TaxMode })}
          />
          <Input
            label="İstenen teslim tarihi"
            type="date"
            value={values.requestedDeliveryDate}
            error={errors.requestedDeliveryDate}
            onChange={(e) => set({ requestedDeliveryDate: e.target.value })}
          />
        </div>
        <Textarea label="Not" value={values.notes} error={errors.notes} maxLength={2000} onChange={(e) => set({ notes: e.target.value })} />
      </section>

      <section aria-labelledby="draft-lines" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
        <h2 id="draft-lines" className="text-h3 text-fg">
          Satırlar
        </h2>
        <VariantScanner
          label="Ürün ekle"
          hint="Barkod, SKU veya ürün adı; Enter ile ekler. Aynı ürün ikinci kez eklenmez, miktarı satırdan değiştirin."
          onPick={(variant) => void addVariant(variant)}
          pickedText={(v) => (values.lines.some((l) => l.variant.id === v.id) ? `${v.sku} zaten siparişte; miktarı satırdan değiştirin.` : `${v.sku} eklendi.`)}
        />
        {values.lines.length === 0 ? (
          <p className="text-body text-fg-muted">Henüz satır yok. Taslak satırsız kaydedilebilir; onaya göndermek için en az bir satır gerekir.</p>
        ) : (
          <ol aria-label="Sipariş satırları" className="flex flex-col gap-2">
            {values.lines.map((line, i) => (
              <DraftLineRow
                key={line.key}
                index={i}
                line={line}
                server={line.id ? (serverLines.get(line.id) ?? null) : null}
                currency={currency}
                errors={errors}
                overridePrice={permissions.overridePrice}
                flaggedUnpriced={unpricedLineIndexes.includes(i)}
                onChange={(patch) => setLine(line.key, patch)}
                onRemove={() => setValues((v) => ({ ...v, lines: v.lines.filter((l) => l.key !== line.key) }))}
              />
            ))}
          </ol>
        )}
        {dirty && loaded ? (
          <p role="status" className="text-caption text-warning-fg">
            Kaydedilmemiş değişiklikler var. Fiyatlar ve toplamlar kaydettiğinizde sunucuda yeniden hesaplanır.
          </p>
        ) : null}
        {!loaded ? <p className="text-caption text-fg-muted">Fiyatlar, iskontolar, KDV ve toplamlar taslak kaydedildiğinde sunucuda hesaplanır.</p> : null}
        <div className="flex flex-wrap justify-end gap-2">
          {loaded && dirty ? (
            <Button variant="secondary" icon={<X className="size-4" />} onClick={() => original && setValues(original)} disabled={saving}>
              Değişiklikleri geri al
            </Button>
          ) : null}
          <Button type="submit" icon={<Save className="size-4" />} loading={saving} disabled={loaded ? !dirty : false}>
            {loaded ? "Taslağı kaydet" : "Taslak oluştur"}
          </Button>
        </div>
      </section>
    </form>
  );
}

function DraftLineRow({
  index,
  line,
  server,
  currency,
  errors,
  overridePrice,
  flaggedUnpriced,
  onChange,
  onRemove,
}: {
  index: number;
  line: DraftLine;
  server: OrderLine | null;
  currency: string;
  errors: Record<string, string>;
  overridePrice: boolean;
  flaggedUnpriced: boolean;
  onChange: (patch: Partial<DraftLine>) => void;
  onRemove: () => void;
}) {
  const sku = line.variant.sku;
  const noPrice = server?.priceStatus === "no_price";
  return (
    <li
      data-testid={`draft-line-${index}`}
      className={`flex flex-col gap-3 rounded-md border p-3 ${noPrice || flaggedUnpriced ? "border-danger-border" : "border-line"}`}
    >
      <div className="flex items-start justify-between gap-2">
        <span className="flex flex-col">
          <span className="font-mono text-body text-fg">{sku}</span>
          {line.variant.name ? <span className="text-caption text-fg-muted">{line.variant.name}</span> : null}
          {errors[`lines[${index}].variantId`] ? <span className="text-caption text-danger-fg">{errors[`lines[${index}].variantId`]}</span> : null}
        </span>
        <span className="flex items-start gap-2">
          {server ? (
            <span className="flex flex-col items-end gap-0.5 text-right">
              <LinePrice line={server} currency={currency} />
              {server.priceStatus === "priced" ? (
                <span className="text-caption text-fg-muted">
                  {server.discountPercent != null || server.discountAmount != null ? `İskonto ${lineDiscountText(server, currency)} · ` : ""}
                  Satır toplamı {formatMoney(server.lineTotal, currency) ?? "—"}
                </span>
              ) : null}
            </span>
          ) : (
            <Badge tone="neutral">Kaydedince fiyatlanır</Badge>
          )}
          <Button variant="ghost" icon={<X className="size-4" />} aria-label={`${sku} satırını kaldır`} onClick={onRemove} />
        </span>
      </div>
      {noPrice ? (
        <p className="text-caption text-danger-fg">
          Bu ürünün müşteri için geçerli bir fiyatı yok. {overridePrice ? "Manuel fiyat girin veya" : "Yetkili bir kullanıcıdan manuel fiyat isteyin veya"} fiyat
          listesine fiyat ekleyin; aksi hâlde sipariş onaya gönderilemez.
        </p>
      ) : null}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-4">
        <Input
          label={`${sku} miktarı`}
          inputMode="decimal"
          value={line.quantity}
          error={errors[`lines[${index}].quantity`]}
          suffix={server?.unit}
          onChange={(e) => onChange({ quantity: e.target.value })}
        />
        {overridePrice ? (
          <>
            <Input
              label={`${sku} manuel birim fiyat`}
              inputMode="decimal"
              placeholder="Liste fiyatı"
              hint="Boş: fiyat listesinden"
              value={line.unitPrice}
              error={errors[`lines[${index}].unitPrice`]}
              onChange={(e) => onChange({ unitPrice: e.target.value })}
            />
            <Select
              label={`${sku} iskonto türü`}
              options={DISCOUNT_OPTIONS}
              value={line.discountMode}
              onChange={(e) => onChange({ discountMode: e.target.value as DiscountMode, discount: "" })}
            />
            {line.discountMode !== "none" ? (
              <Input
                label={line.discountMode === "percent" ? `${sku} iskonto oranı (%)` : `${sku} iskonto tutarı`}
                inputMode="decimal"
                value={line.discount}
                error={errors[discountKey(index, line.discountMode)]}
                onChange={(e) => onChange({ discount: e.target.value })}
              />
            ) : null}
          </>
        ) : null}
      </div>
    </li>
  );
}

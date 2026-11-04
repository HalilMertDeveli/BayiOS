"use client";

import { CheckboxField } from "@/components/ui/checkbox-field";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import type { PriceList } from "@/lib/pricing/api";
import { priceListKindLabels } from "@/lib/pricing/labels";
import { currencyOptions } from "@/lib/pricing/money";
import { PRICE_LIST_NAME_MAX, baseListCandidates, type PriceListFormValues } from "@/lib/pricing/price-list-form";

/**
 * Fields shared by "new price list" and the list settings form. Kind and currency are fixed after creation (the API's
 * update request has no such members). Purchase lists are offered only to roles with unconditional prices.read.cost.
 */
export function PriceListFields({
  values,
  errors,
  set,
  lists,
  original,
  canChooseKind,
}: {
  values: PriceListFormValues;
  errors: Record<string, string>;
  set: (patch: Partial<PriceListFormValues>) => void;
  lists: readonly PriceList[];
  original: PriceList | null;
  canChooseKind: boolean;
}) {
  const sales = values.kind === "sales";
  const candidates = baseListCandidates(lists, values, original?.id ?? null);
  const baseOptions = candidates.map((l) => ({ value: l.id, label: l.name }));
  if (values.basePriceListId && !baseOptions.some((o) => o.value === values.basePriceListId)) {
    const current = lists.find((l) => l.id === values.basePriceListId);
    baseOptions.push({ value: values.basePriceListId, label: current ? `${current.name} (uygun değil)` : "Erişilemeyen liste" });
  }
  // A list that is itself used as a base cannot be derived (one level only).
  const usedAsBase = original ? lists.some((l) => l.basePriceListId === original.id && l.status !== "archived") : false;

  return (
    <div className="flex flex-col gap-4">
      <Input label="Liste adı" required maxLength={PRICE_LIST_NAME_MAX} value={values.name} error={errors.name} hint="Ör. Bayi A 2026, Perakende" onChange={(e) => set({ name: e.target.value })} />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        {original ? (
          <p className="text-body text-fg-secondary">
            Tür: <strong>{priceListKindLabels[original.kind] ?? original.kind}</strong> · Para birimi: <strong>{original.currency}</strong>
            <span className="block text-caption text-fg-muted">Oluşturulduktan sonra değiştirilemez.</span>
          </p>
        ) : (
          <>
            <Select
              label="Liste türü"
              options={[
                { value: "sales", label: "Satış" },
                ...(canChooseKind || values.kind === "purchase" ? [{ value: "purchase", label: "Alış (maliyet)" }] : []),
              ]}
              value={values.kind}
              error={errors.kind}
              hint={canChooseKind ? "Alış listeleri yalnızca maliyet görme yetkisi olanlara görünür." : undefined}
              onChange={(e) => set({ kind: e.target.value === "purchase" ? "purchase" : "sales", ...(e.target.value === "purchase" ? { isDefault: false, basePriceListId: "", discountPercent: "" } : {}) })}
            />
            <Select
              label="Para birimi"
              required
              options={currencyOptions(values.currency)}
              value={values.currency}
              error={errors.currency}
              onChange={(e) => set({ currency: e.target.value, basePriceListId: "" })}
            />
          </>
        )}
      </div>
      <CheckboxField
        label="Fiyatlar KDV dahil"
        checked={values.pricesIncludeTax}
        onChange={(v) => set({ pricesIncludeTax: v, basePriceListId: "" })}
        hint="Kapalıyken fiyatlar KDV hariç girilir; KDV oranı ürün kartından gelir."
      />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <Input label="Geçerlilik başlangıcı" type="date" value={values.validFrom} error={errors.validFrom} hint="Boş: hemen geçerli" onChange={(e) => set({ validFrom: e.target.value })} />
        <Input label="Geçerlilik bitişi" type="date" value={values.validTo} error={errors.validTo} hint="Boş: süresiz" onChange={(e) => set({ validTo: e.target.value })} />
      </div>
      {sales ? (
        <fieldset className="flex flex-col gap-3 rounded-md border border-line p-3">
          <legend className="px-1 text-body font-medium text-fg">Taban listeden türet (isteğe bağlı)</legend>
          <p className="text-caption text-fg-muted">
            Bu listede kalemi olmayan ürünlerde fiyat, taban listenin fiyatından iskonto düşülerek API tarafından hesaplanır. Taban liste aynı para
            biriminde ve KDV modunda, kendisi türetilmemiş bir satış listesi olmalı.
          </p>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Select
              label="Taban liste"
              placeholder="Türetme yok"
              options={baseOptions}
              value={values.basePriceListId}
              error={errors.basePriceListId}
              disabled={usedAsBase}
              hint={usedAsBase ? "Bu liste başka listelerin tabanı; kendisi türetilemez." : baseOptions.length === 0 ? "Uygun taban liste yok." : undefined}
              onChange={(e) => set({ basePriceListId: e.target.value, ...(e.target.value ? {} : { discountPercent: "" }) })}
            />
            <Input
              label="İskonto (%)"
              inputMode="decimal"
              value={values.discountPercent}
              error={errors.discountPercent}
              disabled={!values.basePriceListId}
              hint="0–100, en fazla 2 ondalık (ör. 12,5)"
              onChange={(e) => set({ discountPercent: e.target.value })}
            />
          </div>
        </fieldset>
      ) : null}
      {sales && !original?.isDefault ? (
        <CheckboxField
          label={`${values.currency} için varsayılan satış listesi yap`}
          checked={values.isDefault}
          onChange={(v) => set({ isDefault: v, ...(v ? { status: "active" } : {}) })}
          hint="Müşteriye veya bayi grubuna liste atanmamışsa bu liste kullanılır. Para biriminin ilk satış listesi otomatik varsayılan olur."
        />
      ) : null}
      {original ? (
        <CheckboxField
          label="Aktif"
          checked={values.status === "active"}
          disabled={original.isDefault}
          onChange={(v) => set({ status: v ? "active" : "inactive" })}
          hint={original.isDefault ? "Varsayılan liste pasif yapılamaz. Önce başka bir listeyi varsayılan yapın." : "Pasif liste fiyat çözümlemede atlanır."}
        />
      ) : null}
    </div>
  );
}

"use client";

import { useEffect, useState, type FormEvent, useId } from "react";
import Link from "next/link";
import { CircleAlert } from "@/components/ui/icons";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import type { Product, Unit } from "@/lib/catalog/api";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { variantStockHref } from "@/lib/inventory/balance-query";
import { inventoryErrorMessage, inventoryFieldErrors } from "@/lib/inventory/errors";
import {
  bulkErrorsByKey,
  bulkThresholds,
  defaultThresholds,
  listThresholds,
  planThresholds,
  thresholdInput,
  type StockThreshold,
} from "@/lib/inventory/thresholds";
import { useSession } from "@/lib/session/session-context";
import { FormAlert } from "./form-alert";

type Load = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; thresholds: StockThreshold[] };

/**
 * "Minimum stok" of a product's variants (#137): the general (all-warehouse) minimum per variant, saved with one
 * all-or-nothing bulk request (only changed rows; an emptied field removes the minimum). Warehouse overrides live on
 * the variant's stock page. Visible to everyone with inventory.read; editable with catalog.write.
 */
export function StockThresholdsSection({ product, units, readOnly }: { product: Product; units: readonly Unit[]; readOnly: boolean }) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const variants = product.variants.filter((v) => v.status !== "archived");
  const variantKey = variants.map((v) => v.id).join(",");
  const [load, setLoad] = useState<Load>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [texts, setTexts] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());

  useEffect(() => {
    const controller = new AbortController();
    const ids = variantKey ? variantKey.split(",") : [];
    listThresholds(api, ids, controller.signal)
      .then((thresholds) => {
        if (controller.signal.aborted) return;
        const defaults = defaultThresholds(thresholds);
        setTexts(Object.fromEntries(ids.map((id) => [id, thresholdInput(defaults.get(id)?.minQuantity)])));
        setLoad({ status: "ready", thresholds });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setLoad({ status: "error", message: inventoryErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, variantKey, attempt]);

  if (variants.length === 0) return null;

  const unitOf = (unitId: string | null | undefined) => units.find((u) => u.id === (unitId ?? product.defaultUnitId));
  const overrides = (variantId: string) => (load.status === "ready" ? load.thresholds.filter((t) => t.variantId === variantId && t.warehouseId).length : 0);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (readOnly || saving || load.status !== "ready") return;
    setSummary(null);
    const rows = variants.map((v) => ({ variantId: v.id, text: texts[v.id] ?? "", allowsFraction: unitOf(v.unitId)?.allowsFraction ?? false }));
    const { items, errors: clientErrors } = planThresholds(rows, load.thresholds);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    if (items.length === 0) {
      toast({ tone: "info", title: "Değişiklik yok", description: "Minimum stok değerleri aynı." });
      return;
    }
    setSaving(true);
    try {
      await bulkThresholds(api, items, keys.keyFor(items));
      keys.reset();
      toast({ tone: "success", title: "Minimum stok kaydedildi", description: `${items.length} varyant güncellendi.` });
      setAttempt((n) => n + 1);
    } catch (error) {
      if (error instanceof ApiError && error.status === 422 && Object.keys(error.fieldErrors).length) {
        // items[i].minQuantity → the input of the i-th changed row.
        const mapped = bulkErrorsByKey(items, inventoryFieldErrors(error));
        setErrors(mapped);
        if (Object.keys(mapped).length === 0) setSummary(inventoryErrorMessage(error));
      } else {
        setSummary(inventoryErrorMessage(error));
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <section aria-labelledby={`${uid}-sec-thresholds`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
      <div className="flex flex-col gap-1">
        <h2 id={`${uid}-sec-thresholds`} className="text-h3 text-fg">
          Minimum stok
        </h2>
        <p className="text-caption text-fg-muted">
          Satılabilir miktar bu değere eşit veya altına inince ürün “kritik stok” olur (pano, stok listesi ve kritik stok raporu). Boş bırakılan varyant
          takip edilmez. Depoya özel eşik için varyantın stok sayfasını kullanın.
        </p>
      </div>
      {load.status === "error" ? (
        <div role="alert" className="flex flex-wrap items-center gap-3 text-body text-danger-fg">
          <CircleAlert className="size-4" aria-hidden="true" />
          <span>Minimum stok bilgileri yüklenemedi: {load.message}</span>
          <Button size="sm" variant="secondary" onClick={() => setAttempt((n) => n + 1)}>
            Tekrar dene
          </Button>
        </div>
      ) : (
        <form noValidate onSubmit={submit} aria-label="Minimum stok" className="flex flex-col gap-3">
          {summary ? (
            <FormAlert tone="error" title="Kaydedilemedi">
              <p>{summary}</p>
            </FormAlert>
          ) : null}
          <ul className="flex flex-col gap-2">
            {variants.map((v) => {
              const unit = unitOf(v.unitId);
              const count = overrides(v.id);
              return (
                <li key={v.id} className="grid grid-cols-1 items-start gap-2 sm:grid-cols-[1fr_14rem]">
                  <div className="flex min-w-0 flex-col pt-1">
                    <span className="font-mono text-caption font-medium text-fg">{v.sku}</span>
                    <span className="text-caption text-fg-muted">
                      {v.name ?? "Varsayılan varyant"}
                      {count > 0 ? ` · ${count} depoya özel eşik` : ""} ·{" "}
                      <Link href={variantStockHref(v.id)} className="text-brand-fg underline underline-offset-2">
                        Depo bazında
                      </Link>
                    </span>
                  </div>
                  <Input
                    id={`${uid}-threshold-${v.id}`}
                    label={`${v.sku} minimum stok`}
                    hideLabel
                    inputMode={unit?.allowsFraction ? "decimal" : "numeric"}
                    placeholder="Takip yok"
                    suffix={unit?.code}
                    value={texts[v.id] ?? ""}
                    disabled={readOnly || load.status !== "ready"}
                    error={errors[v.id]}
                    onChange={(e) => setTexts((prev) => ({ ...prev, [v.id]: e.target.value }))}
                  />
                </li>
              );
            })}
          </ul>
          {!readOnly ? (
            <div className="flex justify-end">
              <Button type="submit" variant="secondary" loading={saving} disabled={load.status !== "ready"}>
                Minimum stoku kaydet
              </Button>
            </div>
          ) : null}
        </form>
      )}
    </section>
  );
}

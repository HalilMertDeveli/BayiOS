"use client";

import { useEffect, useState, type FormEvent, useId } from "react";
import { CircleAlert, Gauge } from "@/components/ui/icons";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { listUnits, type Unit, type Variant } from "@/lib/catalog/api";
import type { Warehouse } from "@/lib/inventory/api";
import { inventoryErrorMessage, inventoryFieldErrors } from "@/lib/inventory/errors";
import { formatQuantity } from "@/lib/inventory/quantity";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import {
  bulkErrorsByKey,
  bulkThresholds,
  listThresholds,
  planThresholds,
  thresholdInput,
  thresholdKey,
  type StockThreshold,
} from "@/lib/inventory/thresholds";
import { useSession } from "@/lib/session/session-context";

type Load = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; thresholds: StockThreshold[]; unit: Unit | null };

/**
 * /stok/varyant/[id] — the variant's minimum stock (#137): the general minimum (all warehouses) and per-warehouse
 * overrides. In one warehouse the override wins, else the general minimum applies; across all warehouses the general
 * minimum applies, else the sum of the overrides. One save sends only the changed rows (bulk, all or nothing).
 */
export function VariantThresholds({
  variant,
  warehouses,
  canEdit,
  onSaved,
}: {
  variant: Variant;
  warehouses: readonly Pick<Warehouse, "id" | "name" | "code" | "status">[];
  canEdit: boolean;
  /** Called after a successful save (the balances' "Kritik" flags may change). */
  onSaved: () => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [load, setLoad] = useState<Load>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [texts, setTexts] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());

  useEffect(() => {
    const controller = new AbortController();
    Promise.all([listThresholds(api, [variant.id], controller.signal), listUnits(api, controller.signal).catch(() => [] as Unit[])])
      .then(([thresholds, units]) => {
        if (controller.signal.aborted) return;
        setTexts(Object.fromEntries(thresholds.map((t) => [thresholdKey(t.variantId, t.warehouseId), thresholdInput(t.minQuantity)])));
        setLoad({ status: "ready", thresholds, unit: units.find((u) => u.id === variant.unitId) ?? null });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setLoad({ status: "error", message: inventoryErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, variant.id, variant.unitId, attempt]);

  const unit = load.status === "ready" ? load.unit : null;
  // Unknown unit: let the API decide (it rejects fractions for whole-number units).
  const allowsFraction = unit?.allowsFraction ?? true;
  const general = texts[thresholdKey(variant.id, null)] ?? "";
  const rows = [
    { warehouseId: null as string | null, label: "Genel (tüm depolar)", hint: "Depoya özel eşik yoksa her depoda bu değer geçerlidir." },
    ...warehouses
      .filter((w) => w.status === "active" || texts[thresholdKey(variant.id, w.id)])
      .map((w) => ({
        warehouseId: w.id as string | null,
        label: `${w.name} (${w.code})${w.status !== "active" ? " — pasif" : ""}`,
        hint: general.trim() ? `Boş bırakılırsa genel eşik (${general}) geçerli.` : "Boş bırakılırsa bu depoda takip yok.",
      })),
  ];

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!canEdit || saving || load.status !== "ready") return;
    setSummary(null);
    const inputs = rows.map((r) => ({ variantId: variant.id, warehouseId: r.warehouseId, text: texts[thresholdKey(variant.id, r.warehouseId)] ?? "", allowsFraction }));
    const { items, errors: clientErrors } = planThresholds(inputs, load.thresholds);
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
      toast({ tone: "success", title: "Minimum stok kaydedildi", description: variant.sku });
      setAttempt((n) => n + 1);
      onSaved();
    } catch (error) {
      const mapped = error instanceof ApiError && error.status === 422 ? bulkErrorsByKey(items, inventoryFieldErrors(error)) : {};
      setErrors(mapped);
      if (Object.keys(mapped).length === 0) setSummary(inventoryErrorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  return (
    <section aria-labelledby={`${uid}-variant-thresholds`} className="flex flex-col gap-3 rounded-xl border border-line bg-card p-4 shadow-e1">
      <div className="flex flex-col gap-1">
        <h2 id={`${uid}-variant-thresholds`} className="flex items-center gap-2 text-h3 text-fg">
          <Gauge className="size-5" aria-hidden="true" />
          Minimum stok
        </h2>
        <p className="text-caption text-fg-muted">
          Satılabilir miktar eşiğe eşit veya altındaysa ürün “kritik” görünür. Depo eşiği o depoda genel eşiğin yerine geçer; tüm depolar görünümünde genel
          eşik, yoksa depo eşiklerinin toplamı kullanılır.
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
      ) : load.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Yükleniyor…
        </p>
      ) : canEdit ? (
        <form noValidate onSubmit={submit} aria-label="Minimum stok" className="flex flex-col gap-3">
          {summary ? (
            <p role="alert" className="text-body text-danger-fg">
              {summary}
            </p>
          ) : null}
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:max-w-3xl">
            {rows.map((r) => {
              const key = thresholdKey(variant.id, r.warehouseId);
              return (
                <Input
                  key={key}
                  id={`${uid}-threshold-${key}`}
                  label={r.label}
                  hint={r.hint}
                  inputMode={allowsFraction ? "decimal" : "numeric"}
                  placeholder="Takip yok"
                  suffix={unit?.code}
                  value={texts[key] ?? ""}
                  error={errors[key]}
                  onChange={(e) => setTexts((prev) => ({ ...prev, [key]: e.target.value }))}
                />
              );
            })}
          </div>
          <div>
            <Button type="submit" variant="secondary" loading={saving}>
              Minimum stoku kaydet
            </Button>
          </div>
        </form>
      ) : (
        <dl className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:max-w-3xl">
          {rows.map((r) => {
            const value = load.thresholds.find((t) => (t.warehouseId ?? null) === r.warehouseId)?.minQuantity;
            return (
              <div key={thresholdKey(variant.id, r.warehouseId)} className="rounded-md bg-sunken p-3">
                <dt className="text-caption text-fg-muted">{r.label}</dt>
                <dd className="text-body tabular-nums text-fg">{value === undefined ? "Takip yok" : `${formatQuantity(value)}${unit ? ` ${unit.code}` : ""}`}</dd>
              </div>
            );
          })}
        </dl>
      )}
    </section>
  );
}

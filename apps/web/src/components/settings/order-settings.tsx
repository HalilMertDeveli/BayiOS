"use client";

import { useEffect, useRef, useState, type FormEvent, useId } from "react";
import { CircleAlert, Lock, RotateCcw, Save, X } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { getTenantSettings, updateTenantSettings, type TenantSettings } from "@/lib/api/identity";
import { etagOf } from "@/lib/catalog/api";
import { isVersionConflict } from "@/lib/catalog/errors";
import { priceDisplayOptions, taxRateOptions } from "@/lib/onboarding/tenant-form";
import {
  ORDER_NUMBER_PREFIX_MAX,
  buildSettingsPatch,
  orderNumberExample,
  settingsToValues,
  translateSettingsFieldMessage,
  validateOrderSettings,
  type OrderSettingsValues,
} from "@/lib/settings/order-settings-form";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";
import { NegativeStockRule } from "./negative-stock-rule";

type Loaded = { settings: TenantSettings; etag: string };
type LoadState = { status: "loading" } | { status: "error"; message: string } | ({ status: "ready" } & Loaded);

const FIELD_KEYS = ["requireOrderApproval", "orderNumberPrefix", "defaultTaxRate", "pricesIncludeTaxDisplay", "autoCreatePickingTask"];

/**
 * Ayarlar → Sipariş ayarları (#92): approval requirement, order number prefix, the fixed negative-stock rule (OD-09), default VAT rate
 * and price display. GET/PATCH /tenant/settings (tenant.settings.manage) with If-Match; a 412 asks to reload.
 */
export function OrderSettings() {
  const uid = useId();
  const { api, access } = useSession();
  const canManage = access.can("tenant.settings.manage");
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!canManage) return;
    let cancelled = false;
    getTenantSettings(api)
      .then(({ settings, etag }) => {
        if (!cancelled) setLoad({ status: "ready", settings, etag: etag ?? etagOf(0) });
      })
      .catch((error: unknown) => {
        if (!cancelled) setLoad({ status: "error", message: error instanceof ApiError ? error.userMessage : "Ayarlar yüklenemedi." });
      });
    return () => {
      cancelled = true;
    };
  }, [api, attempt, canManage]);

  const reload = () => {
    setLoad({ status: "loading" });
    setAttempt((n) => n + 1);
  };

  return (
    <section aria-labelledby={`${uid}-order-settings`} className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      <div className="flex flex-col gap-1">
        <h2 id={`${uid}-order-settings`} className="text-h3 text-fg">
          Sipariş ayarları
        </h2>
        <p className="text-caption text-fg-muted">
          Sipariş onayı, sipariş numaraları, onayda toplama görevi, stoğun eksiye düşmemesi (sabit kural) ve KDV varsayılanları.
        </p>
      </div>
      {!canManage ? (
        <EmptyState
          icon={<Lock className="size-6" />}
          title="Ayarları değiştirme yetkiniz yok"
          description="İşletme ayarlarını yalnızca işletme sahibi ve yöneticiler değiştirebilir."
        />
      ) : load.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Ayarlar yükleniyor…
        </p>
      ) : load.status === "error" ? (
        <div role="alert">
          <EmptyState icon={<CircleAlert className="size-6" />} title="Ayarlar yüklenemedi" description={load.message} action={<Button onClick={reload}>Tekrar dene</Button>} />
        </div>
      ) : (
        <OrderSettingsForm
          key={load.etag}
          loaded={load}
          onSaved={(next) => setLoad({ status: "ready", ...next })}
          onReload={reload}
        />
      )}
    </section>
  );
}

export function OrderSettingsForm({ loaded, onSaved, onReload }: { loaded: Loaded; onSaved: (next: Loaded) => void; onReload: () => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [values, setValues] = useState<OrderSettingsValues>(() => settingsToValues(loaded.settings));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const alertRef = useRef<HTMLDivElement>(null);
  const conflictRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const set = (patch: Partial<OrderSettingsValues>) => setValues((v) => ({ ...v, ...patch }));

  const patch = buildSettingsPatch(loaded.settings, values);
  const dirty = Object.keys(patch).length > 0;
  const year = new Date().getFullYear();
  const taxOptions = taxRateOptions.some((o) => Number(o.value) === Number(values.defaultTaxRate))
    ? taxRateOptions
    : [...taxRateOptions, { value: values.defaultTaxRate, label: `%${values.defaultTaxRate.replace(".", ",")}` }];
  const taxValue = taxOptions.find((o) => Number(o.value) === Number(values.defaultTaxRate))?.value ?? values.defaultTaxRate;

  const fail = (message: string) => {
    setFormError(message);
    focusAfterCommit(() => alertRef.current);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    setFormError(null);
    const local = validateOrderSettings(values);
    setErrors(local);
    if (Object.keys(local).length > 0) {
      fail("Bazı alanlar hatalı. İşaretli alanları düzeltin.");
      return;
    }
    if (!dirty) return;
    setSaving(true);
    try {
      const saved = await updateTenantSettings(api, patch, loaded.etag);
      toast({ tone: "success", title: "Sipariş ayarları kaydedildi" });
      onSaved({ settings: saved.settings, etag: saved.etag ?? loaded.etag });
    } catch (error) {
      setSaving(false);
      if (isVersionConflict(error)) {
        setConflict(true);
        focusAfterCommit(() => conflictRef.current);
        return;
      }
      if (error instanceof ApiError && error.status === 422) {
        const fieldErrors: Record<string, string> = {};
        const other: string[] = [];
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          if (FIELD_KEYS.includes(key)) fieldErrors[key] = translateSettingsFieldMessage(messages[0]);
          else other.push(translateSettingsFieldMessage(messages[0]));
        }
        setErrors(fieldErrors);
        fail([error.userMessage, ...other].join(" "));
        return;
      }
      fail(error instanceof ApiError ? error.userMessage : "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");
    }
  };

  return (
    <form noValidate onSubmit={submit} aria-label="Sipariş ayarları" className="flex flex-col gap-5">
      {conflict ? (
        <FormAlert
          ref={conflictRef}
          tone="warning"
          title="Ayarlar başkası tarafından değiştirildi"
          actions={
            <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={onReload}>
              Güncel ayarları yükle
            </Button>
          }
        >
          Siz düzenlerken başka bir yönetici ayarları kaydetti; değişiklikleriniz kaydedilmedi. Güncel ayarları yükleyin ve değişikliklerinizi yeniden yapın.
        </FormAlert>
      ) : null}
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Ayarlar kaydedilemedi">
          {formError}
        </FormAlert>
      ) : null}

      <fieldset className="flex flex-col gap-3">
        <legend className="mb-1 text-body font-semibold text-fg">Onay ve numaralandırma</legend>
        <CheckboxField
          label="Siparişler onaydan geçsin"
          checked={values.requireOrderApproval}
          onChange={(v) => set({ requireOrderApproval: v })}
          hint={
            values.requireOrderApproval
              ? "Gönderilen siparişler “Onay bekliyor” durumuna geçer; stok onayda ayrılır."
              : "Onay yetkisi olan bir kullanıcının gönderdiği sipariş hemen onaylanır ve stok ayrılır. Diğer kullanıcıların ve WhatsApp'tan gelen siparişler yine onay bekler."
          }
        />
        <Input
          label="Sipariş numarası öneki"
          required
          value={values.orderNumberPrefix}
          maxLength={ORDER_NUMBER_PREFIX_MAX}
          autoCapitalize="characters"
          spellCheck={false}
          className="sm:max-w-md"
          error={errors.orderNumberPrefix}
          hint={`1–10 karakter: A–Z ve 0–9, harfle başlar. Yeni siparişler ${orderNumberExample(values.orderNumberPrefix, year)} biçiminde numaralanır; mevcut numaralar değişmez.`}
          onChange={(e) => set({ orderNumberPrefix: e.target.value.toLocaleUpperCase("en-US") })}
        />
      </fieldset>

      <fieldset className="flex flex-col gap-3">
        <legend className="mb-1 text-body font-semibold text-fg">Depo</legend>
        <CheckboxField
          label="Sipariş onaylanınca toplama görevi otomatik oluşturulsun"
          checked={values.autoCreatePickingTask}
          onChange={(v) => set({ autoCreatePickingTask: v })}
          hint={
            values.autoCreatePickingTask
              ? "Onaylanan her sipariş için toplama görevi hemen oluşur ve depo havuzuna düşer."
              : "Onayda stok yine ayrılır ama görev oluşmaz; yönetici siparişin sayfasından önceliğini seçerek “Toplama görevi oluştur” ile başlatır."
          }
        />
      </fieldset>

      <fieldset className="flex flex-col gap-3">
        <legend className="mb-1 text-body font-semibold text-fg">Stok ve KDV</legend>
        <NegativeStockRule />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Select
            label="Varsayılan KDV oranı"
            options={taxOptions}
            value={taxValue}
            error={errors.defaultTaxRate}
            hint="Yeni ürünlerde önerilir; ürün kartından değiştirilebilir."
            onChange={(e) => set({ defaultTaxRate: e.target.value })}
          />
          <Select
            label="Fiyat gösterimi"
            options={priceDisplayOptions}
            value={values.pricesIncludeTaxDisplay ? "included" : "excluded"}
            error={errors.pricesIncludeTaxDisplay}
            hint="Ekranlarda fiyatların KDV dahil mi hariç mi gösterileceği."
            onChange={(e) => set({ pricesIncludeTaxDisplay: e.target.value === "included" })}
          />
        </div>
      </fieldset>

      <div className="flex flex-wrap items-center justify-end gap-2">
        {dirty ? (
          <p role="status" className="mr-auto text-caption text-warning-fg">
            Kaydedilmemiş değişiklikler var.
          </p>
        ) : null}
        {dirty ? (
          <Button
            variant="secondary"
            icon={<X className="size-4" />}
            disabled={saving}
            onClick={() => {
              setValues(settingsToValues(loaded.settings));
              setErrors({});
              setFormError(null);
            }}
          >
            Değişiklikleri geri al
          </Button>
        ) : null}
        <Button type="submit" icon={<Save className="size-4" />} loading={saving} disabled={!dirty || conflict}>
          Ayarları kaydet
        </Button>
      </div>
    </form>
  );
}

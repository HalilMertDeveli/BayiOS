"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { CircleAlert, Lock, RotateCcw, Save, X } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { getTenantSettings, updateTenantSettings, type TenantSettings } from "@/lib/api/identity";
import { etagOf } from "@/lib/catalog/api";
import { isVersionConflict } from "@/lib/catalog/errors";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";
import {
  RULE_FIELDS,
  buildRulesPatch,
  discountRuleSummary,
  settingsToRules,
  translateRuleFieldMessage,
  validateRules,
  type RulesValues,
} from "@/lib/settings/rules-form";
import { NegativeStockRule } from "./negative-stock-rule";
import { SettingsBackLink } from "./settings-back-link";

type Loaded = { settings: TenantSettings; etag: string };
type LoadState = { status: "loading" } | { status: "error"; message: string } | ({ status: "ready" } & Loaded);

/**
 * Ayarlar → İş kuralları (#129 item 6, roles-permissions §4): what sales, warehouse staff, managers and viewers may do
 * under this tenant's rules. GET/PATCH /tenant/settings (tenant.settings.manage) with If-Match; after a save GET /me is
 * reloaded so the menu and buttons follow the new permissions.
 */
export function BusinessRules() {
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
        if (!cancelled) setLoad({ status: "error", message: error instanceof ApiError ? error.userMessage : "Kurallar yüklenemedi." });
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
    <>
      <SettingsBackLink />
      <PageHeader title="İş kuralları" description="Satış, depo ve izleyici rollerinin işletmenize göre neler yapabileceği." />
      {!canManage ? (
        <EmptyState
          icon={<Lock className="size-6" />}
          title="İş kurallarını değiştirme yetkiniz yok"
          description="İş kurallarını yalnızca işletme sahibi ve yöneticiler değiştirebilir."
        />
      ) : load.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Kurallar yükleniyor…
        </p>
      ) : load.status === "error" ? (
        <div role="alert">
          <EmptyState icon={<CircleAlert className="size-6" />} title="Kurallar yüklenemedi" description={load.message} action={<Button onClick={reload}>Tekrar dene</Button>} />
        </div>
      ) : (
        <BusinessRulesForm key={load.etag} loaded={load} onSaved={(next) => setLoad({ status: "ready", ...next })} onReload={reload} />
      )}
    </>
  );
}

function RuleGroup({ legend, description, children }: { legend: string; description: string; children: React.ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      <legend className="float-left mb-1 text-h3 text-fg">{legend}</legend>
      <p className="clear-left text-caption text-fg-muted">{description}</p>
      {children}
    </fieldset>
  );
}

export function BusinessRulesForm({ loaded, onSaved, onReload }: { loaded: Loaded; onSaved: (next: Loaded) => void; onReload: () => void }) {
  const { api, reload: reloadSession } = useSession();
  const { toast } = useToast();
  const [values, setValues] = useState<RulesValues>(() => settingsToRules(loaded.settings));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const alertRef = useRef<HTMLDivElement>(null);
  const conflictRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const set = (patch: Partial<RulesValues>) => setValues((v) => ({ ...v, ...patch }));

  const patch = buildRulesPatch(loaded.settings, values);
  // An edited but invalid discount is not in the patch yet; it still counts as a change (so saving shows the error).
  const dirty = Object.keys(patch).length > 0 || values.salesMaxDiscountPercent !== settingsToRules(loaded.settings).salesMaxDiscountPercent;

  const fail = (message: string) => {
    setFormError(message);
    focusAfterCommit(() => alertRef.current);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    setFormError(null);
    const local = validateRules(values);
    setErrors(local);
    if (Object.keys(local).length > 0) {
      fail("Bazı alanlar hatalı. İşaretli alanları düzeltin.");
      return;
    }
    if (Object.keys(patch).length === 0) return;
    setSaving(true);
    try {
      const saved = await updateTenantSettings(api, patch, loaded.etag);
      toast({ tone: "success", title: "İş kuralları kaydedildi", description: "Değişiklik ekibinizin bir sonraki işleminde geçerli olur." });
      // Your own GET /me permissions follow the rules too (e.g. cost visibility): refresh them.
      void reloadSession().catch(() => undefined);
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
          if ((RULE_FIELDS as readonly string[]).includes(key)) fieldErrors[key] = translateRuleFieldMessage(messages[0]);
          else other.push(translateRuleFieldMessage(messages[0]));
        }
        setErrors(fieldErrors);
        fail([error.userMessage, ...other].join(" "));
        return;
      }
      fail(error instanceof ApiError ? error.userMessage : "Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.");
    }
  };

  return (
    <form noValidate onSubmit={submit} aria-label="İş kuralları" className="flex max-w-3xl flex-col gap-5">
      {conflict ? (
        <FormAlert
          ref={conflictRef}
          tone="warning"
          title="Kurallar başkası tarafından değiştirildi"
          actions={
            <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={onReload}>
              Güncel kuralları yükle
            </Button>
          }
        >
          Siz düzenlerken başka bir yönetici ayarları kaydetti; değişiklikleriniz kaydedilmedi. Güncel kuralları yükleyin ve değişikliklerinizi yeniden yapın.
        </FormAlert>
      ) : null}
      {formError ? (
        <FormAlert ref={alertRef} tone="error" title="Kurallar kaydedilemedi">
          {formError}
        </FormAlert>
      ) : null}

      <RuleGroup legend="Satış personeli" description="Satış rolündeki kişilerin sipariş onayı ve iskonto yetkisi.">
        <CheckboxField
          label="Satış personeli sipariş onaylayabilir"
          checked={values.salesCanApproveOrders}
          onChange={(v) => set({ salesCanApproveOrders: v })}
          hint={
            values.salesCanApproveOrders
              ? "Satış personeli onay bekleyen siparişleri onaylayabilir; onayda stok ayrılır."
              : "Satış personelinin siparişleri bir yönetici veya operasyon müdürü onaylayana kadar bekler."
          }
        />
        <Input
          label="Satış personeli iskonto sınırı (%)"
          inputMode="decimal"
          value={values.salesMaxDiscountPercent}
          error={errors.salesMaxDiscountPercent}
          hint={discountRuleSummary(values.salesMaxDiscountPercent)}
          className="sm:max-w-xs"
          onChange={(e) => set({ salesMaxDiscountPercent: e.target.value })}
        />
      </RuleGroup>

      <RuleGroup legend="Alış fiyatı görünürlüğü" description="İşletme sahibi ve yöneticiler alış fiyatını her zaman görür.">
        <CheckboxField
          label="Operasyon müdürü alış fiyatını görebilir"
          checked={values.showCostToManager}
          onChange={(v) => set({ showCostToManager: v })}
          hint={values.showCostToManager ? "Müdürler ürün ve raporlarda alış fiyatını görür." : "Müdürler alış fiyatını görmez."}
        />
        <CheckboxField
          label="İzleyici alış fiyatını görebilir"
          checked={values.showCostToViewer}
          onChange={(v) => set({ showCostToViewer: v })}
          hint={values.showCostToViewer ? "İzleyiciler (ör. muhasebeci) alış fiyatını görür." : "İzleyiciler alış fiyatını görmez."}
        />
      </RuleGroup>

      <RuleGroup legend="Depo personeli" description="Depo rolündeki kişilerin ek yetkileri.">
        <CheckboxField
          label="Depo personeli barkod atayabilir"
          checked={values.staffCanAssignBarcode}
          onChange={(v) => set({ staffCanAssignBarcode: v })}
          hint={values.staffCanAssignBarcode ? "Depo personeli barkodu olmayan ürünlere barkod okutup atayabilir." : "Barkodu yalnızca katalog yetkisi olanlar atar."}
        />
        <CheckboxField
          label="Depo personeli mal kabulü onaylayabilir"
          checked={values.staffCanApproveReceipt}
          onChange={(v) => set({ staffCanApproveReceipt: v })}
          hint={values.staffCanApproveReceipt ? "Depo personeli mal kabulü kendisi onaylayabilir." : "Mal kabulü müdür veya yönetici onaylar."}
        />
      </RuleGroup>

      <RuleGroup legend="Stok" description="Sipariş onayı, stok düzeltme ve sevkiyatta tüm roller için geçerlidir.">
        <NegativeStockRule />
      </RuleGroup>

      <RuleGroup legend="Güvenlik" description="Hesap güvenliği tercihleri.">
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone="warning">Yakında · henüz uygulanmıyor</Badge>
        </div>
        <CheckboxField
          label="İşletme sahibi ve yöneticiler için iki adımlı doğrulama zorunlu (yakında)"
          checked={values.require2fa}
          onChange={(v) => set({ require2fa: v })}
          hint="Bu tercih yalnızca kaydedilir; şu an hiçbir girişte iki adımlı doğrulama istenmez ve ek koruma sağlamaz. Özellik hazır olduğunda bu tercihe göre açılacak."
        />
      </RuleGroup>

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
              setValues(settingsToRules(loaded.settings));
              setErrors({});
              setFormError(null);
            }}
          >
            Değişiklikleri geri al
          </Button>
        ) : null}
        <Button type="submit" icon={<Save className="size-4" />} loading={saving} disabled={!dirty || conflict}>
          Kuralları kaydet
        </Button>
      </div>
    </form>
  );
}

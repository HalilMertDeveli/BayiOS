"use client";

import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Archive, ArchiveRestore, ArrowLeft, CircleAlert, RotateCcw, ShieldAlert } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button, buttonClasses } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { isVersionConflict, partitionErrors } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import {
  CUSTOMERS_HREF,
  createCustomer,
  customerHref,
  getCustomer,
  updateCustomer,
  type Address,
  type Customer,
} from "@/lib/customers/api";
import {
  CODE_MAX,
  NAME_MAX,
  NOTES_MAX,
  SHORT_TEXT_MAX,
  buildCreateCustomer,
  buildCustomerPatch,
  customerToValues,
  newCustomerValues,
  validateCustomer,
  type CustomerFormValues,
} from "@/lib/customers/customer-form";
import { customerErrorMessage, customerFieldErrors } from "@/lib/customers/errors";
import { customerPermissions } from "@/lib/customers/permissions";
import { currencyOptions } from "@/lib/pricing/money";
import { useSession } from "@/lib/session/session-context";
import { AddressSection } from "./address-section";
import { AnonymizeDialog } from "./anonymize-dialog";
import { CustomerStatusBadge } from "./customer-status-badge";
import { assignableOptions, useCustomerReference } from "./use-customer-reference";

type Loaded = { customer: Customer; addresses: Address[]; etag: string };
type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | ({ status: "ready" } & Loaded);

const FIELD_KEYS = new Set([
  "code",
  "name",
  "contactName",
  "taxNumber",
  "taxOffice",
  "phone",
  "email",
  "whatsappNumbers",
  "dealerGroupId",
  "priceListId",
  "paymentTermDays",
  "creditLimit",
  "currency",
  "notes",
  "status",
]);

function BackLink() {
  return (
    <Link href={CUSTOMERS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Müşteriler
    </Link>
  );
}

/** /musteriler/yeni (customerId = null) and /musteriler/[id]: create, view or edit a customer. */
export function CustomerEditor({ customerId }: { customerId: string | null }) {
  const { api } = useSession();
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!customerId) return;
    const controller = new AbortController();
    getCustomer(api, customerId, controller.signal)
      .then(({ detail, etag }) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", customer: detail.customer, addresses: detail.addresses, etag });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: customerErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, customerId, attempt]);

  const reload = useCallback(() => {
    setLoad({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  const onAddresses = useCallback((addresses: Address[]) => setLoad((prev) => (prev.status === "ready" ? { ...prev, addresses } : prev)), []);

  if (!customerId) return <CustomerForm key="new" loaded={null} onSaved={() => undefined} onAddresses={onAddresses} onReload={reload} />;

  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Müşteri" />
        <p role="status" className="text-body text-fg-muted">
          Müşteri yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Müşteri" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Müşteri bulunamadı" : "Müşteri yüklenemedi"}
            description={load.notFound ? "Kayıt silinmiş olabilir veya bu işletmeye ait değil." : load.message}
            action={load.notFound ? undefined : <Button onClick={reload}>Tekrar dene</Button>}
          />
        </div>
      </>
    );
  }
  return (
    <CustomerForm
      key={`${load.customer.id}-${load.customer.version}`}
      loaded={load}
      onSaved={(next) => setLoad({ status: "ready", ...next })}
      onAddresses={onAddresses}
      onReload={reload}
    />
  );
}

function CustomerForm({
  loaded,
  onSaved,
  onAddresses,
  onReload,
}: {
  loaded: Loaded | null;
  onSaved: (loaded: Loaded) => void;
  onAddresses: (addresses: Address[]) => void;
  onReload: () => void;
}) {
  const { api, access } = useSession();
  const permissions = customerPermissions(access);
  const router = useRouter();
  const { toast } = useToast();
  const { state: reference } = useCustomerReference();
  const customer = loaded?.customer ?? null;
  const anonymized = customer?.anonymizedAt != null;
  const archived = customer?.status === "archived";
  const readOnly = !permissions.write || archived || anonymized;

  const [values, setValues] = useState<CustomerFormValues>(() => (customer ? customerToValues(customer) : newCustomerValues()));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const [statusDialog, setStatusDialog] = useState<"archive" | "restore" | null>(null);
  const [statusBusy, setStatusBusy] = useState(false);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [anonymizeOpen, setAnonymizeOpen] = useState(false);
  const summaryRef = useRef<HTMLDivElement>(null);
  const set = (patch: Partial<CustomerFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const showErrors = (fieldErrors: Record<string, string>, extra: string[] = []) => {
    const { onFields, unmatched } = partitionErrors(fieldErrors, FIELD_KEYS);
    setErrors(onFields);
    setSummary([...unmatched, ...extra]);
    requestAnimationFrame(() => summaryRef.current?.focus());
  };

  const handleFailure = (error: unknown) => {
    if (isVersionConflict(error)) {
      setConflict(true);
      setErrors({});
      setSummary([]);
      return;
    }
    if (error instanceof ApiError && error.status === 422 && Object.keys(error.fieldErrors).length > 0) {
      showErrors(customerFieldErrors(error));
      return;
    }
    if (error instanceof ApiError && error.code === "customers.code_taken") {
      showErrors({ code: customerErrorMessage(error) });
      return;
    }
    showErrors({}, [customerErrorMessage(error)]);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (readOnly || saving) return;
    setConflict(false);
    const clientErrors = validateCustomer(values);
    if (Object.keys(clientErrors).length > 0) {
      showErrors(clientErrors);
      return;
    }
    setSaving(true);
    try {
      if (!loaded) {
        const body = buildCreateCustomer(values, permissions.assignPriceLists);
        const created = await createCustomer(api, body, keys.keyFor(body));
        keys.reset();
        toast({ tone: "success", title: "Müşteri eklendi", description: `${created.customer.name} — şimdi adres ekleyebilirsiniz.` });
        router.push(customerHref(created.customer.id));
        return;
      }
      const patch = buildCustomerPatch(loaded.customer, values, permissions.assignPriceLists);
      if (Object.keys(patch).length === 0) {
        toast({ tone: "info", title: "Değişiklik yok" });
        return;
      }
      const updated = await updateCustomer(api, loaded.customer.id, patch, loaded.etag);
      toast({ tone: "success", title: "Müşteri kaydedildi", description: updated.detail.customer.name });
      // Addresses only change through their own endpoints: keep the list on screen.
      onSaved({ customer: updated.detail.customer, addresses: loaded.addresses, etag: updated.etag });
    } catch (error) {
      handleFailure(error);
    } finally {
      setSaving(false);
    }
  };

  const changeStatus = async (status: "archived" | "active") => {
    if (!loaded) return;
    setStatusBusy(true);
    setStatusError(null);
    try {
      const updated = await updateCustomer(api, loaded.customer.id, { status }, loaded.etag);
      toast({ tone: "success", title: status === "archived" ? "Müşteri arşivlendi" : "Müşteri arşivden çıkarıldı", description: updated.detail.customer.name });
      setStatusDialog(null);
      onSaved({ customer: updated.detail.customer, addresses: loaded.addresses, etag: updated.etag });
    } catch (error) {
      if (isVersionConflict(error)) {
        setStatusDialog(null);
        setConflict(true);
      } else {
        setStatusError(customerErrorMessage(error));
      }
    } finally {
      setStatusBusy(false);
    }
  };

  const ready = reference.status === "ready" ? reference : null;
  const groupOptions = assignableOptions(ready?.groups ?? [], values.dealerGroupId);
  const listOptions = assignableOptions(ready?.priceLists ?? [], values.priceListId, (l) => l.kind === "sales");
  const assignDisabled = readOnly || !permissions.assignPriceLists || !ready;
  const errorCount = Object.keys(errors).length + summary.length;
  const title = customer ? customer.name : "Yeni müşteri";

  return (
    <>
      <BackLink />
      <PageHeader
        title={title}
        description={
          customer ? (
            <span className="inline-flex flex-wrap items-center gap-2">
              <span className="font-mono">{customer.code}</span>
              <CustomerStatusBadge status={customer.status} anonymized={anonymized} />
            </span>
          ) : (
            "Firma bilgileri, iletişim ve ticari koşullar. Adresleri kaydettikten sonra ekleyebilirsiniz."
          )
        }
        actions={
          customer && permissions.write && !anonymized ? (
            archived ? (
              <Button variant="secondary" icon={<ArchiveRestore className="size-4" />} onClick={() => setStatusDialog("restore")}>
                Arşivden çıkar
              </Button>
            ) : (
              <Button variant="secondary" icon={<Archive className="size-4" />} onClick={() => setStatusDialog("archive")}>
                Arşivle
              </Button>
            )
          ) : null
        }
      />

      {anonymized ? (
        <FormAlert tone="warning" title="Müşteri anonimleştirildi">
          Kişisel bilgiler KVKK talebiyle silindi; kayıt yalnızca sipariş geçmişinin bütünlüğü için tutuluyor ve değiştirilemez.
        </FormAlert>
      ) : archived ? (
        <FormAlert tone="info" title="Arşivlenmiş müşteri">
          Arşivdeki müşteri değiştirilemez ve yeni siparişlerde seçilemez. Düzenlemek için önce arşivden çıkarın.
        </FormAlert>
      ) : !permissions.write ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Müşteri kayıtlarını işletme sahibi, yönetici, operasyon müdürü veya satış personeli değiştirebilir.
        </FormAlert>
      ) : null}

      {conflict ? (
        <FormAlert
          tone="warning"
          title="Kayıt başkası tarafından değiştirildi"
          actions={
            <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={onReload}>
              Yeniden yükle
            </Button>
          }
        >
          Bu müşteri siz düzenlerken başka biri tarafından güncellendi; değişiklikleriniz kaydedilmedi. Güncel hâlini yeniden yükleyin, ardından
          değişikliklerinizi tekrar yapın.
        </FormAlert>
      ) : null}
      {errorCount > 0 ? (
        <FormAlert ref={summaryRef} tone="error" title="Kaydedilemedi">
          <p>İşaretli alanları düzeltip tekrar deneyin.</p>
          {summary.length > 0 ? (
            <ul className="mt-1 list-disc pl-5">
              {summary.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          ) : null}
        </FormAlert>
      ) : null}

      <form noValidate onSubmit={submit} aria-label={customer ? "Müşteriyi düzenle" : "Yeni müşteri"} className="flex flex-col gap-4">
        <fieldset disabled={readOnly} className="flex flex-col gap-4">
          <section aria-labelledby="sec-firm" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
            <h2 id="sec-firm" className="text-h3 text-fg">
              Firma bilgileri
            </h2>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Input
                label="Müşteri kodu"
                required
                maxLength={CODE_MAX}
                value={values.code}
                error={errors.code}
                hint="Boşluksuz, ör. C-001. Siparişlerde ve aramada kullanılır."
                autoComplete="off"
                onChange={(e) => set({ code: e.target.value })}
              />
              <Input label="Firma / müşteri adı" required maxLength={NAME_MAX} value={values.name} error={errors.name} onChange={(e) => set({ name: e.target.value })} />
              <Input label="Yetkili kişi" maxLength={SHORT_TEXT_MAX} value={values.contactName} error={errors.contactName} onChange={(e) => set({ contactName: e.target.value })} />
              <Input
                label="Vergi no / TCKN"
                inputMode="numeric"
                maxLength={13}
                value={values.taxNumber}
                error={errors.taxNumber}
                hint="10 haneli VKN veya şahıs firmasında 11 haneli TCKN. TCKN listelerde maskeli gösterilir."
                autoComplete="off"
                onChange={(e) => set({ taxNumber: e.target.value })}
              />
              <Input label="Vergi dairesi" maxLength={SHORT_TEXT_MAX} value={values.taxOffice} error={errors.taxOffice} onChange={(e) => set({ taxOffice: e.target.value })} />
            </div>
          </section>

          <section aria-labelledby="sec-contact" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
            <h2 id="sec-contact" className="text-h3 text-fg">
              İletişim
            </h2>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Input label="Telefon" type="tel" value={values.phone} error={errors.phone} autoComplete="off" onChange={(e) => set({ phone: e.target.value })} />
              <Input label="E-posta" type="email" value={values.email} error={errors.email} autoComplete="off" onChange={(e) => set({ email: e.target.value })} />
            </div>
            <Textarea
              label="WhatsApp numaraları"
              rows={2}
              value={values.whatsappNumbers}
              error={errors.whatsappNumbers}
              hint="Her satıra bir numara, ülke koduyla (ör. +905321234567); en fazla 5. WhatsApp'tan gelen siparişte müşteri bu numarayla bulunur."
              onChange={(e) => set({ whatsappNumbers: e.target.value })}
            />
          </section>

          <section aria-labelledby="sec-commercial" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
            <h2 id="sec-commercial" className="text-h3 text-fg">
              Ticari koşullar
            </h2>
            {!permissions.assignPriceLists ? (
              <p className="text-caption text-fg-muted">Bayi grubu ve fiyat listesi atama yetkiniz yok; atama için işletme yöneticinizle görüşün.</p>
            ) : null}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Select
                label="Bayi grubu"
                placeholder="Grup yok"
                options={groupOptions}
                value={values.dealerGroupId}
                error={errors.dealerGroupId}
                disabled={assignDisabled}
                hint="Grubun fiyat listesi ve varsayılan iskontosu uygulanır."
                onChange={(e) => set({ dealerGroupId: e.target.value })}
              />
              <Select
                label="Müşteriye özel fiyat listesi"
                placeholder="Liste yok (grup veya varsayılan liste)"
                options={listOptions}
                value={values.priceListId}
                error={errors.priceListId}
                disabled={assignDisabled || (ready !== null && !ready.priceListsAvailable)}
                hint={
                  ready && !ready.priceListsAvailable
                    ? "Fiyat listeleri bu rol için görüntülenemiyor."
                    : "Fiyat sırası: müşteri listesi → bayi grubu listesi → varsayılan liste."
                }
                onChange={(e) => set({ priceListId: e.target.value })}
              />
              <Input
                label="Vade (gün)"
                inputMode="numeric"
                value={values.paymentTermDays}
                error={errors.paymentTermDays}
                hint="0–3650"
                onChange={(e) => set({ paymentTermDays: e.target.value })}
              />
              <Select
                label="Para birimi"
                placeholder="İşletmenin para birimi"
                options={currencyOptions(values.currency)}
                value={values.currency}
                error={errors.currency}
                onChange={(e) => set({ currency: e.target.value })}
              />
              <Input
                label="Kredi limiti"
                inputMode="decimal"
                value={values.creditLimit}
                error={errors.creditLimit}
                hint="Ör. 250000 veya 250000,50 (binlik ayırıcı kullanmayın)."
                suffix={<span className="text-caption">{values.currency || "₺"}</span>}
                onChange={(e) => set({ creditLimit: e.target.value })}
              />
            </div>
            {customer ? (
              <CheckboxField
                label="Aktif"
                checked={values.status === "active"}
                onChange={(v) => set({ status: v ? "active" : "inactive" })}
                hint="Pasif müşteri listede kalır ama yeni siparişte önerilmez."
              />
            ) : null}
          </section>

          <section aria-labelledby="sec-notes" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
            <h2 id="sec-notes" className="text-h3 text-fg">
              Notlar
            </h2>
            <Textarea label="İç not" rows={3} maxLength={NOTES_MAX} value={values.notes} error={errors.notes} hint="Yalnızca ekibiniz görür." onChange={(e) => set({ notes: e.target.value })} />
          </section>
        </fieldset>

        {!readOnly ? (
          <div className="sticky bottom-0 z-(--bos-z-sticky) -mx-4 flex flex-wrap justify-end gap-2 border-t border-line bg-page/95 px-4 py-3 backdrop-blur sm:mx-0 sm:rounded-lg sm:border">
            <Link href={CUSTOMERS_HREF} className={buttonClasses("secondary")}>
              Vazgeç
            </Link>
            <Button type="submit" loading={saving}>
              {customer ? "Kaydet" : "Müşteriyi ekle"}
            </Button>
          </div>
        ) : null}
      </form>

      {loaded && !anonymized ? <AddressSection customer={loaded.customer} initial={loaded.addresses} canEdit={permissions.write && !archived} onChange={onAddresses} /> : null}

      {loaded && permissions.anonymize && !anonymized ? (
        <section aria-labelledby="sec-kvkk" className="flex flex-col gap-3 rounded-lg border border-danger-border bg-card p-4 shadow-e1 sm:p-6">
          <h2 id="sec-kvkk" className="flex items-center gap-2 text-h3 text-fg">
            <ShieldAlert className="size-5 text-danger-fg" aria-hidden="true" />
            KVKK: kişisel verileri sil
          </h2>
          <p className="text-body text-fg-secondary">
            İlgili kişi başvurusunda kullanılır. Ad, iletişim, vergi bilgileri ve tüm adresler silinir, müşteri arşivlenir; sipariş geçmişi korunur.
            Bu işlem geri alınamaz.
          </p>
          <div>
            <Button variant="danger" onClick={() => setAnonymizeOpen(true)}>
              Müşteriyi anonimleştir
            </Button>
          </div>
        </section>
      ) : null}

      {statusDialog && loaded ? (
        <Dialog
          open
          role="alertdialog"
          onClose={() => setStatusDialog(null)}
          title={statusDialog === "archive" ? "Müşteri arşivlensin mi?" : "Müşteri arşivden çıkarılsın mı?"}
          description={
            statusDialog === "archive"
              ? `${loaded.customer.name} listede gizlenir ve yeni siparişte seçilemez. Geçmiş siparişler etkilenmez; istediğiniz zaman arşivden çıkarabilirsiniz.`
              : `${loaded.customer.name} yeniden aktif olur.`
          }
          dismissOnBackdrop={false}
          footer={
            <>
              <Button variant="secondary" onClick={() => setStatusDialog(null)} disabled={statusBusy}>
                Vazgeç
              </Button>
              <Button
                variant={statusDialog === "archive" ? "danger" : "primary"}
                loading={statusBusy}
                onClick={() => void changeStatus(statusDialog === "archive" ? "archived" : "active")}
              >
                {statusDialog === "archive" ? "Arşivle" : "Arşivden çıkar"}
              </Button>
            </>
          }
        >
          {statusError ? (
            <p role="alert" className="text-body text-danger-fg">
              {statusError}
            </p>
          ) : null}
        </Dialog>
      ) : null}

      {anonymizeOpen && loaded ? (
        <AnonymizeDialog
          customer={loaded.customer}
          onClose={() => setAnonymizeOpen(false)}
          onDone={(detail) => {
            setAnonymizeOpen(false);
            onSaved({ customer: detail.customer, addresses: [], etag: `W/"${detail.customer.version}"` });
          }}
        />
      ) : null}
    </>
  );
}

"use client";

import { useCallback, useState, type FormEvent, useId } from "react";
import { MapPin, Pencil, Plus, Star, Trash2 } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { isVersionConflict } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { createAddress, deleteAddress, listAddresses, updateAddress, type Address, type Customer } from "@/lib/customers/api";
import {
  ADDRESS_TYPES,
  NAME_MAX,
  SHORT_TEXT_MAX,
  addressToValues,
  addressTypeLabels,
  buildAddressPatch,
  buildCreateAddress,
  newAddressValues,
  validateAddress,
  type AddressFormValues,
  type AddressType,
} from "@/lib/customers/customer-form";
import { customerErrorMessage, customerFieldErrors } from "@/lib/customers/errors";
import { useSession } from "@/lib/session/session-context";

const ADDRESS_CONFLICT = "Adres siz düzenlerken değişti. Liste yenilendi; değişikliğinizi tekrar yapın.";

function sortAddresses(addresses: Address[]): Address[] {
  return [...addresses].sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.createdAt.localeCompare(b.createdAt));
}

/** Addresses of a customer, grouped by type; one default per type (the API keeps it unique). */
export function AddressSection({
  customer,
  initial,
  canEdit,
  onChange,
}: {
  customer: Customer;
  initial: Address[];
  canEdit: boolean;
  /** Keeps the page's copy in sync (the customer form remounts after a save). */
  onChange?: (addresses: Address[]) => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [addresses, setAddresses] = useState<Address[]>(initial);
  const [editing, setEditing] = useState<Address | { type: AddressType } | null>(null);
  const [deleting, setDeleting] = useState<Address | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [message, setMessage] = useState<{ tone: "error" | "warning"; text: string } | null>(null);

  const refresh = useCallback(async () => {
    try {
      const next = await listAddresses(api, customer.id);
      setAddresses(next);
      onChange?.(next);
    } catch (error) {
      setMessage({ tone: "error", text: customerErrorMessage(error) });
    }
  }, [api, customer.id, onChange]);

  const makeDefault = async (address: Address) => {
    setBusyId(address.id);
    setMessage(null);
    try {
      await updateAddress(api, address, { isDefault: true });
      toast({ tone: "success", title: "Varsayılan adres değişti", description: `${addressTypeLabels[address.type] ?? address.type}: ${address.title ?? address.line1}` });
      // The previous default of this type was cleared by the API: reload the whole list.
      await refresh();
    } catch (error) {
      if (isVersionConflict(error)) {
        setMessage({ tone: "warning", text: ADDRESS_CONFLICT });
        await refresh();
      } else {
        setMessage({ tone: "error", text: customerErrorMessage(error) });
      }
    } finally {
      setBusyId(null);
    }
  };

  const remove = async (address: Address) => {
    setBusyId(address.id);
    setMessage(null);
    try {
      await deleteAddress(api, address);
      setDeleting(null);
      toast({ tone: "success", title: "Adres silindi" });
      await refresh();
    } catch (error) {
      setDeleting(null);
      setMessage({ tone: "error", text: customerErrorMessage(error) });
    } finally {
      setBusyId(null);
    }
  };

  return (
    <section aria-labelledby={`${uid}-sec-addresses`} className="flex flex-col gap-4 rounded-xl border border-line bg-card p-4 shadow-e1 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`${uid}-sec-addresses`} className="flex items-center gap-2 text-h3 text-fg">
          <MapPin className="size-5" aria-hidden="true" />
          Adresler
        </h2>
        {canEdit ? (
          <Button variant="secondary" icon={<Plus className="size-4" />} onClick={() => setEditing({ type: "shipping" })}>
            Adres ekle
          </Button>
        ) : null}
      </div>
      {message ? (
        <FormAlert tone={message.tone} title={message.tone === "warning" ? "Liste güncellendi" : "İşlem yapılamadı"}>
          {message.text}
        </FormAlert>
      ) : null}

      {ADDRESS_TYPES.map((type) => {
        const ofType = sortAddresses(addresses.filter((a) => a.type === type));
        return (
          <div key={type} className="flex flex-col gap-2">
            <h3 className="text-body font-semibold text-fg">{addressTypeLabels[type]} adresleri</h3>
            {ofType.length === 0 ? (
              <p className="text-caption text-fg-muted">Henüz {addressTypeLabels[type].toLocaleLowerCase("tr")} adresi yok. İlk eklenen adres varsayılan olur.</p>
            ) : (
              <ul aria-label={`${addressTypeLabels[type]} adresleri`} className="grid grid-cols-1 gap-2 lg:grid-cols-2">
                {ofType.map((a) => (
                  <li key={a.id} className="flex flex-col gap-2 rounded-md border border-line p-3">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <span className="font-medium text-fg">{a.title ?? addressTypeLabels[a.type] ?? a.type}</span>
                      {a.isDefault ? (
                        <Badge tone="brand" icon={<Star className="size-3" aria-hidden="true" />}>
                          Varsayılan
                        </Badge>
                      ) : null}
                    </div>
                    <address className="text-body not-italic text-fg-secondary">
                      {a.line1}
                      {a.line2 ? <>, {a.line2}</> : null}
                      <br />
                      {[a.district, a.city, a.postalCode].filter(Boolean).join(" / ")} · {a.country}
                      {a.contactName || a.contactPhone ? (
                        <>
                          <br />
                          {[a.contactName, a.contactPhone].filter(Boolean).join(" · ")}
                        </>
                      ) : null}
                    </address>
                    {canEdit ? (
                      <div className="flex flex-wrap gap-2">
                        {!a.isDefault ? (
                          <Button
                            variant="secondary"
                            size="sm"
                            icon={<Star className="size-4" />}
                            loading={busyId === a.id}
                            onClick={() => void makeDefault(a)}
                            aria-label={`${a.title ?? a.line1} adresini varsayılan yap (${addressTypeLabels[a.type]?.toLocaleLowerCase("tr") ?? a.type})`}
                          >
                            Varsayılan yap
                          </Button>
                        ) : null}
                        <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} onClick={() => setEditing(a)} aria-label={`${a.title ?? a.line1} adresini düzenle`}>
                          Düzenle
                        </Button>
                        <Button variant="ghost" size="sm" icon={<Trash2 className="size-4" />} onClick={() => setDeleting(a)} aria-label={`${a.title ?? a.line1} adresini sil`}>
                          Sil
                        </Button>
                      </div>
                    ) : null}
                  </li>
                ))}
              </ul>
            )}
          </div>
        );
      })}
      {addresses.length === 0 && !canEdit ? (
        <EmptyState headingLevel="h3" icon={<MapPin className="size-6" />} title="Adres yok" description="Bu müşteriye henüz adres eklenmemiş." />
      ) : null}

      {editing ? (
        <AddressDialog
          customer={customer}
          address={"id" in editing ? editing : null}
          defaultType={"id" in editing ? editing.type === "billing" ? "billing" : "shipping" : editing.type}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await refresh();
          }}
          onConflict={async () => {
            setEditing(null);
            setMessage({ tone: "warning", text: ADDRESS_CONFLICT });
            await refresh();
          }}
        />
      ) : null}

      {deleting ? (
        <Dialog
          open
          role="alertdialog"
          onClose={() => setDeleting(null)}
          title="Adres silinsin mi?"
          description={
            deleting.isDefault
              ? "Bu varsayılan adres. Silinirse aynı türdeki en eski adres varsayılan olur."
              : "Adres kalıcı olarak silinir; geçmiş siparişlerdeki adres bilgisi etkilenmez."
          }
          dismissOnBackdrop={false}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDeleting(null)} disabled={busyId === deleting.id}>
                Vazgeç
              </Button>
              <Button variant="danger" loading={busyId === deleting.id} onClick={() => void remove(deleting)}>
                Adresi sil
              </Button>
            </>
          }
        >
          <p>
            {deleting.line1}, {deleting.city}
          </p>
        </Dialog>
      ) : null}
    </section>
  );
}

function AddressDialog({
  customer,
  address,
  defaultType,
  onClose,
  onSaved,
  onConflict,
}: {
  customer: Customer;
  address: Address | null;
  defaultType: AddressType;
  onClose: () => void;
  onSaved: () => void;
  onConflict: () => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [values, setValues] = useState<AddressFormValues>(() => (address ? addressToValues(address) : newAddressValues(defaultType)));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = (patch: Partial<AddressFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validateAddress(values);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    setSaving(true);
    try {
      if (!address) {
        const body = buildCreateAddress(values);
        await createAddress(api, customer.id, body, keys.keyFor(body));
        toast({ tone: "success", title: "Adres eklendi" });
      } else {
        const patch = buildAddressPatch(address, values);
        if (Object.keys(patch).length === 0) {
          onClose();
          return;
        }
        await updateAddress(api, address, patch);
        toast({ tone: "success", title: "Adres güncellendi" });
      }
      onSaved();
    } catch (error) {
      if (isVersionConflict(error)) {
        onConflict();
        return;
      }
      if (error instanceof ApiError && error.status === 422) {
        setErrors(customerFieldErrors(error));
        setFormError(error.userMessage);
      } else {
        setFormError(customerErrorMessage(error));
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      open
      size="lg"
      onClose={onClose}
      title={address ? "Adresi düzenle" : `Yeni adres — ${customer.name}`}
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={`${uid}-address-form`} loading={saving}>
            {address ? "Kaydet" : "Adresi ekle"}
          </Button>
        </>
      }
    >
      <form id={`${uid}-address-form`} noValidate onSubmit={submit} className="flex flex-col gap-4">
        {formError ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {formError}
          </FormAlert>
        ) : null}
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Select
            label="Adres türü"
            options={ADDRESS_TYPES.map((t) => ({ value: t, label: addressTypeLabels[t] }))}
            value={values.type}
            error={errors.type}
            disabled={address?.isDefault}
            hint={address?.isDefault ? "Varsayılan adresin türü değiştirilemez." : undefined}
            onChange={(e) => set({ type: e.target.value as AddressType })}
          />
          <Input label="Adres adı" maxLength={SHORT_TEXT_MAX} value={values.title} error={errors.title} hint="Ör. Merkez, Depo" onChange={(e) => set({ title: e.target.value })} />
        </div>
        <Input label="Adres satırı 1" required maxLength={NAME_MAX} value={values.line1} error={errors.line1} onChange={(e) => set({ line1: e.target.value })} />
        <Input label="Adres satırı 2" maxLength={NAME_MAX} value={values.line2} error={errors.line2} onChange={(e) => set({ line2: e.target.value })} />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Input label="İlçe" maxLength={SHORT_TEXT_MAX} value={values.district} error={errors.district} onChange={(e) => set({ district: e.target.value })} />
          <Input label="İl" required maxLength={SHORT_TEXT_MAX} value={values.city} error={errors.city} onChange={(e) => set({ city: e.target.value })} />
          <Input label="Posta kodu" maxLength={20} value={values.postalCode} error={errors.postalCode} onChange={(e) => set({ postalCode: e.target.value })} />
          <Input label="Ülke kodu" required maxLength={2} value={values.country} error={errors.country} hint="İki harf, ör. TR" onChange={(e) => set({ country: e.target.value.toUpperCase() })} />
          <Input label="Teslim alacak kişi" maxLength={SHORT_TEXT_MAX} value={values.contactName} error={errors.contactName} onChange={(e) => set({ contactName: e.target.value })} />
          <Input label="Telefon" type="tel" value={values.contactPhone} error={errors.contactPhone} onChange={(e) => set({ contactPhone: e.target.value })} />
        </div>
        {!address ? (
          <CheckboxField
            label="Bu türün varsayılan adresi yap"
            checked={values.isDefault}
            onChange={(v) => set({ isDefault: v })}
            hint="Türün ilk adresi her zaman varsayılan olur."
          />
        ) : null}
      </form>
    </Dialog>
  );
}

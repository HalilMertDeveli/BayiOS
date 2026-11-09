"use client";

import { useState, type FormEvent, type ReactNode, useId } from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { CircleAlert, Lock, MapPin, PackagePlus, Pencil, Plus, Star, Warehouse as WarehouseIcon } from "lucide-react";
import { LockedFeature } from "@/components/billing/locked-feature";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { FEATURES, featureGate, warehouseGate, type Gate } from "@/lib/billing/entitlements";
import { isVersionConflict } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { createLocation, createWarehouse, updateLocation, updateWarehouse, type Location, type Warehouse } from "@/lib/inventory/api";
import { RECEIPT_HREF } from "@/lib/inventory/balance-query";
import { inventoryErrorMessage, inventoryFieldErrors } from "@/lib/inventory/errors";
import { inventoryPermissions } from "@/lib/inventory/permissions";
import {
  LOCATION_BARCODE_MAX,
  LOCATION_CODE_MAX,
  WAREHOUSE_CODE_MAX,
  WAREHOUSE_NAME_MAX,
  buildCreateLocation,
  buildCreateWarehouse,
  buildLocationPatch,
  buildWarehousePatch,
  locationToValues,
  newLocationValues,
  newWarehouseValues,
  validateLocation,
  validateWarehouse,
  warehouseToValues,
  type LocationFormValues,
  type WarehouseFormValues,
} from "@/lib/inventory/warehouse-form";
import { useSession } from "@/lib/session/session-context";
import { useLocations, useWarehouses } from "./use-inventory-data";

const VERSION_CONFLICT = "Kayıt siz düzenlerken değişti. Pencereyi kapatıp listeyi yenileyin, sonra tekrar deneyin.";

function mutationError(error: unknown): { fields: Record<string, string>; message: string } {
  if (error instanceof ApiError && error.status === 422) return { fields: inventoryFieldErrors(error), message: error.userMessage };
  if (isVersionConflict(error)) return { fields: {}, message: VERSION_CONFLICT };
  return { fields: {}, message: inventoryErrorMessage(error) };
}

/** /depo: warehouses and their shelf/bin locations (create, edit, deactivate) + entry to goods receipt. */
export function WarehouseAdmin() {
  const uid = useId();
  const { access, entitlements } = useSession();
  const permissions = inventoryPermissions(access);
  const { state, reload, upsert } = useWarehouses();
  // Entitlement gates (#51): Temel allows one active warehouse and no shelf/bin locations. Visibility only; existing
  // warehouses and locations stay visible, only new ones are locked (the API enforces it again).
  const newWarehouseGate = state.status === "ready" ? warehouseGate(entitlements, state.data) : ({ locked: false } as const);
  const locationsGate = featureGate(entitlements, FEATURES.warehouseLocations);
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const [editing, setEditing] = useState<Warehouse | "new" | null>(null);

  const warehouses = state.status === "ready" ? state.data : [];
  const selectedId = searchParams.get("depo") ?? warehouses[0]?.id ?? "";
  const selected = warehouses.find((w) => w.id === selectedId) ?? null;
  const select = (id: string) => router.replace(`${pathname}?depo=${encodeURIComponent(id)}`, { scroll: false });

  const columns: TableColumn<Warehouse>[] = [
    { key: "code", header: "Kod", width: "8rem", cell: (w) => <span className="font-mono text-caption">{w.code}</span> },
    {
      key: "name",
      header: "Depo",
      cell: (w) => (
        <span className="inline-flex flex-wrap items-center gap-2">
          <span className="font-medium">{w.name}</span>
          {w.isDefault ? (
            <Badge tone="brand" icon={<Star className="size-3" aria-hidden="true" />}>
              Varsayılan
            </Badge>
          ) : null}
        </span>
      ),
    },
    { key: "locations", header: "Raf/göz takibi", cell: (w) => (w.usesLocations ? "Açık" : "Kapalı") },
    { key: "status", header: "Durum", width: "7rem", cell: (w) => <Badge tone={w.status === "active" ? "success" : "neutral"}>{w.status === "active" ? "Aktif" : "Pasif"}</Badge> },
    {
      key: "actions",
      header: <span className="sr-only">İşlemler</span>,
      width: permissions.manageWarehouses ? "16rem" : "9rem",
      cell: (w) => (
        <span className="flex justify-end gap-1">
          <Button variant={w.id === selectedId ? "secondary" : "ghost"} size="sm" aria-pressed={w.id === selectedId} onClick={() => select(w.id)}>
            Lokasyonlar
            <span className="sr-only"> — {w.name}</span>
          </Button>
          {permissions.manageWarehouses ? (
            <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} onClick={() => setEditing(w)} aria-label={`${w.name} deposunu düzenle`}>
              Düzenle
            </Button>
          ) : null}
        </span>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        title="Depo"
        description="Depolar, raf/göz lokasyonları ve mal kabul."
        actions={
          <>
            {permissions.manageWarehouses ? (
              <Button
                variant="secondary"
                icon={newWarehouseGate.locked ? <Lock className="size-4" /> : <Plus className="size-4" />}
                disabled={newWarehouseGate.locked}
                aria-describedby={newWarehouseGate.locked ? `${uid}-warehouse-limit` : undefined}
                onClick={() => setEditing("new")}
              >
                Yeni depo
              </Button>
            ) : null}
            {permissions.receive ? (
              <Link href={RECEIPT_HREF} className={buttonClasses("primary", "lg")}>
                <PackagePlus className="size-5" aria-hidden="true" />
                Mal kabul
              </Link>
            ) : null}
          </>
        }
      />

      {permissions.manageWarehouses && newWarehouseGate.locked ? (
        <div id={`${uid}-warehouse-limit`}>
          <LockedFeature gate={newWarehouseGate} title="Yeni aktif depo eklenemiyor" />
        </div>
      ) : null}

      {!permissions.manageWarehouses ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Depo ve lokasyon tanımlarını işletme sahibi, yönetici veya operasyon müdürü değiştirebilir.
        </FormAlert>
      ) : null}

      <section aria-labelledby={`${uid}-warehouse-list`} className="flex flex-col gap-3">
        <h2 id={`${uid}-warehouse-list`} className="text-h3 text-fg">
          Depolar
        </h2>
        {state.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState
              headingLevel="h3"
              icon={<CircleAlert className="size-6" />}
              title="Depolar yüklenemedi"
              description={state.message}
              action={<Button onClick={reload}>Tekrar dene</Button>}
            />
          </div>
        ) : (
          <>
            <div className="hidden md:block">
              <Table
                caption="Depolar"
                columns={columns}
                rows={warehouses}
                getRowId={(w) => w.id}
                loading={state.status === "loading"}
                loadingRows={2}
                empty={
                  <EmptyState
                    headingLevel="h3"
                    icon={<WarehouseIcon className="size-6" />}
                    title="Henüz depo yok"
                    description="İlk mal kabulde “Ana Depo” otomatik oluşturulur ya da şimdi bir depo ekleyin."
                  />
                }
              />
            </div>
            <ul aria-label="Depolar" className="flex flex-col gap-2 md:hidden">
              {state.status === "loading" ? <li className="py-6 text-center text-body text-fg-muted">Yükleniyor…</li> : null}
              {state.status === "ready" && warehouses.length === 0 ? (
                <li className="rounded-lg border border-line bg-card p-4 text-body text-fg-muted">Henüz depo yok. İlk mal kabulde “Ana Depo” otomatik oluşturulur.</li>
              ) : null}
              {warehouses.map((w) => (
                <li key={w.id} className="rounded-lg border border-line bg-card p-4 shadow-e1">
                  <div className="flex items-start justify-between gap-2">
                    <div className="flex flex-col">
                      <span className="font-medium text-fg">{w.name}</span>
                      <span className="font-mono text-caption text-fg-muted">{w.code}</span>
                    </div>
                    <div className="flex flex-wrap justify-end gap-1">
                      {w.isDefault ? <Badge tone="brand">Varsayılan</Badge> : null}
                      <Badge tone={w.status === "active" ? "success" : "neutral"}>{w.status === "active" ? "Aktif" : "Pasif"}</Badge>
                    </div>
                  </div>
                  <p className="mt-1 text-caption text-fg-muted">Raf/göz takibi {w.usesLocations ? "açık" : "kapalı"}</p>
                  <div className="mt-3 flex flex-wrap gap-2">
                    <Button variant="secondary" size="lg" aria-pressed={w.id === selectedId} onClick={() => select(w.id)}>
                      Lokasyonlar<span className="sr-only"> — {w.name}</span>
                    </Button>
                    {permissions.manageWarehouses ? (
                      <Button variant="ghost" size="lg" icon={<Pencil className="size-4" />} onClick={() => setEditing(w)} aria-label={`${w.name} deposunu düzenle`}>
                        Düzenle
                      </Button>
                    ) : null}
                  </div>
                </li>
              ))}
            </ul>
          </>
        )}
      </section>

      {selected ? (
        <LocationsPanel key={selected.id} warehouse={selected} canManage={permissions.manageWarehouses} locationsGate={locationsGate} />
      ) : null}

      {editing ? (
        <WarehouseDialog
          locationsGate={locationsGate}
          warehouse={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={(w) => {
            upsert(w);
            setEditing(null);
          }}
        />
      ) : null}
    </>
  );
}

function CheckboxField({ label, hint, checked, disabled, onChange }: { label: string; hint?: ReactNode; checked: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  return (
    <div className="flex flex-col gap-1">
      <label className="inline-flex min-h-11 items-center gap-3 text-body text-fg">
        <input type="checkbox" className="size-5 accent-(--bos-brand-primary)" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
        {label}
      </label>
      {hint ? <p className="text-caption text-fg-muted">{hint}</p> : null}
    </div>
  );
}

function WarehouseDialog({
  warehouse,
  locationsGate,
  onClose,
  onSaved,
}: {
  warehouse: Warehouse | null;
  locationsGate: Gate;
  onClose: () => void;
  onSaved: (w: Warehouse) => void;
}) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const mode = warehouse ? "edit" : "create";
  const [values, setValues] = useState<WarehouseFormValues>(() => (warehouse ? warehouseToValues(warehouse) : newWarehouseValues()));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = (patch: Partial<WarehouseFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validateWarehouse(values, mode);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    setSaving(true);
    try {
      if (!warehouse) {
        const body = buildCreateWarehouse(values);
        const created = await createWarehouse(api, body, keys.keyFor(body));
        toast({ tone: "success", title: "Depo eklendi", description: created.isDefault ? `${created.name} varsayılan depo oldu.` : created.name });
        onSaved(created);
      } else {
        const patch = buildWarehousePatch(warehouse, values);
        if (Object.keys(patch).length === 0) {
          onClose();
          return;
        }
        const updated = await updateWarehouse(api, warehouse, patch);
        toast({ tone: "success", title: "Depo güncellendi", description: updated.name });
        onSaved(updated);
      }
    } catch (error) {
      const { fields, message } = mutationError(error);
      setErrors(fields);
      setFormError(message);
    } finally {
      setSaving(false);
    }
  };

  const isDefault = warehouse?.isDefault ?? false;
  return (
    <Dialog
      open
      onClose={onClose}
      title={warehouse ? `Depoyu düzenle: ${warehouse.name}` : "Yeni depo"}
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={`${uid}-warehouse-form`} loading={saving}>
            {warehouse ? "Kaydet" : "Depoyu ekle"}
          </Button>
        </>
      }
    >
      <form id={`${uid}-warehouse-form`} noValidate onSubmit={submit} className="flex flex-col gap-4">
        {formError ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {formError}
          </FormAlert>
        ) : null}
        {warehouse ? (
          <p className="text-body text-fg-secondary">
            Kod: <span className="font-mono">{warehouse.code}</span> (değiştirilemez)
          </p>
        ) : (
          <Input
            label="Depo kodu"
            required
            maxLength={WAREHOUSE_CODE_MAX}
            value={values.code}
            error={errors.code}
            hint="Kısa ve boşluksuz, ör. MERKEZ veya DUKKAN."
            autoComplete="off"
            onChange={(e) => set({ code: e.target.value })}
          />
        )}
        <Input label="Depo adı" required maxLength={WAREHOUSE_NAME_MAX} value={values.name} error={errors.name} onChange={(e) => set({ name: e.target.value })} />
        <CheckboxField
          label="Raf/göz (lokasyon) takibi"
          checked={values.usesLocations}
          // Turning tracking on needs the warehouse_locations entitlement; turning it off never does.
          disabled={locationsGate.locked && !warehouse?.usesLocations}
          onChange={(v) => set({ usesLocations: v })}
          hint={
            errors.usesLocations ??
            (warehouse ? "Yalnızca depoda stok yokken değiştirilebilir. Açıkken mal kabul ve düzeltmede raf seçmek zorunludur." : "Açıkken mal kabul ve düzeltmede raf seçmek zorunludur.")
          }
        />
        {locationsGate.locked && !warehouse?.usesLocations ? <LockedFeature gate={locationsGate} compact /> : null}
        {warehouse ? (
          <>
            <CheckboxField
              label="Aktif"
              checked={values.active}
              disabled={isDefault}
              onChange={(v) => set({ active: v })}
              hint={isDefault ? "Varsayılan depo pasif yapılamaz. Önce başka bir depoyu varsayılan yapın." : "Pasif depoya mal kabul ve düzeltme yapılamaz."}
            />
            {!isDefault ? (
              <CheckboxField
                label="Varsayılan depo yap"
                checked={values.makeDefault}
                onChange={(v) => set({ makeDefault: v, ...(v ? { active: true } : {}) })}
                hint="Mal kabul ve düzeltmede önce bu depo seçilir."
              />
            ) : null}
          </>
        ) : null}
      </form>
    </Dialog>
  );
}

function LocationsPanel({ warehouse, canManage: canManageRole, locationsGate }: { warehouse: Warehouse; canManage: boolean; locationsGate: Gate }) {
  const uid = useId();
  // Without warehouse_locations, existing locations stay listed (read-only); adding or editing them is locked.
  const canManage = canManageRole && !locationsGate.locked;
  const [includeInactive, setIncludeInactive] = useState(true);
  const { state, reload } = useLocations(warehouse.id, { includeInactive, enabled: warehouse.usesLocations });
  const [editing, setEditing] = useState<Location | "new" | null>(null);
  const locations = state.status === "ready" ? state.data : [];

  const columns: TableColumn<Location>[] = [
    { key: "code", header: "Kod", cell: (l) => <span className="font-mono">{l.code}</span> },
    { key: "barcode", header: "Barkod", cell: (l) => <span className="font-mono text-caption">{l.barcode ?? "—"}</span> },
    { key: "seq", header: "Toplama sırası", numeric: true, width: "9rem", cell: (l) => String(l.pickSequence) },
    { key: "status", header: "Durum", width: "7rem", cell: (l) => <Badge tone={l.isActive ? "success" : "neutral"}>{l.isActive ? "Aktif" : "Pasif"}</Badge> },
    ...(canManage
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            width: "7rem",
            cell: (l: Location) => (
              <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} onClick={() => setEditing(l)} aria-label={`${l.code} lokasyonunu düzenle`}>
                Düzenle
              </Button>
            ),
          },
        ]
      : []),
  ];

  return (
    <section aria-labelledby={`${uid}-location-list`} className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <h2 id={`${uid}-location-list`} className="flex items-center gap-2 text-h3 text-fg">
          <MapPin className="size-5" aria-hidden="true" />
          {warehouse.name} — lokasyonlar
        </h2>
        {warehouse.usesLocations ? (
          <div className="flex flex-wrap items-center gap-3">
            <label className="inline-flex min-h-11 items-center gap-2 text-body text-fg">
              <input type="checkbox" className="size-5 accent-(--bos-brand-primary)" checked={includeInactive} onChange={(e) => setIncludeInactive(e.target.checked)} />
              Pasifleri göster
            </label>
            {canManage && warehouse.status === "active" ? (
              <Button icon={<Plus className="size-4" />} onClick={() => setEditing("new")}>
                Yeni lokasyon
              </Button>
            ) : null}
          </div>
        ) : null}
      </div>

      {canManageRole && locationsGate.locked ? <LockedFeature gate={locationsGate} title="Yeni lokasyon eklenemiyor" /> : null}

      {!warehouse.usesLocations ? (
        <div className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            headingLevel="h3"
            icon={<MapPin className="size-6" />}
            title="Bu depo raf/göz takibi kullanmıyor"
            description={
              canManage
                ? "Stok depo düzeyinde tutulur. Raf takibi için depoyu düzenleyip “Raf/göz takibi”ni açın (yalnızca depo boşken)."
                : "Stok depo düzeyinde tutulur."
            }
          />
        </div>
      ) : state.status === "error" ? (
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState headingLevel="h3" icon={<CircleAlert className="size-6" />} title="Lokasyonlar yüklenemedi" description={state.message} action={<Button onClick={reload}>Tekrar dene</Button>} />
        </div>
      ) : (
        <Table
          caption={`${warehouse.name} lokasyonları`}
          columns={columns}
          rows={locations}
          getRowId={(l) => l.id}
          loading={state.status === "loading"}
          loadingRows={3}
          empty={<EmptyState headingLevel="h3" icon={<MapPin className="size-6" />} title="Henüz lokasyon yok" description="Raf ve gözleri toplama sırasıyla ekleyin (ör. A-01-01)." />}
          maxHeight="50vh"
        />
      )}

      {editing ? (
        <LocationDialog
          warehouse={warehouse}
          location={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            reload();
          }}
        />
      ) : null}
    </section>
  );
}

function LocationDialog({ warehouse, location, onClose, onSaved }: { warehouse: Warehouse; location: Location | null; onClose: () => void; onSaved: () => void }) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [values, setValues] = useState<LocationFormValues>(() => (location ? locationToValues(location) : newLocationValues()));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = (patch: Partial<LocationFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validateLocation(values);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    setSaving(true);
    try {
      if (!location) {
        const body = buildCreateLocation(values);
        const created = await createLocation(api, warehouse.id, body, keys.keyFor(body));
        toast({ tone: "success", title: "Lokasyon eklendi", description: created.code });
      } else {
        const patch = buildLocationPatch(location, values);
        if (Object.keys(patch).length === 0) {
          onClose();
          return;
        }
        const updated = await updateLocation(api, location, patch);
        toast({ tone: "success", title: "Lokasyon güncellendi", description: updated.code });
      }
      onSaved();
    } catch (error) {
      const { fields, message } = mutationError(error);
      setErrors(fields);
      setFormError(message);
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      open
      onClose={onClose}
      title={location ? `Lokasyonu düzenle: ${location.code}` : `Yeni lokasyon — ${warehouse.name}`}
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={`${uid}-location-form`} loading={saving}>
            {location ? "Kaydet" : "Lokasyonu ekle"}
          </Button>
        </>
      }
    >
      <form id={`${uid}-location-form`} noValidate onSubmit={submit} className="flex flex-col gap-4">
        {formError ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {formError}
          </FormAlert>
        ) : null}
        <Input label="Lokasyon kodu" required maxLength={LOCATION_CODE_MAX} value={values.code} error={errors.code} hint="Ör. A-01-03 (koridor-raf-göz)." autoComplete="off" onChange={(e) => set({ code: e.target.value })} />
        <Input label="Raf barkodu" maxLength={LOCATION_BARCODE_MAX} value={values.barcode} error={errors.barcode} hint="İsteğe bağlı; rafa yapıştırılan etiket." autoComplete="off" onChange={(e) => set({ barcode: e.target.value })} />
        <Input
          label="Toplama sırası"
          inputMode="numeric"
          value={values.pickSequence}
          error={errors.pickSequence}
          hint="Toplama listesinde küçük numara önce gelir."
          onChange={(e) => set({ pickSequence: e.target.value })}
        />
        {location ? <CheckboxField label="Aktif" checked={values.active} onChange={(v) => set({ active: v })} hint="Pasif lokasyona mal kabul ve düzeltme yapılamaz." /> : null}
      </form>
    </Dialog>
  );
}

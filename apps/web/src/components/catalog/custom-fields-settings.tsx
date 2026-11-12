"use client";

import { useEffect, useId, useState, type FormEvent } from "react";
import Link from "next/link";
import { Archive, ArrowLeft, CircleAlert, ListPlus, Pencil, Plus } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import {
  archiveCustomField,
  createCustomField,
  etagOf,
  listCustomFields,
  updateCustomField,
  type CustomField,
  type EntityType,
} from "@/lib/catalog/api";
import { DATA_TYPES, dataTypeLabels, isDataType, type DataType } from "@/lib/catalog/attributes";
import {
  buildCustomFieldCreate,
  buildCustomFieldPatch,
  customFieldToValues,
  hasOptions,
  hasUnit,
  keyFromLabel,
  newCustomFieldValues,
  type CustomFieldFormValues,
} from "@/lib/catalog/custom-field-form";
import { CATALOG_CONFLICTS, catalogErrorMessage, isVersionConflict, partitionErrors, serverFieldErrors } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { catalogPermissions } from "@/lib/catalog/permissions";
import { useSession } from "@/lib/session/session-context";
import { FormAlert } from "./form-alert";

const entityLabels: Record<EntityType, string> = { product: "Ürün", variant: "Varyant" };

type LoadState = { key: string; status: "ready"; fields: CustomField[] } | { key: string; status: "error"; message: string };
type Editing = { mode: "create" } | { mode: "edit"; field: CustomField };

/** Ayarlar → Özel alanlar (PRD CAT-05): custom field definitions for products and variants. */
export function CustomFieldsSettings() {
  const { api, access } = useSession();
  const canManage = catalogPermissions(access).manageFields;
  const [entity, setEntity] = useState<"" | EntityType>("");
  const [includeArchived, setIncludeArchived] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [data, setData] = useState<LoadState | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [archiving, setArchiving] = useState<CustomField | null>(null);
  const loadKey = `${includeArchived}:${attempt}`;

  useEffect(() => {
    const controller = new AbortController();
    listCustomFields(api, { includeArchived, signal: controller.signal })
      .then((fields) => {
        if (!controller.signal.aborted) setData({ key: loadKey, status: "ready", fields });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setData({ key: loadKey, status: "error", message: catalogErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, includeArchived, loadKey]);

  const current = data?.key === loadKey ? data : null;
  const reload = () => setAttempt((n) => n + 1);
  const rows =
    current?.status === "ready"
      ? current.fields
          .filter((f) => !entity || f.entityType === entity)
          .sort(
            (a, b) =>
              a.entityType.localeCompare(b.entityType) ||
              Number(a.sortOrder) - Number(b.sortOrder) ||
              a.label.localeCompare(b.label, "tr"),
          )
      : [];

  const columns: TableColumn<CustomField>[] = [
    { key: "label", header: "Alan", cell: (f) => <span className="font-medium">{f.label}</span> },
    { key: "key", header: "Anahtar", cell: (f) => <code className="text-caption">{f.key}</code> },
    { key: "entity", header: "Kayıt", cell: (f) => entityLabels[f.entityType as EntityType] ?? f.entityType },
    {
      key: "type",
      header: "Tür",
      cell: (f) => (
        <span>
          {isDataType(f.dataType) ? dataTypeLabels[f.dataType] : f.dataType}
          {f.unit ? <span className="text-fg-muted"> · {f.unit}</span> : null}
          {f.options.length ? <span className="block text-caption text-fg-muted">{f.options.join(", ")}</span> : null}
        </span>
      ),
    },
    {
      key: "flags",
      header: "Özellikler",
      cell: (f) => (
        <span className="flex flex-wrap gap-1">
          {f.required ? <Badge tone="info">Zorunlu</Badge> : null}
          {f.filterable ? <Badge tone="teal">Filtrelenebilir</Badge> : null}
          {f.isArchived ? <Badge tone="warning">Arşivlenmiş</Badge> : null}
          {!f.required && !f.filterable && !f.isArchived ? "—" : null}
        </span>
      ),
    },
    { key: "sort", header: "Sıra", numeric: true, width: "4rem", cell: (f) => String(f.sortOrder) },
    ...(canManage
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            cell: (f: CustomField) =>
              f.isArchived ? null : (
                <div className="flex justify-end gap-1">
                  <Button
                    variant="ghost"
                    size="sm"
                    icon={<Pencil className="size-4" />}
                    aria-label={`${f.label} alanını düzenle`}
                    onClick={() => setEditing({ mode: "edit", field: f })}
                  >
                    Düzenle
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    icon={<Archive className="size-4" />}
                    aria-label={`${f.label} alanını arşivle`}
                    onClick={() => setArchiving(f)}
                  >
                    Arşivle
                  </Button>
                </div>
              ),
          },
        ]
      : []),
  ];

  return (
    <>
      <Link href="/ayarlar" className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
        <ArrowLeft className="size-4" aria-hidden="true" />
        Ayarlar
      </Link>
      <PageHeader
        title="Özel alanlar"
        description="Ürün ve varyantlarınıza işletmenize özel bilgiler ekleyin (ör. renk, ölçü, malzeme, garanti süresi)."
        actions={
          canManage ? (
            <Button icon={<Plus className="size-4" />} onClick={() => setEditing({ mode: "create" })}>
              Yeni alan
            </Button>
          ) : null
        }
      />
      {!canManage ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Özel alanları yalnızca işletme sahibi ve yöneticiler değiştirebilir.
        </FormAlert>
      ) : null}

      <div className="flex flex-wrap items-end gap-4">
        <Select
          label="Kayıt türü"
          className="w-48"
          options={[
            { value: "", label: "Tümü" },
            { value: "product", label: "Ürün" },
            { value: "variant", label: "Varyant" },
          ]}
          value={entity}
          onChange={(e) => setEntity(e.target.value as "" | EntityType)}
        />
        <label className="inline-flex h-9 items-center gap-2 text-body text-fg">
          <input
            type="checkbox"
            className="size-4 accent-(--bos-brand-primary)"
            checked={includeArchived}
            onChange={(e) => setIncludeArchived(e.target.checked)}
          />
          Arşivlenenleri göster
        </label>
      </div>

      {current?.status === "error" ? (
        <section role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            headingLevel="h2"
            icon={<CircleAlert className="size-6" />}
            title="Özel alanlar yüklenemedi"
            description={current.message}
            action={<Button onClick={reload}>Tekrar dene</Button>}
          />
        </section>
      ) : (
        <Table
          caption="Özel alanlar"
          columns={columns}
          rows={rows}
          getRowId={(f) => f.id}
          loading={!current}
          density="compact"
          empty={
            <EmptyState
              headingLevel="h2"
              icon={<ListPlus className="size-6" />}
              title="Henüz özel alan yok"
              description={
                canManage
                  ? "Ürünlerde tutmak istediğiniz ek bilgiler için alan ekleyin."
                  : "Bu işletmede tanımlı özel alan bulunmuyor."
              }
            />
          }
        />
      )}

      {editing ? (
        <CustomFieldDialog
          key={editing.mode === "edit" ? editing.field.id : "new"}
          editing={editing}
          defaultEntity={entity || "product"}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            reload();
          }}
        />
      ) : null}
      {archiving ? (
        <ArchiveFieldDialog
          field={archiving}
          onClose={() => setArchiving(null)}
          onDone={() => {
            setArchiving(null);
            reload();
          }}
        />
      ) : null}
    </>
  );
}

function CustomFieldDialog({
  editing,
  defaultEntity,
  onClose,
  onSaved,
}: {
  editing: Editing;
  defaultEntity: EntityType;
  onClose: () => void;
  onSaved: () => void;
}) {
  const { api } = useSession();
  const { toast } = useToast();
  const original = editing.mode === "edit" ? editing.field : null;
  const [values, setValues] = useState<CustomFieldFormValues>(() =>
    original ? customFieldToValues(original) : newCustomFieldValues(defaultEntity),
  );
  const [keyEdited, setKeyEdited] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = <K extends keyof CustomFieldFormValues>(key: K, value: CustomFieldFormValues[K]) => setValues((v) => ({ ...v, [key]: value }));
  const known = new Set(["entityType", "key", "label", "dataType", "unit", "options", "required", "filterable", "sortOrder"]);
  const id = (name: string) => `cf-${name}`;

  const show = (all: Record<string, string>, extra: string[] = []) => {
    const { onFields, unmatched } = partitionErrors(all, known);
    setErrors(onFields);
    setSummary([...unmatched, ...extra]);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    try {
      if (!original) {
        const { body, errors: clientErrors } = buildCustomFieldCreate(values);
        if (Object.keys(clientErrors).length) return show(clientErrors);
        setSaving(true);
        await createCustomField(api, body, keys.keyFor(body));
        keys.reset();
        toast({ tone: "success", title: "Özel alan eklendi", description: body.label ?? undefined });
      } else {
        const { patch, errors: clientErrors } = buildCustomFieldPatch(values, original);
        if (Object.keys(clientErrors).length) return show(clientErrors);
        if (Object.keys(patch).length === 0) return onClose();
        setSaving(true);
        await updateCustomField(api, original.id, patch, etagOf(original.version));
        toast({ tone: "success", title: "Özel alan kaydedildi", description: values.label });
      }
      onSaved();
    } catch (error) {
      setSaving(false);
      if (isVersionConflict(error)) {
        return show({}, ["Bu alan siz düzenlerken başka biri tarafından değiştirildi. Pencereyi kapatıp listeyi yeniden yükleyin."]);
      }
      if (error instanceof ApiError && error.status === 422 && Object.keys(error.fieldErrors).length) {
        return show(serverFieldErrors(error));
      }
      if (error instanceof ApiError && error.code === "catalog.field_key_taken") return show({ key: CATALOG_CONFLICTS[error.code] });
      show({}, [catalogErrorMessage(error)]);
    }
  };

  const formId = `${useId()}-custom-field-form`;
  return (
    <Dialog
      open
      onClose={() => !saving && onClose()}
      title={original ? `Alanı düzenle: ${original.label}` : "Yeni özel alan"}
      size="lg"
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={formId} loading={saving}>
            {original ? "Kaydet" : "Alanı ekle"}
          </Button>
        </>
      }
    >
      <form id={formId} noValidate onSubmit={submit} className="flex flex-col gap-3">
        {summary.length > 0 || Object.keys(errors).length > 0 ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {summary.length ? summary.map((m) => <p key={m}>{m}</p>) : <p>İşaretli alanları düzeltip tekrar deneyin.</p>}
          </FormAlert>
        ) : null}
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Input
            id={id("label")}
            label="Alan adı"
            required
            value={values.label}
            maxLength={100}
            onChange={(e) => {
              const label = e.target.value;
              setValues((v) => ({ ...v, label, key: !original && !keyEdited ? keyFromLabel(label) : v.key }));
            }}
            error={errors.label}
            autoComplete="off"
          />
          <Input
            id={id("key")}
            label="Anahtar"
            required
            value={values.key}
            maxLength={50}
            readOnly={!!original}
            onChange={(e) => {
              setKeyEdited(true);
              set("key", e.target.value);
            }}
            error={errors.key}
            hint={original ? "Anahtar oluşturulduktan sonra değişmez." : "Filtrelerde ve içe aktarmada kullanılır: küçük harf, rakam, alt çizgi."}
            autoComplete="off"
            spellCheck={false}
          />
          <Select
            id={id("entityType")}
            label="Kayıt türü"
            required
            disabled={!!original}
            options={[
              { value: "product", label: "Ürün" },
              { value: "variant", label: "Varyant" },
            ]}
            value={values.entityType}
            onChange={(e) => set("entityType", e.target.value as EntityType)}
            error={errors.entityType}
          />
          <Select
            id={id("dataType")}
            label="Veri türü"
            required
            disabled={!!original}
            options={DATA_TYPES.map((t) => ({ value: t, label: dataTypeLabels[t] }))}
            value={values.dataType}
            onChange={(e) => set("dataType", e.target.value as DataType)}
            error={errors.dataType}
          />
          {hasUnit(values.dataType) ? (
            <Input
              id={id("unit")}
              label="Birim"
              value={values.unit}
              maxLength={20}
              onChange={(e) => set("unit", e.target.value)}
              error={errors.unit}
              hint="İsteğe bağlı, ör. cm, kg, ay"
              autoComplete="off"
            />
          ) : null}
          <Input
            id={id("sortOrder")}
            label="Sıra"
            inputMode="numeric"
            value={values.sortOrder}
            onChange={(e) => set("sortOrder", e.target.value)}
            error={errors.sortOrder}
            hint="Formda küçükten büyüğe sıralanır."
          />
        </div>
        {hasOptions(values.dataType) ? (
          <Textarea
            id={id("options")}
            label="Seçenekler"
            required
            rows={4}
            value={values.options}
            onChange={(e) => set("options", e.target.value)}
            error={errors.options}
            hint="Her satıra bir seçenek yazın."
          />
        ) : null}
        <div className="flex flex-col gap-2">
          <label className="inline-flex items-center gap-2 text-body text-fg">
            <input
              type="checkbox"
              className="size-4 accent-(--bos-brand-primary)"
              checked={values.required}
              onChange={(e) => set("required", e.target.checked)}
            />
            Zorunlu alan
          </label>
          <label className="inline-flex items-center gap-2 text-body text-fg">
            <input
              type="checkbox"
              className="size-4 accent-(--bos-brand-primary)"
              checked={values.filterable}
              onChange={(e) => set("filterable", e.target.checked)}
            />
            Ürün listesinde filtre olarak kullanılsın
          </label>
        </div>
      </form>
    </Dialog>
  );
}

function ArchiveFieldDialog({ field, onClose, onDone }: { field: CustomField; onClose: () => void; onDone: () => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await archiveCustomField(api, field.id, etagOf(field.version));
      toast({ tone: "success", title: "Özel alan arşivlendi", description: field.label });
      onDone();
    } catch (e) {
      setBusy(false);
      setError(
        isVersionConflict(e)
          ? "Bu alan siz bakarken değiştirildi. Pencereyi kapatıp listeyi yeniden yükleyin."
          : catalogErrorMessage(e),
      );
    }
  };

  return (
    <Dialog
      open
      onClose={() => !busy && onClose()}
      role="alertdialog"
      dismissOnBackdrop={false}
      size="sm"
      title="Özel alanı arşivle"
      description={`"${field.label}" arşivlenecek.`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" onClick={confirm} loading={busy}>
            Arşivle
          </Button>
        </>
      }
    >
      <p>Arşivlenen alan formlarda ve filtrelerde görünmez. Ürünlerde kayıtlı değerler silinmez.</p>
      {error ? (
        <p role="alert" className="mt-3 text-danger-fg">
          {error}
        </p>
      ) : null}
    </Dialog>
  );
}

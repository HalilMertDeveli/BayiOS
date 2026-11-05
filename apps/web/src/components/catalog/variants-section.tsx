"use client";

import { useState, type FormEvent } from "react";
import { Archive, Pencil, Plus, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { addVariant, archiveVariant, etagOf, updateVariant, type CustomField, type Product, type Variant } from "@/lib/catalog/api";
import { formatAttribute } from "@/lib/catalog/attributes";
import { catalogErrorMessage, codeConflictMessage, isVersionConflict, partitionErrors, readCodeConflict, serverFieldErrors } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { buildVariantCreate, buildVariantPatch, emptyVariant, variantToValues, type FormErrors, type VariantFormValues } from "@/lib/catalog/product-form";
import { useSession } from "@/lib/session/session-context";
import { FormAlert } from "./form-alert";
import { ProductStatusBadge } from "./product-status-badge";
import { VariantFields, variantErrorKeys } from "./variant-fields";

/** Error keys in the dialog get this prefix so their DOM ids never clash with the product form's. */
const PREFIX = "variant.";
const prefixed = (errors: FormErrors): FormErrors => Object.fromEntries(Object.entries(errors).map(([k, v]) => [PREFIX + k, v]));

type Editing = { mode: "create" } | { mode: "edit"; variant: Variant };

/** Variants of an existing product: each one is its own resource (POST/PATCH/DELETE with its own version). */
export function VariantsSection({
  product,
  variantFields,
  readOnly,
  onChanged,
  onConflict,
}: {
  product: Product;
  variantFields: readonly CustomField[];
  readOnly: boolean;
  /** Reloads the product (fresh variant versions). */
  onChanged: () => void;
  /** 412 inside a dialog: reload everything. */
  onConflict: () => void;
}) {
  const [editing, setEditing] = useState<Editing | null>(null);
  const [archiving, setArchiving] = useState<Variant | null>(null);
  const productArchived = product.status === "archived";

  const columns: TableColumn<Variant>[] = [
    { key: "sku", header: "SKU", cell: (v) => <span className="font-mono">{v.sku}</span> },
    { key: "barcode", header: "Barkod", cell: (v) => <span className="font-mono">{v.barcode ?? "—"}</span> },
    { key: "name", header: "Ad", cell: (v) => v.name ?? "—" },
    ...(variantFields.length > 0
      ? [
          {
            key: "attributes",
            header: "Özel alanlar",
            cell: (v: Variant) => {
              const attrs = v.attributes;
              const parts = variantFields
                .map((f) => [f.label, formatAttribute(f, attrs?.[f.key])] as const)
                .filter(([, text]) => text);
              return parts.length ? <span className="text-caption">{parts.map(([l, t]) => `${l}: ${t}`).join(" · ")}</span> : "—";
            },
          },
        ]
      : []),
    { key: "status", header: "Durum", cell: (v) => <ProductStatusBadge status={v.status} /> },
    {
      key: "actions",
      header: <span className="sr-only">İşlemler</span>,
      cell: (v) => (
        <div className="flex justify-end gap-1">
          <Button
            variant="ghost"
            size="sm"
            icon={<Pencil className="size-4" />}
            onClick={() => setEditing({ mode: "edit", variant: v })}
            aria-label={`${v.sku} varyantını ${readOnly ? "görüntüle" : "düzenle"}`}
          >
            {readOnly ? "Görüntüle" : "Düzenle"}
          </Button>
          {!readOnly && v.status !== "archived" ? (
            <Button
              variant="ghost"
              size="sm"
              icon={<Archive className="size-4" />}
              onClick={() => setArchiving(v)}
              aria-label={`${v.sku} varyantını arşivle`}
            >
              Arşivle
            </Button>
          ) : null}
        </div>
      ),
    },
  ];

  return (
    <section aria-labelledby="sec-variants" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="sec-variants" className="text-h3 text-fg">
          Varyantlar ({product.variants.length})
        </h2>
        {!readOnly && !productArchived ? (
          <Button variant="secondary" size="sm" icon={<Plus className="size-4" />} onClick={() => setEditing({ mode: "create" })}>
            Varyant ekle
          </Button>
        ) : null}
      </div>
      <Table caption="Varyantlar" columns={columns} rows={product.variants} getRowId={(v) => v.id} density="compact" />

      {editing ? (
        <VariantDialog
          key={editing.mode === "edit" ? editing.variant.id : "new"}
          product={product}
          editing={editing}
          variantFields={variantFields}
          readOnly={readOnly}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            onChanged();
          }}
          onConflict={() => {
            setEditing(null);
            onConflict();
          }}
        />
      ) : null}
      {archiving ? (
        <ArchiveVariantDialog
          variant={archiving}
          onClose={() => setArchiving(null)}
          onDone={() => {
            setArchiving(null);
            onChanged();
          }}
          onConflict={() => {
            setArchiving(null);
            onConflict();
          }}
        />
      ) : null}
    </section>
  );
}

function VariantDialog({
  product,
  editing,
  variantFields,
  readOnly,
  onClose,
  onSaved,
  onConflict,
}: {
  product: Product;
  editing: Editing;
  variantFields: readonly CustomField[];
  readOnly: boolean;
  onClose: () => void;
  onSaved: () => void;
  onConflict: () => void;
}) {
  const { api } = useSession();
  const { toast } = useToast();
  const original = editing.mode === "edit" ? editing.variant : null;
  const [values, setValues] = useState<VariantFormValues>(() =>
    original ? variantToValues(original, variantFields) : emptyVariant(variantFields),
  );
  const [errors, setErrors] = useState<FormErrors>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const known = new Set(variantErrorKeys(PREFIX, variantFields));

  const show = (all: FormErrors, extra: string[] = []) => {
    const { onFields, unmatched } = partitionErrors(all, known);
    setErrors(onFields);
    setSummary([...unmatched, ...extra]);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (readOnly || saving) return;
    try {
      if (!original) {
        const { body, errors: clientErrors } = buildVariantCreate(values, variantFields);
        if (Object.keys(clientErrors).length) return show(prefixed(clientErrors));
        setSaving(true);
        await addVariant(api, product.id, body, keys.keyFor(body));
        keys.reset();
        toast({ tone: "success", title: "Varyant eklendi", description: body.sku ?? undefined });
      } else {
        const { patch, errors: clientErrors } = buildVariantPatch(values, original, variantFields);
        if (Object.keys(clientErrors).length) return show(prefixed(clientErrors));
        if (Object.keys(patch).length === 0) return onClose();
        setSaving(true);
        await updateVariant(api, original.id, patch, etagOf(original.version));
        toast({ tone: "success", title: "Varyant kaydedildi", description: values.sku });
      }
      onSaved();
    } catch (error) {
      setSaving(false);
      if (isVersionConflict(error)) return setConflict(true);
      if (error instanceof ApiError && error.status === 422 && Object.keys(error.fieldErrors).length) {
        return show(prefixed(serverFieldErrors(error)));
      }
      const taken = readCodeConflict(error);
      if (taken) return show({ [`${PREFIX}${taken.field}`]: codeConflictMessage(taken, product.id) });
      show({}, [catalogErrorMessage(error)]);
    }
  };

  const title = original ? (readOnly ? `Varyant: ${original.sku}` : `Varyantı düzenle: ${original.sku}`) : "Varyant ekle";
  const formId = "variant-dialog-form";

  return (
    <Dialog
      open
      onClose={() => !saving && onClose()}
      title={title}
      size="lg"
      dismissOnBackdrop={false}
      footer={
        readOnly ? (
          <Button variant="secondary" onClick={onClose}>
            Kapat
          </Button>
        ) : (
          <>
            <Button variant="secondary" onClick={onClose} disabled={saving}>
              Vazgeç
            </Button>
            <Button type="submit" form={formId} loading={saving}>
              {original ? "Kaydet" : "Varyantı ekle"}
            </Button>
          </>
        )
      }
    >
      <form id={formId} noValidate onSubmit={submit} className="flex flex-col gap-3">
        {conflict ? (
          <FormAlert
            tone="warning"
            title="Kayıt başkası tarafından değiştirildi"
            actions={
              <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={onConflict}>
                Yeniden yükle
              </Button>
            }
          >
            Bu varyant siz düzenlerken güncellendi; değişiklikleriniz kaydedilmedi. Ürünü yeniden yükleyip tekrar deneyin.
          </FormAlert>
        ) : null}
        {summary.length > 0 || Object.keys(errors).length > 0 ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {summary.length ? summary.map((m) => <p key={m}>{m}</p>) : <p>İşaretli alanları düzeltip tekrar deneyin.</p>}
          </FormAlert>
        ) : null}
        <fieldset disabled={readOnly}>
          <VariantFields
            values={values}
            onChange={setValues}
            variantFields={variantFields}
            errors={errors}
            prefix={PREFIX}
            showStatus={original?.status !== "archived"}
          />
        </fieldset>
      </form>
    </Dialog>
  );
}

function ArchiveVariantDialog({
  variant,
  onClose,
  onDone,
  onConflict,
}: {
  variant: Variant;
  onClose: () => void;
  onDone: () => void;
  onConflict: () => void;
}) {
  const { api } = useSession();
  const { toast } = useToast();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await archiveVariant(api, variant.id, etagOf(variant.version));
      toast({ tone: "success", title: "Varyant arşivlendi", description: variant.sku });
      onDone();
    } catch (e) {
      setBusy(false);
      if (isVersionConflict(e)) return onConflict();
      setError(catalogErrorMessage(e));
    }
  };

  return (
    <Dialog
      open
      onClose={() => !busy && onClose()}
      role="alertdialog"
      dismissOnBackdrop={false}
      size="sm"
      title="Varyantı arşivle"
      description={`${variant.sku} arşivlenecek.`}
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
      <p>Arşivlenen varyant yeni siparişlerde kullanılamaz; geçmiş kayıtlar korunur.</p>
      {error ? (
        <p role="alert" className="mt-3 text-danger-fg">
          {error}
        </p>
      ) : null}
    </Dialog>
  );
}

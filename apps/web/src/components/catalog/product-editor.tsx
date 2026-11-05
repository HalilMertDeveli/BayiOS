"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Archive, ArrowLeft, CircleAlert, LoaderCircle, Lock, Plus, RotateCcw, SearchX, Trash2 } from "lucide-react";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select, type SelectOption } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { archiveProduct, createProduct, getProduct, productHref, updateProduct, type Product } from "@/lib/catalog/api";
import { fieldsFor } from "@/lib/catalog/attributes";
import {
  CATALOG_CONFLICTS,
  catalogErrorMessage,
  codeConflictMessage,
  conflictRowIndex,
  fieldId,
  isVersionConflict,
  partitionErrors,
  readCodeConflict,
  serverFieldErrors,
} from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { categoryOptions } from "@/lib/catalog/list-query";
import { catalogPermissions } from "@/lib/catalog/permissions";
import {
  DESCRIPTION_MAX,
  NAME_MAX,
  buildCreateRequest,
  buildProductPatch,
  emptyVariant,
  newProductValues,
  productToValues,
  type EditableStatus,
  type FormErrors,
  type ProductFormValues,
} from "@/lib/catalog/product-form";
import { useSession } from "@/lib/session/session-context";
import { useResetAfterCreate } from "@/lib/use-reset-after-create";
import { AttributeInput } from "./attribute-input";
import { FormAlert } from "./form-alert";
import { ProductStatusBadge } from "./product-status-badge";
import { useCatalogReference, type CatalogReference } from "./use-catalog-reference";
import { VariantFields, variantErrorKeys } from "./variant-fields";
import { VariantsSection } from "./variants-section";

type Loaded = { product: Product; etag: string };
type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | ({ status: "ready" } & Loaded);

/** /urunler/yeni (no productId) and /urunler/[id]. */
export function ProductEditor({ productId }: { productId?: string }) {
  const { api, access } = useSession();
  const { state: reference, reload: reloadReference } = useCatalogReference();
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const { generation, markCreated } = useResetAfterCreate();

  useEffect(() => {
    if (!productId) return;
    const controller = new AbortController();
    getProduct(api, productId, controller.signal)
      .then((loaded) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", ...loaded });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        const notFound = error instanceof ApiError && error.status === 404;
        setLoad({ status: "error", message: catalogErrorMessage(error), notFound });
      });
    return () => controller.abort();
  }, [api, productId, attempt]);

  const reloadProduct = useCallback(() => setAttempt((n) => n + 1), []);
  const onSaved = useCallback((loaded: Loaded) => setLoad({ status: "ready", ...loaded }), []);

  const title = productId ? "Ürün" : "Yeni ürün";

  if (!productId && !catalogPermissions(access).write) {
    return (
      <>
        <BackLink />
        <PageHeader title={title} />
        <section className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="Ürün ekleme yetkiniz yok"
            description="Rolünüz ürünleri yalnızca görüntülemeye izin veriyor. Yeni ürün için işletme yöneticinizle görüşün."
            action={
              <Link href="/urunler" className={buttonClasses("secondary")}>
                Ürün listesine dön
              </Link>
            }
          />
        </section>
      </>
    );
  }

  if (reference.status === "error" || (productId && load.status === "error")) {
    const notFound = productId && load.status === "error" && load.notFound;
    return (
      <>
        <BackLink />
        <PageHeader title={title} />
        <section role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={notFound ? <SearchX className="size-6" /> : <CircleAlert className="size-6" />}
            title={notFound ? "Ürün bulunamadı" : "Ürün bilgileri yüklenemedi"}
            description={
              notFound
                ? "Ürün silinmiş, bağlantı hatalı ya da bu işletmeye ait değil."
                : reference.status === "error"
                  ? reference.message
                  : load.status === "error"
                    ? load.message
                    : null
            }
            action={
              notFound ? (
                <Link href="/urunler" className={buttonClasses("secondary")}>
                  Ürün listesine dön
                </Link>
              ) : (
                <Button
                  onClick={() => {
                    if (reference.status === "error") reloadReference();
                    if (productId) {
                      setLoad({ status: "loading" });
                      reloadProduct();
                    }
                  }}
                >
                  Tekrar dene
                </Button>
              )
            }
          />
        </section>
      </>
    );
  }

  if (reference.status === "loading" || (productId && load.status !== "ready")) {
    return (
      <>
        <BackLink />
        <PageHeader title={title} />
        <p role="status" className="flex items-center gap-2 text-body text-fg-muted">
          <LoaderCircle className="size-4 animate-spin" aria-hidden="true" />
          Ürün bilgileri yükleniyor…
        </p>
      </>
    );
  }

  const loaded = productId && load.status === "ready" ? { product: load.product, etag: load.etag } : null;
  return (
    <ProductForm
      // Remount with fresh values whenever a different version of the product is loaded.
      key={loaded ? `${loaded.product.id}:${loaded.product.version}` : `new-${generation}`}
      reference={reference}
      loaded={loaded}
      onSaved={onSaved}
      onReload={() => {
        setLoad({ status: "loading" });
        reloadProduct();
      }}
      onVariantsChanged={reloadProduct}
      onCreated={markCreated}
    />
  );
}

function BackLink() {
  return (
    <Link href="/urunler" className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Ürünler
    </Link>
  );
}

/** Adds the current value when it is not among the (non-archived) options, so an archived reference stays visible. */
function withCurrent(options: SelectOption[], value: string, label = "Arşivlenmiş kayıt"): SelectOption[] {
  return value && !options.some((o) => o.value === value) ? [...options, { value, label }] : options;
}

type ProductFormProps = {
  reference: CatalogReference;
  loaded: Loaded | null;
  onSaved: (loaded: Loaded) => void;
  onReload: () => void;
  onVariantsChanged: () => void;
  /** A new product was created: the page starts the next visit from an empty form (#96). */
  onCreated: () => void;
};

function ProductForm({ reference, loaded, onSaved, onReload, onVariantsChanged, onCreated }: ProductFormProps) {
  const { api, access } = useSession();
  const permissions = catalogPermissions(access);
  const readOnly = !permissions.write;
  const router = useRouter();
  const { toast } = useToast();
  const product = loaded?.product ?? null;
  const archived = product?.status === "archived";

  const productFields = useMemo(() => fieldsFor(reference.fields, "product"), [reference.fields]);
  const variantFields = useMemo(() => fieldsFor(reference.fields, "variant"), [reference.fields]);

  const [values, setValues] = useState<ProductFormValues>(() => {
    if (product) return productToValues(product, productFields);
    const unit = reference.units.find((u) => u.code.toUpperCase() === "ADET") ?? (reference.units.length === 1 ? reference.units[0] : undefined);
    return newProductValues(productFields, variantFields, { defaultUnitId: unit?.id });
  });
  const [errors, setErrors] = useState<FormErrors>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const [archiveOpen, setArchiveOpen] = useState(false);
  const [archiving, setArchiving] = useState(false);
  const [archiveError, setArchiveError] = useState<string | null>(null);
  const [focusToken, setFocusToken] = useState(0);
  const [keys] = useState(() => new IdempotencyKeys());
  const formRef = useRef<HTMLFormElement>(null);
  const summaryRef = useRef<HTMLDivElement>(null);

  // After a failed submit: focus the first invalid input, else the error summary.
  useEffect(() => {
    if (focusToken === 0) return;
    const invalid = formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]');
    (invalid ?? summaryRef.current)?.focus();
  }, [focusToken]);

  const set = <K extends keyof ProductFormValues>(key: K, value: ProductFormValues[K]) => setValues((v) => ({ ...v, [key]: value }));

  const knownKeys = useMemo(() => {
    const keys = ["name", "description", "categoryId", "brandId", "defaultUnitId", "taxRate", "status"];
    keys.push(...productFields.map((f) => `attributes.${f.key}`));
    if (!product) values.variants.forEach((_, i) => keys.push(...variantErrorKeys(`variants[${i}].`, variantFields)));
    return new Set(keys);
  }, [productFields, variantFields, product, values.variants]);

  const showErrors = (all: FormErrors, extra: string[] = []) => {
    const { onFields, unmatched } = partitionErrors(all, knownKeys);
    setErrors(onFields);
    setSummary([...unmatched, ...extra]);
    setFocusToken((n) => n + 1);
  };

  const handleFailure = (error: unknown) => {
    if (isVersionConflict(error)) {
      setConflict(true);
      setErrors({});
      setSummary([]);
      return;
    }
    if (error instanceof ApiError && error.status === 422 && Object.keys(error.fieldErrors).length > 0) {
      showErrors(serverFieldErrors(error));
      return;
    }
    const taken = readCodeConflict(error);
    if (taken) {
      // 409 conflict detail names the taken value: pin the message to the variant row that holds it.
      const message = codeConflictMessage(taken, product?.id ?? null);
      const row = product ? -1 : conflictRowIndex(values.variants, taken);
      if (row >= 0) showErrors({ [`variants[${row}].${taken.field}`]: message });
      else showErrors({}, [message]);
      return;
    }
    if (error instanceof ApiError && error.code && CATALOG_CONFLICTS[error.code]) {
      showErrors({}, [CATALOG_CONFLICTS[error.code]]);
      return;
    }
    showErrors({}, [catalogErrorMessage(error)]);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (readOnly || saving) return;
    setConflict(false);

    if (!product) {
      const { body, errors: clientErrors } = buildCreateRequest(values, productFields, variantFields);
      if (Object.keys(clientErrors).length > 0) return showErrors(clientErrors);
      setSaving(true);
      try {
        const created = await createProduct(api, body, keys.keyFor(body));
        keys.reset();
        onCreated();
        toast({ tone: "success", title: "Ürün oluşturuldu", description: created.name });
        router.replace(productHref(created.id));
      } catch (error) {
        handleFailure(error);
        setSaving(false);
      }
      return;
    }

    const { patch, errors: clientErrors } = buildProductPatch(values, product, productFields);
    if (Object.keys(clientErrors).length > 0) return showErrors(clientErrors);
    if (Object.keys(patch).length === 0) {
      setErrors({});
      setSummary([]);
      toast({ tone: "info", title: "Kaydedilecek değişiklik yok" });
      return;
    }
    setSaving(true);
    try {
      const saved = await updateProduct(api, product.id, patch, loaded!.etag);
      toast({ tone: "success", title: "Ürün kaydedildi" });
      setSaving(false);
      onSaved(saved);
    } catch (error) {
      setSaving(false);
      handleFailure(error);
    }
  };

  const restore = async () => {
    if (!product || !loaded) return;
    setSaving(true);
    try {
      const saved = await updateProduct(api, product.id, { status: "active" }, loaded.etag);
      toast({ tone: "success", title: "Ürün geri alındı", description: "Ürün yeniden aktif." });
      setSaving(false);
      onSaved(saved);
    } catch (error) {
      setSaving(false);
      handleFailure(error);
    }
  };

  const confirmArchive = async () => {
    if (!product || !loaded) return;
    setArchiving(true);
    setArchiveError(null);
    try {
      await archiveProduct(api, product.id, loaded.etag);
      toast({ tone: "success", title: "Ürün arşivlendi", description: product.name });
      router.push("/urunler");
    } catch (error) {
      setArchiving(false);
      if (isVersionConflict(error)) {
        setArchiveOpen(false);
        setConflict(true);
        return;
      }
      setArchiveError(catalogErrorMessage(error));
    }
  };

  const unitOptions = withCurrent(
    reference.units.map((u) => ({ value: u.id, label: `${u.name} (${u.code})` })),
    values.defaultUnitId,
  );
  const brandOptions = withCurrent(
    [...reference.brands].sort((a, b) => a.name.localeCompare(b.name, "tr")).map((b) => ({ value: b.id, label: b.name })),
    values.brandId,
  );
  const catOptions = withCurrent(categoryOptions(reference.categories), values.categoryId);

  const pageTitle = product ? product.name : "Yeni ürün";
  const errorCount = Object.keys(errors).length + summary.length;

  return (
    <>
      <BackLink />
      <PageHeader
        title={pageTitle}
        description={
          product ? (
            <span className="inline-flex flex-wrap items-center gap-2">
              <ProductStatusBadge status={product.status} />
              <span>Son güncelleme: {new Date(product.updatedAt ?? product.createdAt).toLocaleString("tr-TR")}</span>
            </span>
          ) : (
            "Zorunlu alanlar * ile işaretlidir."
          )
        }
        actions={
          product && !readOnly ? (
            archived ? (
              <Button variant="secondary" icon={<RotateCcw className="size-4" />} onClick={restore} loading={saving}>
                Arşivden çıkar
              </Button>
            ) : (
              <Button variant="secondary" icon={<Archive className="size-4" />} onClick={() => setArchiveOpen(true)}>
                Arşivle
              </Button>
            )
          ) : null
        }
      />

      {readOnly ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Rolünüz ürünleri düzenlemeye izin vermiyor. Değişiklik için işletme yöneticinizle görüşün.
        </FormAlert>
      ) : null}
      {archived && !readOnly ? (
        <FormAlert tone="warning" title="Bu ürün arşivlenmiş">
          Arşivlenmiş ürün satışta ve listelerde görünmez. Düzenlemeye devam edebilir veya arşivden çıkarabilirsiniz.
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
          Bu ürün siz düzenlerken başka biri tarafından güncellendi; değişiklikleriniz kaydedilmedi. Güncel hâlini yeniden
          yükleyin, ardından değişikliklerinizi tekrar yapın.
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

      <form ref={formRef} noValidate onSubmit={submit} aria-label={product ? "Ürünü düzenle" : "Yeni ürün"} className="flex flex-col gap-4">
        <fieldset disabled={readOnly} className="flex flex-col gap-4">
          <section aria-labelledby="sec-core" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
            <h2 id="sec-core" className="text-h3 text-fg">
              Temel bilgiler
            </h2>
            <Input
              id={fieldId("name")}
              label="Ürün adı"
              required
              maxLength={NAME_MAX}
              value={values.name}
              onChange={(e) => set("name", e.target.value)}
              error={errors.name}
              autoComplete="off"
            />
            <Textarea
              id={fieldId("description")}
              label="Açıklama"
              maxLength={DESCRIPTION_MAX}
              value={values.description}
              onChange={(e) => set("description", e.target.value)}
              error={errors.description}
            />
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              <Select
                id={fieldId("categoryId")}
                label="Kategori"
                placeholder="Kategorisiz"
                options={catOptions}
                value={values.categoryId}
                onChange={(e) => set("categoryId", e.target.value)}
                error={errors.categoryId}
              />
              <Select
                id={fieldId("brandId")}
                label="Marka"
                placeholder="Markasız"
                options={brandOptions}
                value={values.brandId}
                onChange={(e) => set("brandId", e.target.value)}
                error={errors.brandId}
              />
              <Select
                id={fieldId("defaultUnitId")}
                label="Birim"
                required
                placeholder="Seçiniz"
                options={unitOptions}
                value={values.defaultUnitId}
                onChange={(e) => set("defaultUnitId", e.target.value)}
                error={errors.defaultUnitId}
                hint={reference.units.length === 0 ? "Tanımlı birim yok. Önce Ayarlar'dan birim ekleyin." : undefined}
              />
              <Input
                id={fieldId("taxRate")}
                label="KDV oranı"
                required
                inputMode="decimal"
                prefix={<span className="text-caption">%</span>}
                value={values.taxRate}
                onChange={(e) => set("taxRate", e.target.value)}
                error={errors.taxRate}
                autoComplete="off"
              />
              {archived ? null : (
                <Select
                  id={fieldId("status")}
                  label="Durum"
                  options={[
                    { value: "active", label: "Aktif" },
                    { value: "inactive", label: "Pasif" },
                  ]}
                  value={values.status}
                  onChange={(e) => set("status", e.target.value as EditableStatus)}
                  error={errors.status}
                  hint="Pasif ürün listede kalır ama satışa kapalıdır."
                />
              )}
            </div>
          </section>

          {productFields.length > 0 ? (
            <section aria-labelledby="sec-attributes" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
              <div className="flex flex-col gap-1">
                <h2 id="sec-attributes" className="text-h3 text-fg">
                  Özel alanlar
                </h2>
                <p className="text-caption text-fg-muted">İşletmenize özel ürün bilgileri.</p>
              </div>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
                {productFields.map((field) => {
                  const key = `attributes.${field.key}`;
                  return (
                    <AttributeInput
                      key={field.key}
                      field={field}
                      value={values.attributes[field.key]}
                      onChange={(value) => set("attributes", { ...values.attributes, [field.key]: value })}
                      errorKey={key}
                      error={errors[key]}
                    />
                  );
                })}
              </div>
            </section>
          ) : null}

          {!product ? (
            <section aria-labelledby="sec-variants" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
              <div className="flex flex-col gap-1">
                <h2 id="sec-variants" className="text-h3 text-fg">
                  Varyantlar
                </h2>
                <p className="text-caption text-fg-muted">
                  Tek çeşit ürün için bir varyant yeterlidir. Renk, beden gibi çeşitleri ayrı SKU ile ekleyin.
                </p>
              </div>
              {values.variants.map((variant, index) => (
                <fieldset key={index} className="flex flex-col gap-3 rounded-md border border-line p-3 sm:p-4">
                  <legend className="px-1 text-body font-semibold text-fg">Varyant {index + 1}</legend>
                  <VariantFields
                    values={variant}
                    onChange={(next) => set("variants", values.variants.map((v, i) => (i === index ? next : v)))}
                    variantFields={variantFields}
                    errors={errors}
                    prefix={`variants[${index}].`}
                  />
                  {values.variants.length > 1 ? (
                    <div>
                      <Button
                        variant="ghost"
                        size="sm"
                        icon={<Trash2 className="size-4" />}
                        onClick={() => set("variants", values.variants.filter((_, i) => i !== index))}
                      >
                        Varyant {index + 1}&apos;i kaldır
                      </Button>
                    </div>
                  ) : null}
                </fieldset>
              ))}
              <div>
                <Button
                  variant="secondary"
                  size="sm"
                  icon={<Plus className="size-4" />}
                  onClick={() => set("variants", [...values.variants, emptyVariant(variantFields)])}
                >
                  Varyant ekle
                </Button>
              </div>
            </section>
          ) : null}
        </fieldset>

        {!readOnly ? (
          <div className="sticky bottom-0 z-(--bos-z-sticky) -mx-4 flex flex-wrap justify-end gap-2 border-t border-line bg-page/95 px-4 py-3 backdrop-blur sm:mx-0 sm:rounded-lg sm:border">
            <Link href="/urunler" className={buttonClasses("secondary")}>
              Vazgeç
            </Link>
            <Button type="submit" loading={saving}>
              {product ? "Değişiklikleri kaydet" : "Ürünü oluştur"}
            </Button>
          </div>
        ) : null}
      </form>

      {product ? (
        <VariantsSection product={product} variantFields={variantFields} readOnly={readOnly} onChanged={onVariantsChanged} onConflict={onReload} />
      ) : null}

      <Dialog
        open={archiveOpen}
        onClose={() => {
          if (!archiving) {
            setArchiveOpen(false);
            setArchiveError(null);
          }
        }}
        role="alertdialog"
        dismissOnBackdrop={false}
        size="sm"
        title="Ürünü arşivle"
        description={`"${product?.name ?? ""}" arşivlenecek.`}
        footer={
          <>
            <Button variant="secondary" onClick={() => setArchiveOpen(false)} disabled={archiving}>
              Vazgeç
            </Button>
            <Button variant="danger" onClick={confirmArchive} loading={archiving}>
              Arşivle
            </Button>
          </>
        }
      >
        <p>Arşivlenen ürün listelerde ve yeni siparişlerde görünmez; geçmiş kayıtlar korunur. Daha sonra arşivden çıkarabilirsiniz.</p>
        {archiveError ? (
          <p role="alert" className="mt-3 text-danger-fg">
            {archiveError}
          </p>
        ) : null}
      </Dialog>
    </>
  );
}

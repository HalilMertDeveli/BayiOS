"use client";

import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { Archive, ArrowLeft, CircleAlert, RotateCcw, Star } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { isVersionConflict } from "@/lib/catalog/errors";
import { PRICE_LISTS_HREF, getPriceList, listPriceLists, updatePriceList, type PriceList } from "@/lib/pricing/api";
import { pricingErrorMessage, pricingFieldErrors } from "@/lib/pricing/errors";
import { priceListKindLabels, priceListStatusMeta, vatModeLabel } from "@/lib/pricing/labels";
import { formatPercent } from "@/lib/pricing/money";
import { pricingPermissions } from "@/lib/pricing/permissions";
import { buildPriceListPatch, priceListToValues, validatePriceList, type PriceListFormValues } from "@/lib/pricing/price-list-form";
import { useSession } from "@/lib/session/session-context";
import { PriceItems } from "./price-items";
import { PriceListFields } from "./price-list-fields";
import { validityText } from "./price-list-index";

type Loaded = { priceList: PriceList; etag: string; lists: PriceList[] };
type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | ({ status: "ready" } & Loaded);

function BackLink() {
  return (
    <Link href={PRICE_LISTS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Fiyat listeleri
    </Link>
  );
}

/** /fiyat-listeleri/[id]: list settings (If-Match / 412) and its items (tiers, bulk upsert). */
export function PriceListDetail({ priceListId }: { priceListId: string }) {
  const { api, role } = useSession();
  const canWrite = pricingPermissions(role).write;
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    Promise.all([getPriceList(api, priceListId, controller.signal), listPriceLists(api, { includeArchived: true, signal: controller.signal })])
      .then(([{ priceList, etag }, lists]) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", priceList, etag, lists });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: pricingErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, priceListId, attempt]);

  const reload = useCallback(() => {
    setLoad({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title="Fiyat listesi" />
        <p role="status" className="text-body text-fg-muted">
          Liste yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title="Fiyat listesi" />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Fiyat listesi bulunamadı" : "Fiyat listesi yüklenemedi"}
            description={load.notFound ? "Liste bu işletmede yok ya da bu rolle görüntülenemiyor." : load.message}
            action={load.notFound ? undefined : <Button onClick={reload}>Tekrar dene</Button>}
          />
        </div>
      </>
    );
  }
  return (
    <>
      <SettingsAndItems
      key={`${load.priceList.id}-${load.priceList.version}`}
      loaded={load}
      onSaved={(priceList, etag) => setLoad((prev) => (prev.status === "ready" ? { ...prev, priceList, etag, lists: prev.lists.map((l) => (l.id === priceList.id ? priceList : priceList.isDefault && l.currency === priceList.currency && l.kind === "sales" ? { ...l, isDefault: false } : l)) } : prev))}
      onReload={reload}
      />
      {/* Outside the keyed settings form: a settings save must not drop unsaved item rows. */}
      <PriceItems key={load.priceList.id} priceList={load.priceList} canEdit={canWrite && load.priceList.status !== "archived"} />
    </>
  );
}

function SettingsAndItems({ loaded, onSaved, onReload }: { loaded: Loaded; onSaved: (list: PriceList, etag: string) => void; onReload: () => void }) {
  const { api, role } = useSession();
  const permissions = pricingPermissions(role);
  const { toast } = useToast();
  const list = loaded.priceList;
  const archived = list.status === "archived";
  const canEdit = permissions.write && !archived;
  const [values, setValues] = useState<PriceListFormValues>(() => priceListToValues(list));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const [archiveOpen, setArchiveOpen] = useState(false);
  const [archiveBusy, setArchiveBusy] = useState(false);
  const [archiveError, setArchiveError] = useState<string | null>(null);
  const errorRef = useRef<HTMLDivElement>(null);
  const set = (patch: Partial<PriceListFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const fail = (error: unknown) => {
    if (isVersionConflict(error)) {
      setConflict(true);
      setErrors({});
      setFormError(null);
      return;
    }
    if (error instanceof ApiError && error.status === 422) {
      setErrors(pricingFieldErrors(error));
      setFormError(error.userMessage);
    } else {
      setFormError(pricingErrorMessage(error));
    }
    requestAnimationFrame(() => errorRef.current?.focus());
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!canEdit || saving) return;
    setConflict(false);
    setFormError(null);
    const clientErrors = validatePriceList(values);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    const patch = buildPriceListPatch(list, values);
    if (Object.keys(patch).length === 0) {
      toast({ tone: "info", title: "Değişiklik yok" });
      return;
    }
    setSaving(true);
    try {
      const updated = await updatePriceList(api, list.id, patch, loaded.etag);
      toast({ tone: "success", title: "Liste kaydedildi", description: updated.priceList.name });
      onSaved(updated.priceList, updated.etag);
    } catch (error) {
      fail(error);
    } finally {
      setSaving(false);
    }
  };

  const archive = async () => {
    setArchiveBusy(true);
    setArchiveError(null);
    try {
      const updated = await updatePriceList(api, list.id, { status: "archived" }, loaded.etag);
      toast({ tone: "success", title: "Liste arşivlendi", description: updated.priceList.name });
      setArchiveOpen(false);
      onSaved(updated.priceList, updated.etag);
    } catch (error) {
      if (isVersionConflict(error)) {
        setArchiveOpen(false);
        setConflict(true);
      } else {
        setArchiveError(pricingErrorMessage(error));
      }
    } finally {
      setArchiveBusy(false);
    }
  };

  const baseName = list.basePriceListId ? (loaded.lists.find((l) => l.id === list.basePriceListId)?.name ?? "Taban liste") : null;

  return (
    <>
      <BackLink />
      <PageHeader
        title={list.name}
        description={
          <span className="inline-flex flex-wrap items-center gap-2">
            <span>
              {priceListKindLabels[list.kind] ?? list.kind} · {list.currency} · {vatModeLabel(list.pricesIncludeTax)} · {validityText(list)}
            </span>
            {list.isDefault ? (
              <Badge tone="brand" icon={<Star className="size-3" aria-hidden="true" />}>
                Varsayılan
              </Badge>
            ) : null}
            <Badge tone={priceListStatusMeta[list.status]?.tone ?? "neutral"}>{priceListStatusMeta[list.status]?.label ?? list.status}</Badge>
          </span>
        }
        actions={
          canEdit && !list.isDefault ? (
            <Button variant="secondary" icon={<Archive className="size-4" />} onClick={() => setArchiveOpen(true)}>
              Arşivle
            </Button>
          ) : null
        }
      />

      {archived ? (
        <FormAlert tone="info" title="Arşivlenmiş liste">
          Arşivlenen liste fiyat çözümlemede kullanılmaz ve değiştirilemez.
        </FormAlert>
      ) : !permissions.write ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Fiyat listelerini işletme sahibi veya yönetici düzenler.
        </FormAlert>
      ) : null}
      {baseName ? (
        <FormAlert tone="info" title="Türetilmiş liste">
          Bu listede kalemi olmayan ürünlerde fiyat “{baseName}” listesinden {formatPercent(list.discountPercent) ?? "%0"} iskontoyla hesaplanır. Aşağıdaki
          kalemler taban fiyatın yerine geçer.
        </FormAlert>
      ) : null}
      {conflict ? (
        <FormAlert
          tone="warning"
          title="Liste başkası tarafından değiştirildi"
          actions={
            <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={onReload}>
              Yeniden yükle
            </Button>
          }
        >
          Bu liste siz düzenlerken başka biri tarafından güncellendi; değişiklikleriniz kaydedilmedi. Güncel hâlini yeniden yükleyin, ardından
          değişikliklerinizi tekrar yapın.
        </FormAlert>
      ) : null}

      {canEdit ? (
        <section aria-labelledby="sec-settings" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
          <h2 id="sec-settings" className="text-h3 text-fg">
            Liste ayarları
          </h2>
          <form noValidate onSubmit={submit} aria-label="Liste ayarları" className="flex flex-col gap-4">
            {formError ? (
              <FormAlert ref={errorRef} tone="error" title="Kaydedilemedi">
                {formError}
              </FormAlert>
            ) : null}
            <PriceListFields values={values} errors={errors} set={set} lists={loaded.lists} original={list} canChooseKind={permissions.readCost} />
            <div className="flex justify-end">
              <Button type="submit" loading={saving}>
                Ayarları kaydet
              </Button>
            </div>
          </form>
        </section>
      ) : null}

      {archiveOpen ? (
        <Dialog
          open
          role="alertdialog"
          onClose={() => setArchiveOpen(false)}
          title={`${list.name} arşivlensin mi?`}
          description="Arşivlenen liste müşteri ve bayi grubu atamalarında fiyat vermez; o müşteriler bir sonraki listeye (grup veya varsayılan) düşer. Bu işlem geri alınamaz."
          dismissOnBackdrop={false}
          footer={
            <>
              <Button variant="secondary" onClick={() => setArchiveOpen(false)} disabled={archiveBusy}>
                Vazgeç
              </Button>
              <Button variant="danger" loading={archiveBusy} onClick={() => void archive()}>
                Arşivle
              </Button>
            </>
          }
        >
          {archiveError ? (
            <p role="alert" className="text-body text-danger-fg">
              {archiveError}
            </p>
          ) : null}
        </Dialog>
      ) : null}
    </>
  );
}

"use client";

import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { CircleAlert, Calculator, Plus, Star, Tags } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { QUOTE_HREF, createPriceList, listPriceLists, priceListHref, type PriceList } from "@/lib/pricing/api";
import { pricingErrorMessage, pricingFieldErrors } from "@/lib/pricing/errors";
import { priceListKindLabels, priceListStatusMeta, vatModeLabel } from "@/lib/pricing/labels";
import { formatPercent } from "@/lib/pricing/money";
import { pricingPermissions } from "@/lib/pricing/permissions";
import { buildCreatePriceList, newPriceListValues, validatePriceList, type PriceListFormValues } from "@/lib/pricing/price-list-form";
import { useSession } from "@/lib/session/session-context";
import { PriceListFields } from "./price-list-fields";

type Loadable = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: PriceList[] };

/** "1 Oca 2026 – 31 Ara 2026", "1 Oca 2026'dan itibaren", "Süresiz". */
export function validityText(list: Pick<PriceList, "validFrom" | "validTo">): string {
  const fmt = (d: string) => new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" }).format(new Date(`${d}T00:00:00Z`));
  if (list.validFrom && list.validTo) return `${fmt(list.validFrom)} – ${fmt(list.validTo)}`;
  if (list.validFrom) return `${fmt(list.validFrom)} itibarıyla`;
  if (list.validTo) return `${fmt(list.validTo)} tarihine kadar`;
  return "Süresiz";
}

export function usePriceLists(includeArchived: boolean) {
  const { api } = useSession();
  const [state, setState] = useState<{ key: string; value: Loadable } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const key = `${includeArchived}|${attempt}`;
  useEffect(() => {
    const controller = new AbortController();
    listPriceLists(api, { includeArchived, signal: controller.signal })
      .then((data) => {
        if (!controller.signal.aborted) setState({ key, value: { status: "ready", data } });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ key, value: { status: "error", message: pricingErrorMessage(error) } });
      });
    return () => controller.abort();
  }, [api, includeArchived, key]);
  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  const current: Loadable = state?.key === key ? state.value : { status: "loading" };
  return { state: current, reload };
}

/** /fiyat-listeleri (W18): sales and (for cost-permitted roles) purchase lists; default first. */
export function PriceListIndex() {
  const { role } = useSession();
  const permissions = pricingPermissions(role);
  const [showArchived, setShowArchived] = useState(false);
  const { state, reload } = usePriceLists(showArchived);
  const [creating, setCreating] = useState(false);
  const lists = useMemo(() => (state.status === "ready" ? state.data : []), [state]);
  const names = useMemo(() => new Map(lists.map((l) => [l.id, l.name])), [lists]);

  const derivation = (l: PriceList) =>
    l.basePriceListId ? `${names.get(l.basePriceListId) ?? "Taban liste"} − ${formatPercent(l.discountPercent) ?? "%0"}` : "—";

  const columns: TableColumn<PriceList>[] = [
    {
      key: "name",
      header: "Liste",
      cell: (l) => (
        <span className="inline-flex flex-wrap items-center gap-2">
          <Link href={priceListHref(l.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
            {l.name}
          </Link>
          {l.isDefault ? (
            <Badge tone="brand" icon={<Star className="size-3" aria-hidden="true" />}>
              Varsayılan
            </Badge>
          ) : null}
        </span>
      ),
    },
    { key: "kind", header: "Tür", width: "6rem", cell: (l) => priceListKindLabels[l.kind] ?? l.kind },
    { key: "currency", header: "Para birimi", width: "7rem", cell: (l) => <span className="font-mono">{l.currency}</span> },
    { key: "vat", header: "KDV", width: "7rem", cell: (l) => vatModeLabel(l.pricesIncludeTax) },
    { key: "validity", header: "Geçerlilik", cell: validityText },
    { key: "base", header: "Türetme", cell: derivation },
    {
      key: "status",
      header: "Durum",
      width: "8rem",
      cell: (l) => <Badge tone={priceListStatusMeta[l.status]?.tone ?? "neutral"}>{priceListStatusMeta[l.status]?.label ?? l.status}</Badge>,
    },
  ];

  return (
    <>
      <PageHeader
        title="Fiyat listeleri"
        description="Satış listeleri, kademeli fiyatlar ve taban listeden iskontolu türetme. Müşteri ve bayi gruplarına atanır."
        actions={
          <>
            <Link href={QUOTE_HREF} className={buttonClasses("secondary")}>
              <Calculator className="size-4" aria-hidden="true" />
              Fiyat sorgula
            </Link>
            {permissions.write ? (
              <Button icon={<Plus className="size-4" />} onClick={() => setCreating(true)}>
                Yeni liste
              </Button>
            ) : null}
          </>
        }
      />
      {!permissions.write ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Fiyat listelerini işletme sahibi veya yönetici düzenler. Fiyatları “Fiyat sorgula” ile müşteri bazında görebilirsiniz.
        </FormAlert>
      ) : null}

      <section aria-labelledby="price-list-list" className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="price-list-list" className="text-h3 text-fg">
            Listeler
          </h2>
          <label className="inline-flex min-h-11 items-center gap-2 text-body text-fg">
            <input type="checkbox" className="size-5 accent-(--bos-brand-primary)" checked={showArchived} onChange={(e) => setShowArchived(e.target.checked)} />
            Arşivlenenleri göster
          </label>
        </div>
        {state.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState headingLevel="h3" icon={<CircleAlert className="size-6" />} title="Fiyat listeleri yüklenemedi" description={state.message} action={<Button onClick={reload}>Tekrar dene</Button>} />
          </div>
        ) : (
          <>
            <div className="hidden md:block">
              <Table
                caption="Fiyat listeleri"
                columns={columns}
                rows={lists}
                getRowId={(l) => l.id}
                loading={state.status === "loading"}
                loadingRows={3}
                empty={
                  <EmptyState
                    headingLevel="h3"
                    icon={<Tags className="size-6" />}
                    title="Henüz fiyat listesi yok"
                    description={permissions.write ? "İlk satış listenizi oluşturun; para biriminin ilk listesi varsayılan olur." : "Bu işletmede henüz fiyat listesi yok."}
                  />
                }
              />
            </div>
            <ul aria-label="Fiyat listeleri" className="flex flex-col gap-2 md:hidden">
              {state.status === "loading" ? <li className="py-6 text-center text-body text-fg-muted">Yükleniyor…</li> : null}
              {state.status === "ready" && lists.length === 0 ? <li className="rounded-lg border border-line bg-card p-4 text-body text-fg-muted">Henüz fiyat listesi yok.</li> : null}
              {lists.map((l) => (
                <li key={l.id} className="rounded-lg border border-line bg-card p-4 shadow-e1">
                  <div className="flex items-start justify-between gap-2">
                    <Link href={priceListHref(l.id)} className="min-h-11 py-2 font-medium text-brand-fg underline-offset-2 hover:underline">
                      {l.name}
                    </Link>
                    <div className="flex flex-wrap justify-end gap-1">
                      {l.isDefault ? <Badge tone="brand">Varsayılan</Badge> : null}
                      <Badge tone={priceListStatusMeta[l.status]?.tone ?? "neutral"}>{priceListStatusMeta[l.status]?.label ?? l.status}</Badge>
                    </div>
                  </div>
                  <p className="text-caption text-fg-muted">
                    {priceListKindLabels[l.kind] ?? l.kind} · {l.currency} · {vatModeLabel(l.pricesIncludeTax)} · {validityText(l)}
                  </p>
                  {l.basePriceListId ? <p className="text-caption text-fg-muted">Türetme: {derivation(l)}</p> : null}
                </li>
              ))}
            </ul>
          </>
        )}
      </section>

      {creating ? <CreatePriceListDialog lists={lists} canChooseKind={permissions.readCost} onClose={() => setCreating(false)} /> : null}
    </>
  );
}

function CreatePriceListDialog({ lists, canChooseKind, onClose }: { lists: PriceList[]; canChooseKind: boolean; onClose: () => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const router = useRouter();
  const [values, setValues] = useState<PriceListFormValues>(() => newPriceListValues());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = (patch: Partial<PriceListFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validatePriceList(values);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    setSaving(true);
    try {
      const body = buildCreatePriceList(values);
      const created = await createPriceList(api, body, keys.keyFor(body));
      toast({ tone: "success", title: "Fiyat listesi oluşturuldu", description: created.isDefault ? `${created.name} — ${created.currency} için varsayılan.` : created.name });
      router.push(priceListHref(created.id));
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) {
        setErrors(pricingFieldErrors(error));
        setFormError(error.userMessage);
      } else if (error instanceof ApiError && error.code === "pricing.name_taken") {
        setErrors({ name: pricingErrorMessage(error) });
        setFormError(pricingErrorMessage(error));
      } else {
        setFormError(pricingErrorMessage(error));
      }
      setSaving(false);
    }
  };

  return (
    <Dialog
      open
      size="lg"
      onClose={onClose}
      title="Yeni fiyat listesi"
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form="price-list-form" loading={saving}>
            Listeyi oluştur
          </Button>
        </>
      }
    >
      <form id="price-list-form" noValidate onSubmit={submit} className="flex flex-col gap-4">
        {formError ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {formError}
          </FormAlert>
        ) : null}
        <PriceListFields values={values} errors={errors} set={set} lists={lists} original={null} canChooseKind={canChooseKind} />
      </form>
    </Dialog>
  );
}

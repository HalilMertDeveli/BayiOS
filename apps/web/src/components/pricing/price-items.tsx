"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { CircleAlert, Layers, Pencil, Trash2, X } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { useVariantDirectory } from "@/components/inventory/use-inventory-data";
import { VariantScanner } from "@/components/inventory/variant-scanner";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import type { VariantRef } from "@/lib/inventory/receipt-form";
import { deletePriceListItem, listPriceListItems, upsertPriceListItems, type PriceList, type PriceListItem } from "@/lib/pricing/api";
import { itemRowErrors, pricingErrorMessage, pricingFieldErrors, type RowErrors } from "@/lib/pricing/errors";
import { buildUpsertItems, draftFromItem, newDraftRow, validateDraftRows, type DraftRow } from "@/lib/pricing/items-form";
import { formatMoney, formatQuantity } from "@/lib/pricing/money";
import { useSession } from "@/lib/session/session-context";

type ItemsState =
  | { status: "loading" }
  | { status: "error"; message: string }
  | { status: "ready"; items: PriceListItem[]; nextCursor: string | null; hasMore: boolean };

function variantLabel(ref: VariantRef | null | undefined, id: string) {
  if (ref === undefined) return { sku: "…", name: null };
  if (ref === null) return { sku: "Bilinmeyen ürün", name: id.slice(0, 8) };
  return { sku: ref.sku, name: ref.name };
}

/**
 * Items of a price list: quantity tiers (min_quantity) per variant, edited as a draft and saved in one bulk upsert
 * (≤ 500 rows, Idempotency-Key per attempt). A 422 marks the offending rows (`items[i].field`). Prices are formatted
 * with Intl in the list currency; nothing is computed here.
 */
export function PriceItems({ priceList, canEdit }: { priceList: PriceList; canEdit: boolean }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [state, setState] = useState<ItemsState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [more, setMore] = useState<{ loading: boolean; error: string | null }>({ loading: false, error: null });
  const [draft, setDraft] = useState<DraftRow[]>([]);
  const [rowErrors, setRowErrors] = useState<RowErrors>({});
  const [general, setGeneral] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const [deleting, setDeleting] = useState<PriceListItem | null>(null);
  const [deleteBusy, setDeleteBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const currency = priceList.currency;

  useEffect(() => {
    const controller = new AbortController();
    listPriceListItems(api, priceList.id, { signal: controller.signal })
      .then((page) => {
        if (!controller.signal.aborted) setState({ status: "ready", items: page.items, nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", message: pricingErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, priceList.id, attempt]);

  const reload = useCallback(() => {
    setState({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  const items = useMemo(() => (state.status === "ready" ? state.items : []), [state]);
  const directory = useVariantDirectory(useMemo(() => items.map((i) => i.variantId), [items]));

  const loadMore = async () => {
    if (state.status !== "ready" || !state.nextCursor) return;
    setMore({ loading: true, error: null });
    try {
      const page = await listPriceListItems(api, priceList.id, { cursor: state.nextCursor });
      setState((prev) =>
        prev.status === "ready"
          ? { status: "ready", items: [...prev.items, ...page.items.filter((i) => !prev.items.some((x) => x.id === i.id))], nextCursor: page.page.nextCursor, hasMore: page.page.hasMore }
          : prev,
      );
      setMore({ loading: false, error: null });
    } catch (error) {
      setMore({ loading: false, error: pricingErrorMessage(error) });
    }
  };

  const clearErrors = () => {
    setRowErrors({});
    setGeneral([]);
  };

  const addVariant = (variant: VariantRef) => {
    clearErrors();
    // A second row for the same product is a new quantity tier: leave its minimum quantity for the user to type.
    setDraft((rows) => [...rows, newDraftRow(variant, rows.some((r) => r.variant.id === variant.id) ? "" : "1")]);
  };

  const editItem = (item: PriceListItem) => {
    const ref = directory.get(item.variantId);
    const variant: VariantRef = ref ?? { id: item.variantId, sku: variantLabel(ref, item.variantId).sku, name: null, barcode: null };
    clearErrors();
    setDraft((rows) => (rows.some((r) => r.itemId === item.id) ? rows : [...rows, draftFromItem(item, variant)]));
  };

  const updateRow = (key: string, patch: Partial<DraftRow>) => setDraft((rows) => rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const removeRow = (key: string) => {
    clearErrors();
    setDraft((rows) => rows.filter((r) => r.key !== key));
  };

  const focusAlert = () => requestAnimationFrame(() => alertRef.current?.focus());

  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    const local = validateDraftRows(draft);
    if (Object.keys(local.rows).length > 0 || local.general.length > 0) {
      setRowErrors(local.rows);
      setGeneral(local.general);
      focusAlert();
      return;
    }
    clearErrors();
    setSaving(true);
    try {
      const body = buildUpsertItems(draft);
      const result = await upsertPriceListItems(api, priceList.id, body, keys.keyFor(body));
      keys.reset();
      toast({ tone: "success", title: "Fiyatlar kaydedildi", description: `${result.created} yeni, ${result.updated} güncellenen kalem.` });
      setDraft([]);
      reload();
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) {
        const mapped = itemRowErrors(pricingFieldErrors(error), (i) => i);
        setRowErrors(mapped.rows);
        setGeneral(mapped.general.length > 0 ? mapped.general : ["İşaretli satırları düzeltip tekrar kaydedin. Hiçbir satır kaydedilmedi."]);
      } else {
        setGeneral([pricingErrorMessage(error)]);
      }
      focusAlert();
    } finally {
      setSaving(false);
    }
  };

  const remove = async (item: PriceListItem) => {
    setDeleteBusy(true);
    setDeleteError(null);
    try {
      await deletePriceListItem(api, priceList.id, item.id);
      toast({ tone: "success", title: "Kalem silindi" });
      setDeleting(null);
      setDraft((rows) => rows.filter((r) => r.itemId !== item.id));
      reload();
    } catch (error) {
      setDeleteError(pricingErrorMessage(error));
    } finally {
      setDeleteBusy(false);
    }
  };

  const errorCount = Object.keys(rowErrors).length + general.length;

  const columns: TableColumn<PriceListItem>[] = [
    {
      key: "variant",
      header: "Ürün",
      cell: (i) => {
        const label = variantLabel(directory.get(i.variantId), i.variantId);
        return (
          <span className="flex flex-col">
            <span className="font-mono text-caption">{label.sku}</span>
            {label.name ? <span className="text-caption text-fg-muted">{label.name}</span> : null}
          </span>
        );
      },
    },
    { key: "min", header: "En az miktar", numeric: true, width: "9rem", cell: (i) => formatQuantity(i.minQuantity) },
    { key: "price", header: `Birim fiyat (${currency})`, numeric: true, width: "11rem", cell: (i) => formatMoney(i.unitPrice, currency) },
    ...(canEdit
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            width: "13rem",
            cell: (i: PriceListItem) => {
              const sku = variantLabel(directory.get(i.variantId), i.variantId).sku;
              return (
                <span className="flex justify-end gap-1">
                  <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} onClick={() => editItem(i)} aria-label={`${sku} (en az ${formatQuantity(i.minQuantity)}) fiyatını değiştir`}>
                    Değiştir
                  </Button>
                  <Button variant="ghost" size="sm" icon={<Trash2 className="size-4" />} onClick={() => setDeleting(i)} aria-label={`${sku} (en az ${formatQuantity(i.minQuantity)}) kalemini sil`}>
                    Sil
                  </Button>
                </span>
              );
            },
          },
        ]
      : []),
  ];

  return (
    <section aria-labelledby="sec-items" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="sec-items" className="flex items-center gap-2 text-h3 text-fg">
          <Layers className="size-5" aria-hidden="true" />
          Fiyatlar ve miktar kademeleri
        </h2>
        <p role="status" className="text-caption text-fg-muted">
          {state.status === "loading" ? "Yükleniyor…" : state.status === "ready" ? `${state.items.length} kalem gösteriliyor${state.hasMore ? " (devamı var)" : ""}` : ""}
        </p>
      </div>

      {canEdit ? (
        <form noValidate onSubmit={save} aria-label="Fiyat ekle veya güncelle" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
          <h3 className="text-body font-semibold text-fg">Fiyat ekle / güncelle</h3>
          <p className="text-caption text-fg-muted">
            Ürünü okutun veya SKU/ad yazıp Enter&apos;a basın. Aynı ürünü tekrar ekleyip farklı “en az miktar” girerek kademe tanımlayın (ör. 1 adet ve 10 adet). Ürün +
            en az miktar aynıysa mevcut fiyat güncellenir.
          </p>
          <VariantScanner label="Ürün okut veya ara" hint="Barkod, SKU veya ürün adı" onPick={addVariant} pickedText={(v) => `${v.sku} eklendi; fiyatını girin.`} />

          {errorCount > 0 ? (
            <FormAlert ref={alertRef} tone="error" title="Fiyatlar kaydedilemedi">
              {general.length > 0 ? (
                <ul className="list-disc pl-5">
                  {general.map((m) => (
                    <li key={m}>{m}</li>
                  ))}
                </ul>
              ) : (
                <p>İşaretli satırları düzeltin. Hiçbir satır kaydedilmedi.</p>
              )}
            </FormAlert>
          ) : null}

          {draft.length > 0 ? (
            <ol aria-label="Kaydedilecek satırlar" className="flex flex-col gap-2">
              {draft.map((row, index) => {
                const err = rowErrors[index] ?? {};
                const n = index + 1;
                return (
                  <li
                    key={row.key}
                    aria-label={`${n}. satır: ${row.variant.sku}`}
                    className={`grid grid-cols-1 gap-3 rounded-md border p-3 sm:grid-cols-[1fr_9rem_11rem_auto] sm:items-start ${err.row || err.variantId || err.minQuantity || err.unitPrice ? "border-danger-border bg-danger-bg/40" : "border-line"}`}
                  >
                    <div className="flex flex-col">
                      <span className="text-caption text-fg-muted">{n}. satır{row.itemId ? " · mevcut kalem" : " · yeni"}</span>
                      <span className="font-mono text-body text-fg">{row.variant.sku}</span>
                      {row.variant.name ? <span className="text-caption text-fg-muted">{row.variant.name}</span> : null}
                      {err.variantId ? <span className="text-caption text-danger-fg">{err.variantId}</span> : null}
                      {err.row ? <span className="text-caption text-danger-fg">{err.row}</span> : null}
                    </div>
                    <Input
                      label={`${n}. satır en az miktar`}
                      inputMode="decimal"
                      value={row.minQuantity}
                      readOnly={row.itemId !== null}
                      error={err.minQuantity}
                      hint={row.itemId ? "Kademe sabit" : undefined}
                      onChange={(e) => updateRow(row.key, { minQuantity: e.target.value })}
                    />
                    <Input
                      label={`${n}. satır birim fiyat`}
                      inputMode="decimal"
                      value={row.unitPrice}
                      error={err.unitPrice}
                      suffix={<span className="text-caption">{currency}</span>}
                      onChange={(e) => updateRow(row.key, { unitPrice: e.target.value })}
                    />
                    <Button variant="ghost" className="sm:mt-[1.625rem]" icon={<X className="size-4" />} onClick={() => removeRow(row.key)} aria-label={`${n}. satırı kaldır (${row.variant.sku})`}>
                      Kaldır
                    </Button>
                  </li>
                );
              })}
            </ol>
          ) : (
            <p className="text-caption text-fg-muted">Taslakta satır yok.</p>
          )}
          <div className="flex flex-wrap justify-end gap-2">
            {draft.length > 0 ? (
              <Button
                variant="secondary"
                onClick={() => {
                  setDraft([]);
                  clearErrors();
                }}
                disabled={saving}
              >
                Taslağı temizle
              </Button>
            ) : null}
            <Button type="submit" loading={saving} disabled={draft.length === 0}>
              {draft.length > 0 ? `${draft.length} satırı kaydet` : "Kaydet"}
            </Button>
          </div>
        </form>
      ) : null}

      {state.status === "error" ? (
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState headingLevel="h3" icon={<CircleAlert className="size-6" />} title="Fiyatlar yüklenemedi" description={state.message} action={<Button onClick={reload}>Tekrar dene</Button>} />
        </div>
      ) : (
        <>
          <div className="hidden md:block">
            <Table
              caption={`${priceList.name} fiyatları`}
              columns={columns}
              rows={items}
              getRowId={(i) => i.id}
              loading={state.status === "loading"}
              loadingRows={3}
              maxHeight="60vh"
              empty={
                <EmptyState
                  headingLevel="h3"
                  icon={<Layers className="size-6" />}
                  title="Bu listede henüz fiyat yok"
                  description={canEdit ? "Yukarıdan ürün okutup fiyat girin." : priceList.basePriceListId ? "Fiyatlar taban listeden iskontoyla hesaplanır." : "Bu listede tanımlı fiyat yok."}
                />
              }
            />
          </div>
          <ul aria-label={`${priceList.name} fiyatları`} className="flex flex-col gap-2 md:hidden">
            {state.status === "loading" ? <li className="py-6 text-center text-body text-fg-muted">Yükleniyor…</li> : null}
            {state.status === "ready" && items.length === 0 ? <li className="rounded-lg border border-line bg-card p-4 text-body text-fg-muted">Bu listede henüz fiyat yok.</li> : null}
            {items.map((i) => {
              const label = variantLabel(directory.get(i.variantId), i.variantId);
              return (
                <li key={i.id} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                  <div className="flex items-start justify-between gap-2">
                    <span className="flex flex-col">
                      <span className="font-mono text-body text-fg">{label.sku}</span>
                      {label.name ? <span className="text-caption text-fg-muted">{label.name}</span> : null}
                    </span>
                    <span className="text-right font-medium tabular-nums text-fg">{formatMoney(i.unitPrice, currency)}</span>
                  </div>
                  <p className="text-caption text-fg-muted">En az {formatQuantity(i.minQuantity)}</p>
                  {canEdit ? (
                    <div className="mt-2 flex flex-wrap gap-2">
                      <Button variant="secondary" size="lg" onClick={() => editItem(i)} aria-label={`${label.sku} (en az ${formatQuantity(i.minQuantity)}) fiyatını değiştir`}>
                        Değiştir
                      </Button>
                      <Button variant="ghost" size="lg" onClick={() => setDeleting(i)} aria-label={`${label.sku} (en az ${formatQuantity(i.minQuantity)}) kalemini sil`}>
                        Sil
                      </Button>
                    </div>
                  ) : null}
                </li>
              );
            })}
          </ul>
          {state.status === "ready" && state.hasMore ? (
            <div className="flex flex-col items-center gap-2">
              <Button variant="secondary" loading={more.loading} onClick={() => void loadMore()}>
                Daha fazla yükle
              </Button>
              {more.error ? (
                <p role="alert" className="text-caption text-danger-fg">
                  {more.error}
                </p>
              ) : null}
            </div>
          ) : null}
        </>
      )}

      {deleting ? (
        <Dialog
          open
          role="alertdialog"
          onClose={() => setDeleting(null)}
          title="Kalem silinsin mi?"
          description={`${variantLabel(directory.get(deleting.variantId), deleting.variantId).sku} — en az ${formatQuantity(deleting.minQuantity)}: ${formatMoney(deleting.unitPrice, currency)}. Silinirse bu kademe için fiyat bir alt kademeden, taban listeden veya sonraki listeden gelir.`}
          dismissOnBackdrop={false}
          footer={
            <>
              <Button variant="secondary" onClick={() => setDeleting(null)} disabled={deleteBusy}>
                Vazgeç
              </Button>
              <Button variant="danger" loading={deleteBusy} onClick={() => void remove(deleting)}>
                Kalemi sil
              </Button>
            </>
          }
        >
          {deleteError ? (
            <p role="alert" className="text-body text-danger-fg">
              {deleteError}
            </p>
          ) : null}
        </Dialog>
      ) : null}
    </section>
  );
}

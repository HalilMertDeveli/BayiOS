"use client";

import { useMemo, useState, type FormEvent, useId } from "react";
import Link from "next/link";
import { Archive, ArchiveRestore, ArrowLeft, CircleAlert, Pencil, Plus, UsersRound } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { isVersionConflict } from "@/lib/catalog/errors";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { CUSTOMERS_HREF, createDealerGroup, updateDealerGroup, type DealerGroup } from "@/lib/customers/api";
import {
  SHORT_TEXT_MAX,
  buildCreateDealerGroup,
  buildDealerGroupPatch,
  dealerGroupToValues,
  newDealerGroupValues,
  validateDealerGroup,
  type DealerGroupFormValues,
} from "@/lib/customers/customer-form";
import { customerErrorMessage, customerFieldErrors } from "@/lib/customers/errors";
import { customerPermissions } from "@/lib/customers/permissions";
import { formatPercent } from "@/lib/pricing/money";
import { useSession } from "@/lib/session/session-context";
import { assignableOptions, useCustomerReference } from "./use-customer-reference";

const VERSION_CONFLICT = "Grup siz düzenlerken değişti. Pencereyi kapatıp sayfayı yenileyin, sonra tekrar deneyin.";

function mutationError(error: unknown): { fields: Record<string, string>; message: string } {
  if (error instanceof ApiError && error.status === 422) return { fields: customerFieldErrors(error), message: error.userMessage };
  if (isVersionConflict(error)) return { fields: {}, message: VERSION_CONFLICT };
  if (error instanceof ApiError && error.code === "customers.group_name_taken") return { fields: { name: customerErrorMessage(error) }, message: customerErrorMessage(error) };
  return { fields: {}, message: customerErrorMessage(error) };
}

/** /musteriler/gruplar (W18): dealer groups with their price list and default discount. */
export function DealerGroups() {
  const uid = useId();
  const { access } = useSession();
  const permissions = customerPermissions(access);
  const { state, reload, upsertGroup } = useCustomerReference();
  const [showArchived, setShowArchived] = useState(false);
  const [editing, setEditing] = useState<DealerGroup | "new" | null>(null);
  const [archiving, setArchiving] = useState<DealerGroup | null>(null);

  const ready = state.status === "ready" ? state : null;
  const priceListNames = useMemo(() => new Map(ready?.priceLists.map((l) => [l.id, l.name]) ?? []), [ready]);
  const rows = (ready?.groups ?? []).filter((g) => showArchived || g.status !== "archived");

  const priceListCell = (g: DealerGroup) =>
    g.priceListId ? (priceListNames.get(g.priceListId) ?? (ready?.priceListsAvailable ? "Erişilemeyen liste" : "Atanmış liste")) : "—";

  const columns: TableColumn<DealerGroup>[] = [
    { key: "name", header: "Grup", cell: (g) => <span className="font-medium">{g.name}</span> },
    { key: "list", header: "Fiyat listesi", cell: priceListCell },
    { key: "discount", header: "Varsayılan iskonto", numeric: true, width: "10rem", cell: (g) => formatPercent(g.defaultDiscountPercent) ?? "—" },
    {
      key: "status",
      header: "Durum",
      width: "8rem",
      cell: (g) => <Badge tone={g.status === "archived" ? "warning" : "success"}>{g.status === "archived" ? "Arşivlendi" : "Aktif"}</Badge>,
    },
    ...(permissions.manageGroups
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            width: "14rem",
            cell: (g: DealerGroup) => (
              <span className="flex justify-end gap-1">
                {g.status !== "archived" ? (
                  <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} onClick={() => setEditing(g)} aria-label={`${g.name} grubunu düzenle`}>
                    Düzenle
                  </Button>
                ) : null}
                <Button
                  variant="ghost"
                  size="sm"
                  icon={g.status === "archived" ? <ArchiveRestore className="size-4" /> : <Archive className="size-4" />}
                  onClick={() => setArchiving(g)}
                  aria-label={`${g.name} grubunu ${g.status === "archived" ? "geri al (arşivden çıkar)" : "arşivle"}`}
                >
                  {g.status === "archived" ? "Geri al" : "Arşivle"}
                </Button>
              </span>
            ),
          },
        ]
      : []),
  ];

  return (
    <>
      <Link href={CUSTOMERS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
        <ArrowLeft className="size-4" aria-hidden="true" />
        Müşteriler
      </Link>
      <PageHeader
        title="Bayi grupları"
        description="Müşterileri gruplayın; grubun fiyat listesi ve varsayılan iskontosu, müşteriye özel liste yoksa uygulanır."
        actions={
          permissions.manageGroups ? (
            <Button icon={<Plus className="size-4" />} onClick={() => setEditing("new")}>
              Yeni grup
            </Button>
          ) : null
        }
      />
      {!permissions.manageGroups ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Bayi gruplarını işletme sahibi, yönetici veya operasyon müdürü yönetir.
        </FormAlert>
      ) : null}

      <section aria-labelledby={`${uid}-group-list`} className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id={`${uid}-group-list`} className="text-h3 text-fg">
            Gruplar
          </h2>
          <label className="inline-flex min-h-11 items-center gap-2 text-body text-fg">
            <input type="checkbox" className="size-5 accent-(--bos-brand-primary)" checked={showArchived} onChange={(e) => setShowArchived(e.target.checked)} />
            Arşivlenenleri göster
          </label>
        </div>
        {state.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState headingLevel="h3" icon={<CircleAlert className="size-6" />} title="Gruplar yüklenemedi" description={state.message} action={<Button onClick={reload}>Tekrar dene</Button>} />
          </div>
        ) : (
          <>
            <div className="hidden md:block">
              <Table
                caption="Bayi grupları"
                columns={columns}
                rows={rows}
                getRowId={(g) => g.id}
                loading={state.status === "loading"}
                loadingRows={3}
                empty={
                  <EmptyState
                    headingLevel="h3"
                    icon={<UsersRound className="size-6" />}
                    title="Henüz bayi grubu yok"
                    description="Ör. “Bayi A”, “Perakende” gibi gruplar açıp her birine bir fiyat listesi atayın."
                  />
                }
              />
            </div>
            <ul aria-label="Bayi grupları" className="flex flex-col gap-2 md:hidden">
              {state.status === "loading" ? <li className="py-6 text-center text-body text-fg-muted">Yükleniyor…</li> : null}
              {state.status === "ready" && rows.length === 0 ? <li className="rounded-lg border border-line bg-card p-4 text-body text-fg-muted">Henüz bayi grubu yok.</li> : null}
              {rows.map((g) => (
                <li key={g.id} className="rounded-lg border border-line bg-card p-4 shadow-e1">
                  <div className="flex items-start justify-between gap-2">
                    <span className="font-medium text-fg">{g.name}</span>
                    <Badge tone={g.status === "archived" ? "warning" : "success"}>{g.status === "archived" ? "Arşivlendi" : "Aktif"}</Badge>
                  </div>
                  <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-caption">
                    <dt className="text-fg-muted">Fiyat listesi</dt>
                    <dd className="text-fg">{priceListCell(g)}</dd>
                    <dt className="text-fg-muted">İskonto</dt>
                    <dd className="text-fg">{formatPercent(g.defaultDiscountPercent) ?? "—"}</dd>
                  </dl>
                  {permissions.manageGroups ? (
                    <div className="mt-3 flex flex-wrap gap-2">
                      {g.status !== "archived" ? (
                        <Button variant="secondary" size="lg" icon={<Pencil className="size-4" />} onClick={() => setEditing(g)} aria-label={`${g.name} grubunu düzenle`}>
                          Düzenle
                        </Button>
                      ) : null}
                      <Button variant="ghost" size="lg" onClick={() => setArchiving(g)} aria-label={`${g.name} grubunu ${g.status === "archived" ? "geri al (arşivden çıkar)" : "arşivle"}`}>
                        {g.status === "archived" ? "Geri al" : "Arşivle"}
                      </Button>
                    </div>
                  ) : null}
                </li>
              ))}
            </ul>
          </>
        )}
      </section>

      {editing && ready ? (
        <DealerGroupDialog
          group={editing === "new" ? null : editing}
          reference={ready}
          onClose={() => setEditing(null)}
          onSaved={(g) => {
            upsertGroup(g);
            setEditing(null);
          }}
        />
      ) : null}
      {archiving ? (
        <ArchiveGroupDialog
          group={archiving}
          onClose={() => setArchiving(null)}
          onSaved={(g) => {
            upsertGroup(g);
            setArchiving(null);
          }}
        />
      ) : null}
    </>
  );
}

function DealerGroupDialog({
  group,
  reference,
  onClose,
  onSaved,
}: {
  group: DealerGroup | null;
  reference: Extract<ReturnType<typeof useCustomerReference>["state"], { status: "ready" }>;
  onClose: () => void;
  onSaved: (g: DealerGroup) => void;
}) {
  const uid = useId();
  const { api, access } = useSession();
  const canAssign = customerPermissions(access).assignPriceLists;
  const { toast } = useToast();
  const [values, setValues] = useState<DealerGroupFormValues>(() => (group ? dealerGroupToValues(group) : newDealerGroupValues()));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = (patch: Partial<DealerGroupFormValues>) => setValues((v) => ({ ...v, ...patch }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validateDealerGroup(values);
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;
    setSaving(true);
    try {
      if (!group) {
        const body = buildCreateDealerGroup(values, canAssign);
        const created = await createDealerGroup(api, body, keys.keyFor(body));
        toast({ tone: "success", title: "Bayi grubu eklendi", description: created.name });
        onSaved(created);
      } else {
        const patch = buildDealerGroupPatch(group, values, canAssign);
        if (Object.keys(patch).length === 0) {
          onClose();
          return;
        }
        const updated = await updateDealerGroup(api, group, patch);
        toast({ tone: "success", title: "Bayi grubu güncellendi", description: updated.name });
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

  return (
    <Dialog
      open
      onClose={onClose}
      title={group ? `Grubu düzenle: ${group.name}` : "Yeni bayi grubu"}
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={`${uid}-dealer-group-form`} loading={saving}>
            {group ? "Kaydet" : "Grubu ekle"}
          </Button>
        </>
      }
    >
      <form id={`${uid}-dealer-group-form`} noValidate onSubmit={submit} className="flex flex-col gap-4">
        {formError ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {formError}
          </FormAlert>
        ) : null}
        <Input label="Grup adı" required maxLength={SHORT_TEXT_MAX} value={values.name} error={errors.name} onChange={(e) => set({ name: e.target.value })} />
        <Select
          label="Fiyat listesi"
          placeholder="Liste yok (varsayılan liste)"
          options={assignableOptions(reference.priceLists, values.priceListId, (l) => l.kind === "sales")}
          value={values.priceListId}
          error={errors.priceListId}
          disabled={!canAssign || !reference.priceListsAvailable}
          hint={canAssign ? "Yalnızca satış listeleri atanabilir." : "Fiyat listesi atama yetkiniz yok."}
          onChange={(e) => set({ priceListId: e.target.value })}
        />
        <Input
          label="Varsayılan iskonto (%)"
          inputMode="decimal"
          value={values.defaultDiscountPercent}
          error={errors.defaultDiscountPercent}
          hint="0–100, en fazla 2 ondalık (ör. 7,5). Siparişte satır fiyatına önerilir."
          onChange={(e) => set({ defaultDiscountPercent: e.target.value })}
        />
      </form>
    </Dialog>
  );
}

function ArchiveGroupDialog({ group, onClose, onSaved }: { group: DealerGroup; onClose: () => void; onSaved: (g: DealerGroup) => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const archive = group.status !== "archived";

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const updated = await updateDealerGroup(api, group, { status: archive ? "archived" : "active" });
      toast({ tone: "success", title: archive ? "Grup arşivlendi" : "Grup arşivden çıkarıldı", description: updated.name });
      onSaved(updated);
    } catch (err) {
      setError(mutationError(err).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      role="alertdialog"
      onClose={onClose}
      title={archive ? `${group.name} arşivlensin mi?` : `${group.name} arşivden çıkarılsın mı?`}
      description={
        archive
          ? "Arşivlenen grubun fiyat listesi artık fiyat çözümlemede kullanılmaz; gruptaki müşteriler varsayılan listeye düşer."
          : "Grup yeniden aktif olur ve fiyat listesi tekrar uygulanır."
      }
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant={archive ? "danger" : "primary"} loading={busy} disabled={archive && !confirm} onClick={() => void run()}>
            {archive ? "Arşivle" : "Arşivden çıkar"}
          </Button>
        </>
      }
    >
      {archive ? <CheckboxField label="Gruptaki müşterilerin fiyatlarının değişebileceğini anladım" checked={confirm} onChange={setConfirm} /> : null}
      {error ? (
        <p role="alert" className="text-body text-danger-fg">
          {error}
        </p>
      ) : null}
    </Dialog>
  );
}

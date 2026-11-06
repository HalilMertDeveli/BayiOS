"use client";

import { useState, type FormEvent } from "react";
import { Archive, FolderPlus, FolderTree, Pencil, Plus } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { archiveCategory, createCategory, etagOf, listCategories, updateCategory, type Category } from "@/lib/catalog/api";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { catalogPermissions } from "@/lib/catalog/permissions";
import {
  CATEGORY_MAX_DEPTH,
  CATEGORY_NAME_MAX,
  buildCategoryCreate,
  buildCategoryPatch,
  canAddChild,
  categoryTree,
  validateCategory,
  type CategoryFormValues,
  type CategoryNode,
} from "@/lib/catalog/reference-form";
import { useSession } from "@/lib/session/session-context";
import { FormAlert } from "./form-alert";
import { ArchiveDialog, ArchivedToggle, ListError, ReferenceNav, saveProblem, useReferenceList } from "./reference-shared";

type Editing = { mode: "create"; parent: CategoryNode | null } | { mode: "edit"; node: CategoryNode };

/**
 * /urunler/kategoriler (#129): the category tree (≤ 4 levels). Create a root or a child, rename / re-order, archive a
 * category without active children. The parent is fixed at creation — the API has no "move". catalog.fields.manage
 * (owner, admin); others read only.
 */
export function CategorySettings() {
  const { api, access } = useSession();
  const canManage = catalogPermissions(access).manageFields;
  const [includeArchived, setIncludeArchived] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [archiving, setArchiving] = useState<CategoryNode | null>(null);
  const { toast } = useToast();
  const key = JSON.stringify({ includeArchived, attempt });
  const current = useReferenceList<Category>(key, (signal) => listCategories(api, signal, { includeArchived }));
  const reload = () => setAttempt((n) => n + 1);
  const nodes = current?.status === "ready" ? categoryTree(current.items) : [];

  const columns: TableColumn<CategoryNode>[] = [
    {
      key: "name",
      header: "Kategori",
      cell: (n) => (
        <span className="flex items-center gap-2" style={{ paddingInlineStart: `${(n.level - 1) * 1.5}rem` }}>
          {n.level > 1 ? (
            <span className="text-fg-muted" aria-hidden="true">
              └
            </span>
          ) : null}
          <span className={n.level === 1 ? "font-semibold" : "font-medium"}>{n.category.name}</span>
          <span className="sr-only">
            , seviye {n.level}
            {n.level > 1 ? `, yol: ${n.trail}` : ""}
          </span>
        </span>
      ),
    },
    { key: "children", header: "Alt kategori", numeric: true, width: "7rem", cell: (n) => String(n.activeChildren) },
    { key: "sort", header: "Sıra", numeric: true, width: "5rem", cell: (n) => String(n.category.sortOrder) },
    { key: "status", header: "Durum", width: "9rem", cell: (n) => (n.category.isArchived ? <Badge tone="warning">Arşivlenmiş</Badge> : <Badge tone="success">Aktif</Badge>) },
    ...(canManage
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            cell: (n: CategoryNode) =>
              n.category.isArchived ? null : (
                <div className="flex justify-end gap-1">
                  {canAddChild(n) ? (
                    <Button
                      variant="ghost"
                      size="sm"
                      icon={<FolderPlus className="size-4" />}
                      aria-label={`${n.category.name}: alt kategori ekle`}
                      onClick={() => setEditing({ mode: "create", parent: n })}
                    >
                      Alt kategori ekle
                    </Button>
                  ) : null}
                  <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} aria-label={`${n.category.name} kategorisini düzenle`} onClick={() => setEditing({ mode: "edit", node: n })}>
                    Düzenle
                  </Button>
                  <Button variant="ghost" size="sm" icon={<Archive className="size-4" />} aria-label={`${n.category.name} kategorisini arşivle`} onClick={() => setArchiving(n)}>
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
      <ReferenceNav />
      <PageHeader
        title="Kategoriler"
        description={`Ürünleri gruplayan kategori ağacı (en fazla ${CATEGORY_MAX_DEPTH} seviye). Bir kategorinin üst kategorisi sonradan değiştirilemez.`}
        actions={
          canManage ? (
            <Button icon={<Plus className="size-4" />} onClick={() => setEditing({ mode: "create", parent: null })}>
              Yeni ana kategori
            </Button>
          ) : null
        }
      />
      {!canManage ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Kategorileri yalnızca işletme sahibi ve yöneticiler değiştirebilir.
        </FormAlert>
      ) : null}
      <ArchivedToggle checked={includeArchived} onChange={setIncludeArchived} />

      {current?.status === "error" ? (
        <ListError title="Kategoriler yüklenemedi" message={current.message} onRetry={reload} />
      ) : (
        <Table
          caption="Kategori ağacı"
          columns={columns}
          rows={nodes}
          getRowId={(n) => n.category.id}
          loading={!current}
          density="compact"
          empty={
            <EmptyState
              headingLevel="h2"
              icon={<FolderTree className="size-6" />}
              title="Henüz kategori yok"
              description={canManage ? "Ürünlerinizi gruplamak için bir ana kategori ekleyin; altına alt kategoriler açabilirsiniz." : "Bu işletmede tanımlı kategori bulunmuyor."}
              action={
                canManage ? (
                  <Button icon={<Plus className="size-4" />} onClick={() => setEditing({ mode: "create", parent: null })}>
                    Yeni ana kategori
                  </Button>
                ) : undefined
              }
            />
          }
        />
      )}

      {editing ? (
        <CategoryDialog
          key={editing.mode === "edit" ? editing.node.category.id : `new-${editing.parent?.category.id ?? "root"}`}
          editing={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            reload();
          }}
        />
      ) : null}
      {archiving ? (
        <ArchiveDialog
          title="Kategoriyi arşivle"
          name={archiving.trail}
          blocked={
            archiving.activeChildren > 0
              ? `Bu kategorinin ${archiving.activeChildren} aktif alt kategorisi var. Önce alt kategorileri arşivleyin.`
              : null
          }
          effect={<p>Arşivlenen kategori yeni ürünlerde ve filtrelerde seçilemez. Bu kategorideki ürünler değişmez.</p>}
          onClose={() => setArchiving(null)}
          onConfirm={async () => {
            await archiveCategory(api, archiving.category.id, etagOf(archiving.category.version));
            toast({ tone: "success", title: "Kategori arşivlendi", description: archiving.category.name });
            setArchiving(null);
            reload();
          }}
        />
      ) : null}
    </>
  );
}

const KNOWN = new Set(["name", "sortOrder"]);

function CategoryDialog({ editing, onClose, onSaved }: { editing: Editing; onClose: () => void; onSaved: () => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const original = editing.mode === "edit" ? editing.node.category : null;
  const parent = editing.mode === "create" ? editing.parent : null;
  const [values, setValues] = useState<CategoryFormValues>(() => ({ name: original?.name ?? "", sortOrder: original ? String(original.sortOrder) : "" }));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = <K extends keyof CategoryFormValues>(k: K, v: CategoryFormValues[K]) => setValues((x) => ({ ...x, [k]: v }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    const clientErrors = validateCategory(values);
    setErrors(clientErrors);
    setSummary([]);
    if (Object.keys(clientErrors).length) return;
    try {
      if (original) {
        const patch = buildCategoryPatch(values, original);
        if (Object.keys(patch).length === 0) return onClose();
        setSaving(true);
        await updateCategory(api, original.id, patch, etagOf(original.version));
        toast({ tone: "success", title: "Kategori kaydedildi", description: values.name.trim() });
      } else {
        const body = buildCategoryCreate(values, parent?.category.id ?? null);
        setSaving(true);
        const created = await createCategory(api, body, keys.keyFor(body));
        keys.reset();
        toast({ tone: "success", title: "Kategori eklendi", description: parent ? `${parent.trail} › ${created.name}` : created.name });
      }
      onSaved();
    } catch (error) {
      setSaving(false);
      const problem = saveProblem(error, "name", KNOWN);
      setErrors(problem.fields);
      setSummary(problem.summary);
    }
  };

  const title = original ? `Kategoriyi düzenle: ${original.name}` : parent ? `${parent.category.name} altına kategori` : "Yeni ana kategori";
  const formId = "category-form";
  return (
    <Dialog
      open
      onClose={() => !saving && onClose()}
      title={title}
      description={!original && parent ? `Üst kategori: ${parent.trail}` : undefined}
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={formId} loading={saving}>
            {original ? "Kaydet" : "Kategoriyi ekle"}
          </Button>
        </>
      }
    >
      <form id={formId} noValidate onSubmit={submit} className="flex flex-col gap-3">
        {summary.length > 0 ? (
          <FormAlert tone="error" title="Kaydedilemedi">
            {summary.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </FormAlert>
        ) : null}
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_8rem]">
          <Input
            id="category-name"
            label="Kategori adı"
            required
            maxLength={CATEGORY_NAME_MAX}
            value={values.name}
            onChange={(e) => set("name", e.target.value)}
            error={errors.name}
            hint="Aynı üst kategori altında adlar benzersiz olmalı."
            autoComplete="off"
          />
          <Input
            id="category-sort"
            label="Sıra"
            inputMode="numeric"
            value={values.sortOrder}
            onChange={(e) => set("sortOrder", e.target.value)}
            error={errors.sortOrder}
            hint="Küçükten büyüğe."
            autoComplete="off"
          />
        </div>
      </form>
    </Dialog>
  );
}

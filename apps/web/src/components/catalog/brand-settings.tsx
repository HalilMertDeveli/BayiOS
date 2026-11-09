"use client";

import { useState, type FormEvent, useId } from "react";
import { Archive, Pencil, Plus, Search, Tag } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { archiveBrand, createBrand, etagOf, listBrands, updateBrand, type Brand } from "@/lib/catalog/api";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { catalogPermissions } from "@/lib/catalog/permissions";
import { BRAND_NAME_MAX, cleanName, sortBrands, validateBrandName } from "@/lib/catalog/reference-form";
import { useSession } from "@/lib/session/session-context";
import { FormAlert } from "./form-alert";
import { ArchiveDialog, ArchivedToggle, ListError, ReferenceNav, formatUpdated, saveProblem, useReferenceList } from "./reference-shared";

/** /urunler/markalar (#129): brands — list (server search), create, rename, archive. catalog.write; others read only. */
export function BrandSettings() {
  const { api, access } = useSession();
  const canWrite = catalogPermissions(access).write;
  const [includeArchived, setIncludeArchived] = useState(false);
  const [searchText, setSearchText] = useState("");
  const [q, setQ] = useState("");
  const [attempt, setAttempt] = useState(0);
  const [editing, setEditing] = useState<Brand | "new" | null>(null);
  const [archiving, setArchiving] = useState<Brand | null>(null);
  const { toast } = useToast();
  const key = JSON.stringify({ includeArchived, q, attempt });
  const current = useReferenceList<Brand>(key, (signal) => listBrands(api, signal, { includeArchived, q }));
  const reload = () => setAttempt((n) => n + 1);
  const rows = current?.status === "ready" ? sortBrands(current.items) : [];

  const columns: TableColumn<Brand>[] = [
    { key: "name", header: "Marka", cell: (b) => <span className="font-medium">{b.name}</span> },
    { key: "status", header: "Durum", width: "9rem", cell: (b) => (b.isArchived ? <Badge tone="warning">Arşivlenmiş</Badge> : <Badge tone="success">Aktif</Badge>) },
    { key: "updated", header: "Son değişiklik", width: "10rem", cell: (b) => formatUpdated(b.updatedAt, b.createdAt) },
    ...(canWrite
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            cell: (b: Brand) =>
              b.isArchived ? null : (
                <div className="flex justify-end gap-1">
                  <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} aria-label={`${b.name} markasını yeniden adlandır`} onClick={() => setEditing(b)}>
                    Yeniden adlandır
                  </Button>
                  <Button variant="ghost" size="sm" icon={<Archive className="size-4" />} aria-label={`${b.name} markasını arşivle`} onClick={() => setArchiving(b)}>
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
        title="Markalar"
        description="Ürünlerde seçilen markalar. Arşivlenen marka yeni ürünlerde seçilemez; mevcut ürünlerde görünmeye devam eder."
        actions={
          canWrite ? (
            <Button icon={<Plus className="size-4" />} onClick={() => setEditing("new")}>
              Yeni marka
            </Button>
          ) : null
        }
      />
      {!canWrite ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Markaları ürün düzenleme yetkisi olan kullanıcılar (işletme sahibi, yönetici, operasyon müdürü) değiştirebilir.
        </FormAlert>
      ) : null}

      <div className="flex flex-wrap items-end gap-4">
        <form
          role="search"
          aria-label="Marka ara"
          className="flex items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            setQ(searchText.trim());
          }}
        >
          <Input
            label="Marka ara"
            type="search"
            className="w-64"
            prefix={<Search className="size-4" />}
            value={searchText}
            onChange={(e) => {
              setSearchText(e.target.value);
              if (!e.target.value) setQ("");
            }}
            autoComplete="off"
          />
          <Button type="submit" variant="secondary">
            Ara
          </Button>
        </form>
        <ArchivedToggle checked={includeArchived} onChange={setIncludeArchived} />
      </div>

      {current?.status === "error" ? (
        <ListError title="Markalar yüklenemedi" message={current.message} onRetry={reload} />
      ) : (
        <Table
          caption="Markalar"
          columns={columns}
          rows={rows}
          getRowId={(b) => b.id}
          loading={!current}
          density="compact"
          empty={
            <EmptyState
              headingLevel="h2"
              icon={<Tag className="size-6" />}
              title={q ? "Aramaya uyan marka yok" : "Henüz marka yok"}
              description={
                q
                  ? "Farklı bir ad deneyin veya aramayı temizleyin."
                  : canWrite
                    ? "Ürünlerinizi markaya göre filtrelemek için ilk markanızı ekleyin."
                    : "Bu işletmede tanımlı marka bulunmuyor."
              }
              action={
                !q && canWrite ? (
                  <Button icon={<Plus className="size-4" />} onClick={() => setEditing("new")}>
                    Yeni marka
                  </Button>
                ) : undefined
              }
            />
          }
        />
      )}

      {editing ? (
        <BrandDialog
          key={editing === "new" ? "new" : editing.id}
          brand={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            reload();
          }}
        />
      ) : null}
      {archiving ? (
        <ArchiveDialog
          title="Markayı arşivle"
          name={archiving.name}
          effect={<p>Arşivlenen marka yeni ürünlerde ve filtrelerde seçilemez. Bu markadaki ürünler değişmez.</p>}
          onClose={() => setArchiving(null)}
          onConfirm={async () => {
            await archiveBrand(api, archiving.id, etagOf(archiving.version));
            toast({ tone: "success", title: "Marka arşivlendi", description: archiving.name });
            setArchiving(null);
            reload();
          }}
        />
      ) : null}
    </>
  );
}

const KNOWN = new Set(["name"]);

/**
 * Create (Idempotency-Key per attempt) or rename (If-Match) a brand. Also used inline from the product form; `onSaved`
 * receives the saved brand.
 */
export function BrandDialog({ brand, onClose, onSaved }: { brand: Brand | null; onClose: () => void; onSaved: (brand: Brand) => void }) {
  const uid = useId();
  const { api } = useSession();
  const { toast } = useToast();
  const [name, setName] = useState(brand?.name ?? "");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    const clientErrors = validateBrandName(name);
    setErrors(clientErrors);
    setSummary([]);
    if (Object.keys(clientErrors).length) return;
    const clean = cleanName(name);
    if (brand && clean === brand.name) return onClose();
    setSaving(true);
    try {
      const saved = brand ? await updateBrand(api, brand.id, { name: clean }, etagOf(brand.version)) : await createBrand(api, { name: clean }, keys.keyFor({ name: clean }));
      keys.reset();
      toast({ tone: "success", title: brand ? "Marka adı değiştirildi" : "Marka eklendi", description: saved.name });
      onSaved(saved);
    } catch (error) {
      setSaving(false);
      const problem = saveProblem(error, "name", KNOWN);
      setErrors(problem.fields);
      setSummary(problem.summary);
    }
  };

  const formId = `${useId()}-brand-form`;
  return (
    <Dialog
      open
      onClose={() => !saving && onClose()}
      title={brand ? `Markayı yeniden adlandır: ${brand.name}` : "Yeni marka"}
      size="sm"
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={formId} loading={saving}>
            {brand ? "Kaydet" : "Markayı ekle"}
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
        <Input
          id={`${uid}-brand-name`}
          label="Marka adı"
          required
          maxLength={BRAND_NAME_MAX}
          value={name}
          onChange={(e) => setName(e.target.value)}
          error={errors.name}
          autoComplete="off"
        />
      </form>
    </Dialog>
  );
}

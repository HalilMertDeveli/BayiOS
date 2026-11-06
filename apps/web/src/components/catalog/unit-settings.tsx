"use client";

import { useState, type FormEvent } from "react";
import { Archive, Pencil, Plus, Ruler } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { archiveUnit, createUnit, etagOf, listUnits, updateUnit, type Unit } from "@/lib/catalog/api";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { catalogPermissions } from "@/lib/catalog/permissions";
import { UNIT_CODE_MAX, UNIT_NAME_MAX, buildUnitCreate, buildUnitPatch, sortUnits, validateUnit, type UnitFormValues } from "@/lib/catalog/reference-form";
import { useSession } from "@/lib/session/session-context";
import { FormAlert } from "./form-alert";
import { ArchiveDialog, ArchivedToggle, ListError, ReferenceNav, formatUpdated, saveProblem, useReferenceList } from "./reference-shared";

/**
 * /urunler/birimler (#129): units of measure — code (fixed after creation), name and whether quantities may have
 * decimals (kg, metre). catalog.fields.manage (owner, admin); others read only.
 */
export function UnitSettings() {
  const { api, access } = useSession();
  const canManage = catalogPermissions(access).manageFields;
  const [includeArchived, setIncludeArchived] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [editing, setEditing] = useState<Unit | "new" | null>(null);
  const [archiving, setArchiving] = useState<Unit | null>(null);
  const { toast } = useToast();
  const key = JSON.stringify({ includeArchived, attempt });
  const current = useReferenceList<Unit>(key, (signal) => listUnits(api, signal, { includeArchived }));
  const reload = () => setAttempt((n) => n + 1);
  const rows = current?.status === "ready" ? sortUnits(current.items) : [];

  const columns: TableColumn<Unit>[] = [
    { key: "code", header: "Kod", width: "8rem", cell: (u) => <code className="text-body font-medium">{u.code}</code> },
    { key: "name", header: "Ad", cell: (u) => u.name },
    { key: "fraction", header: "Küsuratlı miktar", width: "10rem", cell: (u) => (u.allowsFraction ? "Evet (ör. 2,5)" : "Hayır (tam sayı)") },
    { key: "status", header: "Durum", width: "9rem", cell: (u) => (u.isArchived ? <Badge tone="warning">Arşivlenmiş</Badge> : <Badge tone="success">Aktif</Badge>) },
    { key: "updated", header: "Son değişiklik", width: "10rem", cell: (u) => formatUpdated(u.updatedAt, u.createdAt) },
    ...(canManage
      ? [
          {
            key: "actions",
            header: <span className="sr-only">İşlemler</span>,
            cell: (u: Unit) =>
              u.isArchived ? null : (
                <div className="flex justify-end gap-1">
                  <Button variant="ghost" size="sm" icon={<Pencil className="size-4" />} aria-label={`${u.name} birimini düzenle`} onClick={() => setEditing(u)}>
                    Düzenle
                  </Button>
                  <Button variant="ghost" size="sm" icon={<Archive className="size-4" />} aria-label={`${u.name} birimini arşivle`} onClick={() => setArchiving(u)}>
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
        title="Birimler"
        description="Ürün ve varyantların ölçü birimleri (adet, kg, metre…). Küsurat kabul eden birimlerde sipariş ve stok miktarı ondalıklı girilebilir."
        actions={
          canManage ? (
            <Button icon={<Plus className="size-4" />} onClick={() => setEditing("new")}>
              Yeni birim
            </Button>
          ) : null
        }
      />
      {!canManage ? (
        <FormAlert tone="info" title="Salt okunur görünüm">
          Birimleri yalnızca işletme sahibi ve yöneticiler değiştirebilir.
        </FormAlert>
      ) : null}
      <ArchivedToggle checked={includeArchived} onChange={setIncludeArchived} />

      {current?.status === "error" ? (
        <ListError title="Birimler yüklenemedi" message={current.message} onRetry={reload} />
      ) : (
        <Table
          caption="Birimler"
          columns={columns}
          rows={rows}
          getRowId={(u) => u.id}
          loading={!current}
          density="compact"
          empty={
            <EmptyState
              headingLevel="h2"
              icon={<Ruler className="size-6" />}
              title="Henüz birim yok"
              description={canManage ? "Ürün ekleyebilmek için en az bir birim gerekir (ör. ADET)." : "Bu işletmede tanımlı birim yok. Yöneticinizden birim eklemesini isteyin."}
              action={
                canManage ? (
                  <Button icon={<Plus className="size-4" />} onClick={() => setEditing("new")}>
                    Yeni birim
                  </Button>
                ) : undefined
              }
            />
          }
        />
      )}

      {editing ? (
        <UnitDialog
          key={editing === "new" ? "new" : editing.id}
          unit={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            reload();
          }}
        />
      ) : null}
      {archiving ? (
        <ArchiveDialog
          title="Birimi arşivle"
          name={`${archiving.name} (${archiving.code})`}
          effect={<p>Arşivlenen birim yeni ürün ve varyantlarda seçilemez. Bu birimi kullanan ürünler değişmez. Kod tekrar kullanılamaz.</p>}
          onClose={() => setArchiving(null)}
          onConfirm={async () => {
            await archiveUnit(api, archiving.id, etagOf(archiving.version));
            toast({ tone: "success", title: "Birim arşivlendi", description: archiving.name });
            setArchiving(null);
            reload();
          }}
        />
      ) : null}
    </>
  );
}

const KNOWN = new Set(["code", "name", "allowsFraction"]);

function UnitDialog({ unit, onClose, onSaved }: { unit: Unit | null; onClose: () => void; onSaved: () => void }) {
  const { api } = useSession();
  const { toast } = useToast();
  const [values, setValues] = useState<UnitFormValues>(() => ({ code: unit?.code ?? "", name: unit?.name ?? "", allowsFraction: unit?.allowsFraction ?? false }));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const set = <K extends keyof UnitFormValues>(k: K, v: UnitFormValues[K]) => setValues((x) => ({ ...x, [k]: v }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (saving) return;
    const clientErrors = validateUnit(values, unit ? "edit" : "create");
    setErrors(clientErrors);
    setSummary([]);
    if (Object.keys(clientErrors).length) return;
    try {
      if (unit) {
        const patch = buildUnitPatch(values, unit);
        if (Object.keys(patch).length === 0) return onClose();
        setSaving(true);
        await updateUnit(api, unit.id, patch, etagOf(unit.version));
        toast({ tone: "success", title: "Birim kaydedildi", description: values.name });
      } else {
        const body = buildUnitCreate(values);
        setSaving(true);
        const created = await createUnit(api, body, keys.keyFor(body));
        keys.reset();
        toast({ tone: "success", title: "Birim eklendi", description: `${created.name} (${created.code})` });
      }
      onSaved();
    } catch (error) {
      setSaving(false);
      const problem = saveProblem(error, "code", KNOWN);
      setErrors(problem.fields);
      setSummary(problem.summary);
    }
  };

  const formId = "unit-form";
  return (
    <Dialog
      open
      onClose={() => !saving && onClose()}
      title={unit ? `Birimi düzenle: ${unit.name}` : "Yeni birim"}
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Vazgeç
          </Button>
          <Button type="submit" form={formId} loading={saving}>
            {unit ? "Kaydet" : "Birimi ekle"}
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
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Input
            id="unit-code"
            label="Kod"
            required
            readOnly={!!unit}
            maxLength={UNIT_CODE_MAX}
            value={values.code}
            onChange={(e) => set("code", e.target.value)}
            error={errors.code}
            hint={unit ? "Kod oluşturulduktan sonra değişmez." : "Kısa ve boşluksuz, ör. adet, kg, m. Küçük harfe çevrilir."}
            autoComplete="off"
            spellCheck={false}
          />
          <Input
            id="unit-name"
            label="Birim adı"
            required
            maxLength={UNIT_NAME_MAX}
            value={values.name}
            onChange={(e) => set("name", e.target.value)}
            error={errors.name}
            hint="Listelerde görünen ad, ör. Adet, Kilogram."
            autoComplete="off"
          />
        </div>
        <CheckboxField
          label="Küsuratlı miktar kabul eder"
          hint="Açıksa sipariş ve stok miktarı ondalıklı girilebilir (ör. 2,5 kg). Kapalıysa yalnızca tam sayı."
          checked={values.allowsFraction}
          onChange={(v) => set("allowsFraction", v)}
        />
      </form>
    </Dialog>
  );
}

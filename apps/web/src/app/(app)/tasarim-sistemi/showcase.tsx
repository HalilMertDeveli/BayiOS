"use client";

import { useState } from "react";
import { Plus, Search } from "@/components/ui/icons";
import { Badge, OrderStatusBadge, orderStatusMeta, type OrderStatus } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn, type TableDensity } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";

// Illustrative rows for the component catalogue only — clearly fictional, never shown as business data.
type SampleRow = { id: string; sku: string; name: string; qty: number; status: OrderStatus };
const sampleRows: SampleRow[] = (Object.keys(orderStatusMeta) as OrderStatus[]).map((status, i) => ({
  id: `ornek-${i + 1}`,
  sku: `ORNEK-${String(i + 1).padStart(3, "0")}`,
  name: `Örnek ürün ${i + 1} (çğıİöşü)`,
  qty: (i + 1) * 12,
  status,
}));

const columns: TableColumn<SampleRow>[] = [
  { key: "sku", header: "SKU", cell: (r) => <span className="font-mono text-code">{r.sku}</span>, width: "9rem" },
  { key: "name", header: "Ürün", cell: (r) => r.name },
  { key: "qty", header: "Miktar", cell: (r) => r.qty.toLocaleString("tr-TR"), numeric: true, width: "7rem" },
  { key: "status", header: "Durum", cell: (r) => <OrderStatusBadge status={r.status} />, width: "11rem" },
];

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-4 rounded-xl border border-line bg-card p-4 shadow-e1 md:p-6">
      <h2 className="text-h2 text-fg">{title}</h2>
      {children}
    </section>
  );
}

export function Showcase() {
  const { toast } = useToast();
  const [dialogOpen, setDialogOpen] = useState(false);
  const [density, setDensity] = useState<TableDensity>("comfortable");
  const [tableState, setTableState] = useState<"data" | "loading" | "empty">("data");

  return (
    <div className="flex flex-col gap-6">
      <Section title="Buton">
        <div className="flex flex-wrap items-center gap-2">
          <Button icon={<Plus className="size-4" />}>Birincil</Button>
          <Button variant="secondary">İkincil</Button>
          <Button variant="ghost">Sade</Button>
          <Button variant="danger">Sil</Button>
          <Button loading>Kaydediliyor</Button>
          <Button disabled>Devre dışı</Button>
          <Button size="sm" variant="secondary">
            Küçük
          </Button>
          <Button size="lg">Büyük</Button>
        </div>
      </Section>

      <Section title="Form alanları">
        <div className="grid gap-4 md:grid-cols-2">
          <Input label="Ürün ara" placeholder="Ad, SKU veya barkod" prefix={<Search className="size-4" />} type="search" />
          <Input label="Birim fiyat" inputMode="decimal" suffix="₺" hint="Örnek: 1.234,56" />
          <Input
            label="Miktar"
            inputMode="numeric"
            defaultValue="0"
            error="Miktar sıfırdan büyük olmalı. Değeri artırın."
            required
          />
          <Select
            label="Depo"
            placeholder="Seçiniz"
            options={[
              { value: "merkez", label: "Merkez depo (örnek)" },
              { value: "sube", label: "Şube depo (örnek)" },
            ]}
          />
        </div>
      </Section>

      <Section title="Rozetler ve durumlar">
        <div className="flex flex-wrap gap-2">
          {(Object.keys(orderStatusMeta) as OrderStatus[]).map((s) => (
            <OrderStatusBadge key={s} status={s} />
          ))}
        </div>
        <div className="flex flex-wrap gap-2">
          <Badge tone="success">Satılabilir</Badge>
          <Badge tone="warning">Az kaldı</Badge>
          <Badge tone="danger">Tükendi</Badge>
          <Badge tone="info">Ayrıldı</Badge>
          <Badge tone="ai">Öneri</Badge>
        </div>
      </Section>

      <Section title="Tablo">
        <div className="flex flex-wrap items-end gap-4">
          <Select
            label="Görünüm"
            value={density}
            onChange={(e) => setDensity(e.target.value as TableDensity)}
            options={[
              { value: "comfortable", label: "Rahat" },
              { value: "compact", label: "Yoğun" },
            ]}
          />
          <Select
            label="Durum"
            value={tableState}
            onChange={(e) => setTableState(e.target.value as typeof tableState)}
            options={[
              { value: "data", label: "Veri" },
              { value: "loading", label: "Yükleniyor" },
              { value: "empty", label: "Boş" },
            ]}
          />
        </div>
        <Table
          caption="Örnek tablo (kurgusal veri)"
          showCaption
          columns={columns}
          rows={tableState === "empty" ? [] : sampleRows}
          getRowId={(r) => r.id}
          density={density}
          loading={tableState === "loading"}
          maxHeight="20rem"
        />
      </Section>

      <Section title="Bildirim ve diyalog">
        <div className="flex flex-wrap gap-2">
          <Button
            variant="secondary"
            onClick={() => toast({ tone: "success", title: "Kaydedildi", description: "Değişiklikleriniz kaydedildi." })}
          >
            Bildirim göster
          </Button>
          <Button
            variant="secondary"
            onClick={() =>
              toast({
                tone: "info",
                title: "Satır silindi",
                action: { label: "Geri al", onAction: () => toast({ title: "Geri alındı" }) },
              })
            }
          >
            Geri alınabilir bildirim
          </Button>
          <Button variant="danger" onClick={() => setDialogOpen(true)}>
            Diyaloğu aç
          </Button>
        </div>
        <Dialog
          open={dialogOpen}
          onClose={() => setDialogOpen(false)}
          role="alertdialog"
          dismissOnBackdrop={false}
          title="Siparişi iptal et"
          description="Bu işlem geri alınamaz. Ayrılan stok serbest bırakılır."
          footer={
            <>
              <Button variant="secondary" onClick={() => setDialogOpen(false)}>
                Vazgeç
              </Button>
              <Button variant="danger" onClick={() => setDialogOpen(false)}>
                Siparişi iptal et
              </Button>
            </>
          }
        />
      </Section>

      <Section title="Boş durum">
        <EmptyState
          title="Henüz ürün yok"
          description="İlk ürününüzü ekleyin veya Excel'den içe aktarın."
          action={<Button icon={<Plus className="size-4" />}>Ürün ekle</Button>}
        />
      </Section>
    </div>
  );
}

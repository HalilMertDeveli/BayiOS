"use client";

import Link from "next/link";
import { CircleAlert, ClipboardCheck, EyeOff, Lock, Plus, RefreshCw } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { formatDateTime } from "@/lib/inventory/labels";
import { formatQuantity } from "@/lib/inventory/quantity";
import { NEW_COUNT_HREF, countHref, documentListQuery, listCounts, type CountSummary } from "@/lib/stock-documents/api";
import { COUNT_FILTERS, countScopeLabels, type CountFilter } from "@/lib/stock-documents/labels";
import { parseCountListQuery, toCountListSearch } from "@/lib/stock-documents/list-query";
import { countPermissions } from "@/lib/stock-documents/permissions";
import { useSession } from "@/lib/session/session-context";
import { useUrlQuery } from "@/lib/use-url-query";
import { useWarehouses } from "@/components/inventory/use-inventory-data";
import { CountStatusBadge } from "./badges";
import { useDocumentList } from "./use-document-list";

/** /depo/sayimlar: stock counts (W10), newest first, keyset paged; filters in the URL. */
export function CountList() {
  const { api, access } = useSession();
  const permissions = countPermissions(access);
  const { query, latest, navigate } = useUrlQuery(parseCountListQuery, toCountListSearch);
  const { state: warehouses } = useWarehouses();
  const statuses = COUNT_FILTERS[query.filter].statuses;
  const { data, more, loadMore, reload } = useDocumentList<CountSummary>(
    toCountListSearch(query),
    (cursor, signal) => listCounts(api, documentListQuery({ statuses, warehouseId: query.warehouseId || null }, cursor), signal),
    permissions.read,
  );

  if (!permissions.read) {
    return (
      <>
        <PageHeader title="Sayımlar" />
        <section aria-label="Sayımlar" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<Lock className="size-6" />} title="Sayımları görme yetkiniz yok" description="Rolünüz stok sayımına izin vermiyor. Gerekirse işletme yöneticinizle görüşün." />
        </section>
      </>
    );
  }

  const newCount = permissions.manage ? (
    <Link href={NEW_COUNT_HREF} className={buttonClasses("primary", "lg")}>
      <Plus className="size-5" aria-hidden="true" />
      Yeni sayım
    </Link>
  ) : null;

  return (
    <>
      <PageHeader
        title="Sayımlar"
        description="Sayım başladığında kapsamdaki stok anlık görüntü olarak alınır; fark, onaylanınca sayım düzeltmesi olarak stoğa işlenir."
        actions={
          <span className="flex flex-wrap gap-2">
            <Button size="lg" variant="secondary" icon={<RefreshCw className="size-5" />} loading={data.status === "loading"} onClick={reload}>
              Yenile
            </Button>
            {newCount}
          </span>
        }
      />
      <section aria-label="Filtreler" className="grid grid-cols-1 gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 sm:grid-cols-2">
        <Select
          label="Durum"
          size="lg"
          value={query.filter}
          onChange={(e) => navigate({ ...latest(), filter: e.target.value as CountFilter })}
          options={Object.entries(COUNT_FILTERS).map(([value, f]) => ({ value, label: f.label }))}
        />
        <Select
          label="Depo"
          size="lg"
          placeholder="Tüm depolar"
          value={query.warehouseId}
          disabled={warehouses.status !== "ready"}
          onChange={(e) => navigate({ ...latest(), warehouseId: e.target.value })}
          options={warehouses.status === "ready" ? warehouses.data.map((w) => ({ value: w.id, label: `${w.name} (${w.code})` })) : []}
        />
      </section>

      {data.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Sayımlar yükleniyor…
        </p>
      ) : data.status === "error" ? (
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState icon={<CircleAlert className="size-6" />} title="Sayımlar yüklenemedi" description={data.message} action={<Button size="lg" onClick={reload}>Tekrar dene</Button>} />
        </div>
      ) : data.items.length === 0 ? (
        <section aria-label="Sayımlar" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<ClipboardCheck className="size-6" />}
            title="Bu filtrede sayım yok"
            description={permissions.manage ? "Bir depoda, seçili raflarda veya ürünlerde sayım başlatın." : "Size açılan sayımlar burada görünür."}
            action={newCount ?? undefined}
          />
        </section>
      ) : (
        <ul aria-label="Sayımlar" className="flex flex-col gap-3">
          {data.items.map((c) => (
            <li key={c.id} data-testid={`count-${c.number}`} className="flex flex-col gap-2 rounded-lg border border-line bg-card p-4 shadow-e1">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <h2 className="text-h3 text-fg">
                  <Link href={countHref(c.id)} className="underline-offset-2 hover:underline">
                    {c.number}
                  </Link>
                </h2>
                <span className="flex flex-wrap gap-1">
                  <CountStatusBadge status={c.status} />
                  {c.isBlind ? (
                    <Badge tone="purple" icon={<EyeOff className="size-3.5" />}>
                      Kör sayım
                    </Badge>
                  ) : null}
                </span>
              </div>
              <p className="text-body text-fg-secondary">
                {c.warehouseName ?? "Depo"} · {countScopeLabels[c.scope] ?? c.scope} · {formatQuantity(c.countedLineCount)} / {formatQuantity(c.lineCount)} satır sayıldı
              </p>
              <p className="text-caption text-fg-muted">
                Anlık görüntü {formatDateTime(c.snapshotAt)}
                {c.submittedAt ? ` · Bitirildi ${formatDateTime(c.submittedAt)}` : ""}
                {c.appliedAt ? ` · Uygulandı ${formatDateTime(c.appliedAt)}` : ""}
              </p>
            </li>
          ))}
        </ul>
      )}
      {more.error ? (
        <p role="alert" className="text-body text-danger-fg">
          {more.error}
        </p>
      ) : null}
      {data.status === "ready" && data.hasMore ? (
        <Button size="lg" variant="secondary" className="self-center" loading={more.loading} onClick={() => void loadMore()}>
          Daha fazla yükle
        </Button>
      ) : null}
    </>
  );
}

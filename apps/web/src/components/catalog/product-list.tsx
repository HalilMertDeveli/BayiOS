"use client";

import { useEffect, useMemo, useState, type FormEvent } from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { CircleAlert, Package, Plus, Rows3, Rows4, Search, SearchX, SlidersHorizontal, Tags } from "lucide-react";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { listProducts, productHref, type CustomField, type Product } from "@/lib/catalog/api";
import { catalogErrorMessage } from "@/lib/catalog/errors";
import {
  STATUS_FILTERS,
  activeFilterCount,
  categoryOptions,
  emptyQuery,
  filterableFields,
  normalizeNumericFilter,
  parseListQuery,
  sortLabels,
  statusFilterLabels,
  toApiQuery,
  toSearchString,
  type ProductListQuery,
  type SortOption,
  type StatusFilter,
} from "@/lib/catalog/list-query";
import { catalogPermissions } from "@/lib/catalog/permissions";
import { useSession } from "@/lib/session/session-context";
import { usePersistentBoolean } from "@/lib/use-persistent-boolean";
import { ProductStatusBadge } from "./product-status-badge";
import { useCatalogReference } from "./use-catalog-reference";

const SEARCH_DEBOUNCE_MS = 300;

type ListData =
  | { key: string; status: "ready"; items: Product[]; nextCursor: string | null; hasMore: boolean }
  | { key: string; status: "error"; message: string };

type MoreState = { status: "idle" } | { status: "loading" } | { status: "error"; message: string };

/** First non-archived variant (or the first one) — what the list shows as the product's SKU/barcode. */
function primaryVariant(product: Product) {
  return product.variants.find((v) => v.status !== "archived") ?? product.variants[0];
}

/**
 * W04 product list: server-side search (debounced, in the URL), filters (category incl. subcategories, brand,
 * status, filterable custom fields), keyset paging with "Daha fazla yükle". Only one page at a time is fetched.
 */
export function ProductList() {
  const { api, access } = useSession();
  const permissions = catalogPermissions(access);
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const query = useMemo(() => parseListQuery(searchParams), [searchParams]);
  const apiKey = toApiQuery(query);
  const { state: reference } = useCatalogReference();
  const [dense, setDense] = usePersistentBoolean("bayios.urunler.dense");

  const navigate = (next: ProductListQuery) => {
    const search = toSearchString(next);
    router.replace(search ? `${pathname}?${search}` : pathname, { scroll: false });
  };

  // ---- Search box: local text, pushed to the URL after a pause. ----
  const [searchText, setSearchText] = useState(query.q);
  const [pushedQ, setPushedQ] = useState(query.q);
  const [seenQ, setSeenQ] = useState(query.q);
  if (query.q !== seenQ) {
    // The URL changed. If it was not our own debounced push (back/forward, "clear filters"), follow it.
    setSeenQ(query.q);
    if (query.q !== pushedQ) {
      setPushedQ(query.q);
      setSearchText(query.q);
    }
  }
  useEffect(() => {
    const text = searchText.trim();
    if (text === query.q) return;
    const timer = setTimeout(() => {
      setPushedQ(text);
      const search = toSearchString({ ...query, q: text });
      router.replace(search ? `${pathname}?${search}` : pathname, { scroll: false });
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [searchText, query, pathname, router]);

  // ---- Data: first page per query; more pages appended with the cursor. ----
  const [data, setData] = useState<ListData | null>(null);
  const [more, setMore] = useState<MoreState>({ status: "idle" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    listProducts(api, apiKey, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        setMore({ status: "idle" });
        setData({ key: apiKey, status: "ready", items: page.items, nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setData({ key: apiKey, status: "error", message: catalogErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, apiKey, attempt]);

  const current = data?.key === apiKey ? data : null;
  const loading = current === null;

  const loadMore = async () => {
    if (current?.status !== "ready" || !current.nextCursor) return;
    setMore({ status: "loading" });
    try {
      const page = await listProducts(api, toApiQuery(query, current.nextCursor));
      setData((prev) =>
        prev && prev.key === current.key && prev.status === "ready"
          ? {
              ...prev,
              items: [...prev.items, ...page.items.filter((p) => !prev.items.some((x) => x.id === p.id))],
              nextCursor: page.page.nextCursor,
              hasMore: page.page.hasMore,
            }
          : prev,
      );
      setMore({ status: "idle" });
    } catch (error) {
      setMore({ status: "error", message: catalogErrorMessage(error) });
    }
  };

  const ready = reference.status === "ready" ? reference : null;
  const categoryNames = useMemo(() => new Map(ready?.categories.map((c) => [c.id, c.name]) ?? []), [ready]);
  const brandNames = useMemo(() => new Map(ready?.brands.map((b) => [b.id, b.name]) ?? []), [ready]);
  const filterCount = activeFilterCount(query);
  const hasCriteria = filterCount > 0 || query.q !== "";

  const columns: TableColumn<Product>[] = [
    {
      key: "name",
      header: "Ürün",
      cell: (p) => (
        <Link href={productHref(p.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
          {p.name}
        </Link>
      ),
    },
    {
      key: "sku",
      header: "SKU",
      cell: (p) => {
        const v = primaryVariant(p);
        const others = p.variants.filter((x) => x.status !== "archived").length - 1;
        return (
          <span className="font-mono text-caption">
            {v?.sku ?? "—"}
            {others > 0 ? <span className="ml-1 font-sans text-fg-muted">+{others} varyant</span> : null}
          </span>
        );
      },
    },
    { key: "barcode", header: "Barkod", cell: (p) => <span className="font-mono text-caption">{primaryVariant(p)?.barcode ?? "—"}</span> },
    { key: "category", header: "Kategori", cell: (p) => (p.categoryId ? (categoryNames.get(p.categoryId) ?? "—") : "—") },
    { key: "brand", header: "Marka", cell: (p) => (p.brandId ? (brandNames.get(p.brandId) ?? "—") : "—") },
    { key: "tax", header: "KDV", numeric: true, width: "5rem", cell: (p) => `%${String(p.taxRate).replace(".", ",")}` },
    { key: "status", header: "Durum", width: "8rem", cell: (p) => <ProductStatusBadge status={p.status} /> },
  ];

  const rows = current?.status === "ready" ? current.items : [];

  const emptyView = hasCriteria ? (
    <EmptyState
      headingLevel="h3"
      icon={<SearchX className="size-6" />}
      title="Eşleşen ürün bulunamadı"
      description="Aramanızı veya filtreleri değiştirip tekrar deneyin."
      action={
        <Button variant="secondary" onClick={() => navigate(emptyQuery)}>
          Aramayı ve filtreleri temizle
        </Button>
      }
    />
  ) : (
    <EmptyState
      headingLevel="h3"
      icon={<Package className="size-6" />}
      title="Henüz ürün yok"
      description={
        permissions.write
          ? "İlk ürününüzü ekleyerek kataloğu oluşturmaya başlayın."
          : "Bu işletmede henüz ürün tanımlanmamış."
      }
      action={
        permissions.write ? (
          <Link href="/urunler/yeni" className={buttonClasses("primary")}>
            <Plus className="size-4" aria-hidden="true" />
            İlk ürünü ekle
          </Link>
        ) : undefined
      }
    />
  );

  return (
    <>
      <PageHeader
        title="Ürünler"
        description="Ürün kataloğu: ad, SKU veya barkodla arayın; kategori, marka ve özel alanlarla filtreleyin."
        actions={
          <>
            <Link href="/urunler/markalar" className={buttonClasses("secondary")}>
              <Tags className="size-4" aria-hidden="true" />
              Marka, kategori ve birimler
            </Link>
            {permissions.write ? (
              <Link href="/urunler/yeni" className={buttonClasses("primary")}>
                <Plus className="size-4" aria-hidden="true" />
                Yeni ürün
              </Link>
            ) : null}
          </>
        }
      />

      <section aria-label="Arama ve filtreler" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
        <form role="search" onSubmit={(e) => e.preventDefault()} className="flex flex-col gap-3">
          <Input
            label="Ürün ara"
            type="search"
            placeholder="Ürün adı, SKU veya barkod"
            prefix={<Search className="size-4" />}
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            autoComplete="off"
            hint="Yazmayı bıraktığınızda arama yapılır."
          />
        </form>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Select
            label="Kategori"
            hint="Alt kategoriler dahil"
            placeholder="Tüm kategoriler"
            options={ready ? categoryOptions(ready.categories) : []}
            value={query.categoryId}
            disabled={!ready}
            onChange={(e) => navigate({ ...query, categoryId: e.target.value })}
          />
          <Select
            label="Marka"
            placeholder="Tüm markalar"
            options={ready ? [...ready.brands].sort((a, b) => a.name.localeCompare(b.name, "tr")).map((b) => ({ value: b.id, label: b.name })) : []}
            value={query.brandId}
            disabled={!ready}
            onChange={(e) => navigate({ ...query, brandId: e.target.value })}
          />
          <Select
            label="Durum"
            options={STATUS_FILTERS.map((s) => ({ value: s, label: statusFilterLabels[s] }))}
            value={query.status}
            onChange={(e) => navigate({ ...query, status: e.target.value as StatusFilter })}
          />
          <Select
            label="Sıralama"
            options={(Object.keys(sortLabels) as SortOption[]).map((s) => ({ value: s, label: sortLabels[s] }))}
            value={query.sort}
            onChange={(e) => navigate({ ...query, sort: e.target.value as SortOption })}
          />
        </div>
        {ready && filterableFields(ready.fields).length > 0 ? (
          <AttributeFilters
            key={JSON.stringify(query.attributes)}
            fields={filterableFields(ready.fields)}
            applied={query.attributes}
            onApply={(attributes) => navigate({ ...query, attributes })}
          />
        ) : null}
        {reference.status === "error" ? (
          <p className="text-caption text-danger-fg">Filtre seçenekleri yüklenemedi: {reference.message}</p>
        ) : null}
        {hasCriteria ? (
          <div>
            <Button variant="ghost" size="sm" onClick={() => navigate(emptyQuery)}>
              Aramayı ve filtreleri temizle
            </Button>
          </div>
        ) : null}
      </section>

      <section aria-labelledby="product-results" className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="product-results" className="text-h3 text-fg">
            Ürün listesi
          </h2>
          <div className="flex items-center gap-3">
            <p role="status" className="text-caption text-fg-muted">
              {loading
                ? "Yükleniyor…"
                : current.status === "ready"
                  ? `${current.items.length} ürün gösteriliyor${current.hasMore ? " (devamı var)" : ""}`
                  : ""}
            </p>
            <Button
              variant="secondary"
              size="sm"
              aria-pressed={dense}
              className="hidden md:inline-flex"
              icon={dense ? <Rows4 className="size-4" /> : <Rows3 className="size-4" />}
              onClick={() => setDense(!dense)}
            >
              Sık görünüm
            </Button>
          </div>
        </div>

        {current?.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState
              headingLevel="h3"
              icon={<CircleAlert className="size-6" />}
              title="Ürünler yüklenemedi"
              description={current.message}
              action={
                <Button
                  onClick={() => {
                    setData(null);
                    setAttempt((n) => n + 1);
                  }}
                >
                  Tekrar dene
                </Button>
              }
            />
          </div>
        ) : (
          <>
            <div className="hidden md:block">
              <Table
                caption="Ürünler"
                columns={columns}
                rows={rows}
                getRowId={(p) => p.id}
                density={dense ? "compact" : "comfortable"}
                loading={loading}
                empty={emptyView}
                maxHeight="70vh"
              />
            </div>
            <div className="md:hidden">
              {loading ? (
                <p className="py-6 text-center text-body text-fg-muted">Yükleniyor…</p>
              ) : rows.length === 0 ? (
                <div className="rounded-lg border border-line bg-card">{emptyView}</div>
              ) : (
                <ul aria-label="Ürünler" className="flex flex-col gap-2">
                  {rows.map((p) => {
                    const v = primaryVariant(p);
                    return (
                      <li key={p.id} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                        <div className="flex items-start justify-between gap-2">
                          <Link href={productHref(p.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
                            {p.name}
                          </Link>
                          <ProductStatusBadge status={p.status} />
                        </div>
                        <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-caption">
                          <dt className="text-fg-muted">SKU</dt>
                          <dd className="font-mono text-fg">
                            {v?.sku ?? "—"}
                            {p.variants.length > 1 ? <span className="ml-1 font-sans text-fg-muted">({p.variants.length} varyant)</span> : null}
                          </dd>
                          {v?.barcode ? (
                            <>
                              <dt className="text-fg-muted">Barkod</dt>
                              <dd className="font-mono text-fg">{v.barcode}</dd>
                            </>
                          ) : null}
                          {p.categoryId && categoryNames.get(p.categoryId) ? (
                            <>
                              <dt className="text-fg-muted">Kategori</dt>
                              <dd className="text-fg">{categoryNames.get(p.categoryId)}</dd>
                            </>
                          ) : null}
                          {p.brandId && brandNames.get(p.brandId) ? (
                            <>
                              <dt className="text-fg-muted">Marka</dt>
                              <dd className="text-fg">{brandNames.get(p.brandId)}</dd>
                            </>
                          ) : null}
                        </dl>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>
            {current?.status === "ready" && current.hasMore ? (
              <div className="flex flex-col items-center gap-2">
                <Button variant="secondary" loading={more.status === "loading"} onClick={loadMore}>
                  Daha fazla yükle
                </Button>
                {more.status === "error" ? (
                  <p role="alert" className="text-caption text-danger-fg">
                    {more.message}
                  </p>
                ) : null}
              </div>
            ) : null}
          </>
        )}
      </section>
    </>
  );
}

function fieldSuffix(field: CustomField) {
  return field.entityType === "variant" ? " (varyant)" : "";
}

/**
 * Inputs for `attr.*` filters of filterable custom fields. Kept as a draft and applied together with
 * "Filtreleri uygula", so typing a number does not fire a request per keystroke.
 */
function AttributeFilters({
  fields,
  applied,
  onApply,
}: {
  fields: CustomField[];
  applied: Record<string, string>;
  onApply: (attributes: Record<string, string>) => void;
}) {
  const appliedCount = Object.keys(applied).length;
  const [open, setOpen] = useState(appliedCount > 0);
  const [draft, setDraft] = useState<Record<string, string>>(applied);
  const set = (key: string, value: string) => setDraft((d) => ({ ...d, [key]: value }));

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const numeric = new Set(
      fields.filter((f) => f.dataType === "number" || f.dataType === "integer" || f.dataType === "range").map((f) => `attr.${f.key}`),
    );
    for (const [k, v] of Object.entries(draft)) {
      if (!v.trim()) continue;
      next[k] = numeric.has(k.replace(/\.(min|max)$/, "")) ? normalizeNumericFilter(v) : v.trim();
    }
    onApply(next);
  };

  return (
    <div className="flex flex-col gap-3">
      <div>
        <Button
          variant="secondary"
          size="sm"
          aria-expanded={open}
          aria-controls="attribute-filters"
          icon={<SlidersHorizontal className="size-4" />}
          onClick={() => setOpen(!open)}
        >
          Özel alan filtreleri{appliedCount > 0 ? ` (${appliedCount})` : ""}
        </Button>
      </div>
      {open ? (
        <form id="attribute-filters" onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-line p-3">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {fields.map((field) => {
              const key = `attr.${field.key}`;
              const label = `${field.label}${fieldSuffix(field)}`;
              switch (field.dataType) {
                case "enum":
                case "multi_enum":
                  return (
                    <Select
                      key={key}
                      label={label}
                      placeholder="Tümü"
                      options={field.options.map((o) => ({ value: o, label: o }))}
                      value={draft[key] ?? ""}
                      onChange={(e) => set(key, e.target.value)}
                    />
                  );
                case "boolean":
                  return (
                    <Select
                      key={key}
                      label={label}
                      placeholder="Tümü"
                      options={[
                        { value: "true", label: "Evet" },
                        { value: "false", label: "Hayır" },
                      ]}
                      value={draft[key] ?? ""}
                      onChange={(e) => set(key, e.target.value)}
                    />
                  );
                case "number":
                case "integer":
                  return (
                    <fieldset key={key} className="flex flex-col gap-1.5">
                      <legend className="mb-1.5 text-body font-medium text-fg">
                        {label}
                        {field.unit ? <span className="font-normal text-fg-muted"> ({field.unit})</span> : null}
                      </legend>
                      <div className="grid grid-cols-2 gap-2">
                        <Input
                          label={`${field.label} en az`}
                          hideLabel
                          placeholder="En az"
                          inputMode="decimal"
                          value={draft[`${key}.min`] ?? ""}
                          onChange={(e) => set(`${key}.min`, e.target.value)}
                        />
                        <Input
                          label={`${field.label} en çok`}
                          hideLabel
                          placeholder="En çok"
                          inputMode="decimal"
                          value={draft[`${key}.max`] ?? ""}
                          onChange={(e) => set(`${key}.max`, e.target.value)}
                        />
                      </div>
                    </fieldset>
                  );
                case "range":
                  return (
                    <Input
                      key={key}
                      label={label}
                      hint="Aralığın içinde kalan değer"
                      inputMode="decimal"
                      suffix={field.unit ? <span className="text-caption">{field.unit}</span> : undefined}
                      value={draft[key] ?? ""}
                      onChange={(e) => set(key, e.target.value)}
                    />
                  );
                case "date":
                  return <Input key={key} label={label} type="date" value={draft[key] ?? ""} onChange={(e) => set(key, e.target.value)} />;
                default:
                  return (
                    <Input
                      key={key}
                      label={label}
                      hint="Tam eşleşme"
                      value={draft[key] ?? ""}
                      onChange={(e) => set(key, e.target.value)}
                    />
                  );
              }
            })}
          </div>
          <div className="flex flex-wrap gap-2">
            <Button type="submit" size="sm">
              Filtreleri uygula
            </Button>
            {appliedCount > 0 ? (
              <Button variant="ghost" size="sm" onClick={() => onApply({})}>
                Özel alan filtrelerini kaldır
              </Button>
            ) : null}
          </div>
        </form>
      ) : null}
    </div>
  );
}

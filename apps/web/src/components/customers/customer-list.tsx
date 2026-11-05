"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { CircleAlert, Plus, Search, SearchX, Users, UsersRound } from "lucide-react";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { DEALER_GROUPS_HREF, NEW_CUSTOMER_HREF, customerHref, listCustomers, type Customer } from "@/lib/customers/api";
import { customerErrorMessage } from "@/lib/customers/errors";
import {
  CUSTOMER_STATUS_FILTERS,
  customerStatusFilterLabels,
  emptyCustomerQuery,
  hasCustomerCriteria,
  parseCustomerQuery,
  toCustomerApiQuery,
  toCustomerSearchString,
  type CustomerListQuery,
  type CustomerStatusFilter,
} from "@/lib/customers/list-query";
import { customerPermissions } from "@/lib/customers/permissions";
import { useSession } from "@/lib/session/session-context";
import { CustomerStatusBadge } from "./customer-status-badge";
import { useCustomerReference } from "./use-customer-reference";

const SEARCH_DEBOUNCE_MS = 300;

type ListData =
  | { key: string; status: "ready"; items: Customer[]; nextCursor: string | null; hasMore: boolean }
  | { key: string; status: "error"; message: string };

type MoreState = { status: "idle" } | { status: "loading" } | { status: "error"; message: string };

/**
 * W17 customer list: server-side search (name/code contains; exact tax number, phone or WhatsApp — CUS-05), status
 * and dealer group filters in the URL, keyset paging with "Daha fazla yükle". Tax numbers are shown exactly as the
 * API returns them: 11-digit (TCKN) numbers arrive masked in lists (data-policy §2).
 */
export function CustomerList() {
  const { api, access } = useSession();
  const permissions = customerPermissions(access);
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const query = useMemo(() => parseCustomerQuery(searchParams), [searchParams]);
  const apiKey = toCustomerApiQuery(query);
  const { state: reference } = useCustomerReference();

  const navigate = (next: CustomerListQuery) => {
    const search = toCustomerSearchString(next);
    router.replace(search ? `${pathname}?${search}` : pathname, { scroll: false });
  };

  // ---- Search box: local text, pushed to the URL after a pause (same pattern as the product list). ----
  const [searchText, setSearchText] = useState(query.q);
  const [pushedQ, setPushedQ] = useState(query.q);
  const [seenQ, setSeenQ] = useState(query.q);
  if (query.q !== seenQ) {
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
      const search = toCustomerSearchString({ ...query, q: text });
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
    listCustomers(api, apiKey, controller.signal)
      .then((page) => {
        if (controller.signal.aborted) return;
        setMore({ status: "idle" });
        setData({ key: apiKey, status: "ready", items: page.items, nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setData({ key: apiKey, status: "error", message: customerErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, apiKey, attempt]);

  const current = data?.key === apiKey ? data : null;
  const loading = current === null;

  const loadMore = async () => {
    if (current?.status !== "ready" || !current.nextCursor) return;
    setMore({ status: "loading" });
    try {
      const page = await listCustomers(api, toCustomerApiQuery(query, current.nextCursor));
      setData((prev) =>
        prev && prev.key === current.key && prev.status === "ready"
          ? {
              ...prev,
              items: [...prev.items, ...page.items.filter((c) => !prev.items.some((x) => x.id === c.id))],
              nextCursor: page.page.nextCursor,
              hasMore: page.page.hasMore,
            }
          : prev,
      );
      setMore({ status: "idle" });
    } catch (error) {
      setMore({ status: "error", message: customerErrorMessage(error) });
    }
  };

  const groups = useMemo(() => (reference.status === "ready" ? reference.groups : []), [reference]);
  const groupNames = useMemo(() => new Map(groups.map((g) => [g.id, g.name])), [groups]);
  const hasCriteria = hasCustomerCriteria(query);
  const rows = current?.status === "ready" ? current.items : [];

  const columns: TableColumn<Customer>[] = [
    { key: "code", header: "Kod", width: "8rem", cell: (c) => <span className="font-mono text-caption">{c.code}</span> },
    {
      key: "name",
      header: "Müşteri",
      cell: (c) => (
        <span className="flex flex-col">
          <Link href={customerHref(c.id)} className="font-medium text-brand-fg underline-offset-2 hover:underline">
            {c.name}
          </Link>
          {c.contactName ? <span className="text-caption text-fg-muted">{c.contactName}</span> : null}
        </span>
      ),
    },
    { key: "tax", header: "Vergi no", cell: (c) => <span className="font-mono text-caption">{c.taxNumber ?? "—"}</span> },
    { key: "phone", header: "Telefon", cell: (c) => c.phone ?? "—" },
    { key: "group", header: "Bayi grubu", cell: (c) => (c.dealerGroupId ? (groupNames.get(c.dealerGroupId) ?? "—") : "—") },
    { key: "status", header: "Durum", width: "9rem", cell: (c) => <CustomerStatusBadge status={c.status} anonymized={c.anonymizedAt !== null} /> },
  ];

  const emptyView = hasCriteria ? (
    <EmptyState
      headingLevel="h3"
      icon={<SearchX className="size-6" />}
      title="Eşleşen müşteri bulunamadı"
      description="Ad veya kodun bir kısmını ya da tam vergi no / telefon / WhatsApp numarasını yazın; filtreleri değiştirip tekrar deneyin."
      action={
        <Button variant="secondary" onClick={() => navigate(emptyCustomerQuery)}>
          Aramayı ve filtreleri temizle
        </Button>
      }
    />
  ) : (
    <EmptyState
      headingLevel="h3"
      icon={<Users className="size-6" />}
      title="Henüz müşteri yok"
      description={permissions.write ? "İlk müşterinizi ekleyin; adres, bayi grubu ve fiyat listesini sonra da atayabilirsiniz." : "Bu işletmede henüz müşteri kaydı yok."}
      action={
        permissions.write ? (
          <Link href={NEW_CUSTOMER_HREF} className={buttonClasses("primary")}>
            <Plus className="size-4" aria-hidden="true" />
            İlk müşteriyi ekle
          </Link>
        ) : undefined
      }
    />
  );

  return (
    <>
      <PageHeader
        title="Müşteriler"
        description="Müşteri (cari) kayıtları, adresleri, bayi grubu ve fiyat listesi ataması."
        actions={
          <>
            <Link href={DEALER_GROUPS_HREF} className={buttonClasses("secondary")}>
              <UsersRound className="size-4" aria-hidden="true" />
              Bayi grupları
            </Link>
            {permissions.write ? (
              <Link href={NEW_CUSTOMER_HREF} className={buttonClasses("primary")}>
                <Plus className="size-4" aria-hidden="true" />
                Yeni müşteri
              </Link>
            ) : null}
          </>
        }
      />

      <section aria-label="Arama ve filtreler" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
        <form role="search" onSubmit={(e) => e.preventDefault()}>
          <Input
            label="Müşteri ara"
            type="search"
            placeholder="Ad, kod, vergi no, telefon veya WhatsApp"
            prefix={<Search className="size-4" />}
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            autoComplete="off"
            hint="Ad ve kodda içinde geçen metin; vergi no, telefon ve WhatsApp'ta tam numara aranır."
          />
        </form>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Select
            label="Durum"
            options={CUSTOMER_STATUS_FILTERS.map((s) => ({ value: s, label: customerStatusFilterLabels[s] }))}
            value={query.status}
            onChange={(e) => navigate({ ...query, status: e.target.value as CustomerStatusFilter })}
          />
          <Select
            label="Bayi grubu"
            placeholder="Tüm gruplar"
            options={[...groups].sort((a, b) => a.name.localeCompare(b.name, "tr")).map((g) => ({ value: g.id, label: g.status === "archived" ? `${g.name} (arşivlenmiş)` : g.name }))}
            value={query.group}
            disabled={reference.status !== "ready"}
            onChange={(e) => navigate({ ...query, group: e.target.value })}
          />
        </div>
        {reference.status === "error" ? <p className="text-caption text-danger-fg">Bayi grupları yüklenemedi: {reference.message}</p> : null}
        {hasCriteria ? (
          <div>
            <Button variant="ghost" size="sm" onClick={() => navigate(emptyCustomerQuery)}>
              Aramayı ve filtreleri temizle
            </Button>
          </div>
        ) : null}
      </section>

      <section aria-labelledby="customer-results" className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="customer-results" className="text-h3 text-fg">
            Müşteri listesi
          </h2>
          <p role="status" className="text-caption text-fg-muted">
            {loading ? "Yükleniyor…" : current.status === "ready" ? `${current.items.length} müşteri gösteriliyor${current.hasMore ? " (devamı var)" : ""}` : ""}
          </p>
        </div>

        {current?.status === "error" ? (
          <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
            <EmptyState
              headingLevel="h3"
              icon={<CircleAlert className="size-6" />}
              title="Müşteriler yüklenemedi"
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
              <Table caption="Müşteriler" columns={columns} rows={rows} getRowId={(c) => c.id} loading={loading} empty={emptyView} maxHeight="70vh" />
            </div>
            <div className="md:hidden">
              {loading ? (
                <p className="py-6 text-center text-body text-fg-muted">Yükleniyor…</p>
              ) : rows.length === 0 ? (
                <div className="rounded-lg border border-line bg-card">{emptyView}</div>
              ) : (
                <ul aria-label="Müşteriler" className="flex flex-col gap-2">
                  {rows.map((c) => (
                    <li key={c.id} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                      <div className="flex items-start justify-between gap-2">
                        <Link href={customerHref(c.id)} className="min-h-11 py-2 font-medium text-brand-fg underline-offset-2 hover:underline">
                          {c.name}
                        </Link>
                        <CustomerStatusBadge status={c.status} anonymized={c.anonymizedAt !== null} />
                      </div>
                      <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-caption">
                        <dt className="text-fg-muted">Kod</dt>
                        <dd className="font-mono text-fg">{c.code}</dd>
                        {c.taxNumber ? (
                          <>
                            <dt className="text-fg-muted">Vergi no</dt>
                            <dd className="font-mono text-fg">{c.taxNumber}</dd>
                          </>
                        ) : null}
                        {c.phone ? (
                          <>
                            <dt className="text-fg-muted">Telefon</dt>
                            <dd className="text-fg">{c.phone}</dd>
                          </>
                        ) : null}
                        {c.dealerGroupId && groupNames.get(c.dealerGroupId) ? (
                          <>
                            <dt className="text-fg-muted">Bayi grubu</dt>
                            <dd className="text-fg">{groupNames.get(c.dealerGroupId)}</dd>
                          </>
                        ) : null}
                      </dl>
                    </li>
                  ))}
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

"use client";

import { useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import { ArrowLeft, Calculator, Search, UserRound, X } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { VariantScanner } from "@/components/inventory/variant-scanner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { ApiError } from "@/lib/api/client";
import { listCustomers, type Customer } from "@/lib/customers/api";
import { customerErrorMessage } from "@/lib/customers/errors";
import type { VariantRef } from "@/lib/inventory/receipt-form";
import { PRICE_LISTS_HREF, quotePrices, type PriceQuote, type ResolvedPrice } from "@/lib/pricing/api";
import { pricingErrorMessage, pricingFieldErrors } from "@/lib/pricing/errors";
import { priceRuleLabels, priceSourceLabels, vatModeLabel } from "@/lib/pricing/labels";
import { currencyOptions, formatMoney, formatPercent, formatQuantity, isZeroDecimal, parseDecimalInput } from "@/lib/pricing/money";
import { useSession } from "@/lib/session/session-context";

type Line = { key: string; variant: VariantRef; quantity: string };

let lineCounter = 0;

/**
 * "Fiyat sorgula": POST /pricing/quote for a customer (or none = the default list) on a date. Shows where each price
 * came from (customer list → dealer group list → default list; list item or base list − discount). A line without a
 * price is shown as "Fiyat yok" — never as 0. Read-only and side-effect free.
 */
export function PriceQuotePanel() {
  const { api } = useSession();
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [date, setDate] = useState("");
  const [currency, setCurrency] = useState("");
  const [lines, setLines] = useState<Line[]>([]);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<PriceQuote | null>(null);
  const resultRef = useRef<HTMLHeadingElement>(null);

  const addLine = (variant: VariantRef) => {
    setResult(null);
    setLines((ls) => [...ls, { key: `q-${++lineCounter}`, variant, quantity: "1" }]);
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const local: Record<string, string> = {};
    if (lines.length === 0) local.lines = "En az bir ürün ekleyin.";
    lines.forEach((l, i) => {
      const q = parseDecimalInput(l.quantity, 3);
      if (!q.ok || isZeroDecimal(q.value)) local[`lines[${i}].quantity`] = "0'dan büyük, en fazla 3 ondalıklı bir miktar girin.";
    });
    setErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    try {
      const quote = await quotePrices(api, {
        customerId: customer?.id ?? null,
        date: date || null,
        currency: currency || null,
        lines: lines.map((l) => {
          const q = parseDecimalInput(l.quantity, 3);
          return { variantId: l.variant.id, quantity: q.ok ? q.value : l.quantity };
        }),
      });
      setResult(quote);
      requestAnimationFrame(() => resultRef.current?.focus());
    } catch (error) {
      setResult(null);
      if (error instanceof ApiError && error.status === 422) {
        setErrors(pricingFieldErrors(error));
        setFormError(error.userMessage);
      } else {
        setFormError(pricingErrorMessage(error));
      }
    } finally {
      setBusy(false);
    }
  };

  const columns: TableColumn<ResolvedPrice>[] = [
    { key: "sku", header: "Ürün", cell: (r) => <span className="font-mono text-caption">{r.sku}</span> },
    { key: "qty", header: "Miktar", numeric: true, width: "7rem", cell: (r) => formatQuantity(r.quantity) },
    { key: "price", header: "Birim fiyat", numeric: true, width: "10rem", cell: (r) => <PriceCell line={r} currency={result?.currency ?? ""} /> },
    { key: "source", header: "Kaynak", cell: (r) => <SourceCell line={r} currency={result?.currency ?? ""} /> },
    {
      key: "vat",
      header: "KDV",
      width: "9rem",
      cell: (r) => (r.status === "priced" && r.pricesIncludeTax !== null ? `${vatModeLabel(r.pricesIncludeTax)} · ${formatPercent(r.taxRate)}` : (formatPercent(r.taxRate) ?? "—")),
    },
  ];

  return (
    <>
      <Link href={PRICE_LISTS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
        <ArrowLeft className="size-4" aria-hidden="true" />
        Fiyat listeleri
      </Link>
      <PageHeader title="Fiyat sorgula" description="Bir müşterinin bugün (veya seçtiğiniz tarihte) hangi fiyatı göreceğini ve fiyatın hangi listeden geldiğini önizleyin." />

      <form noValidate onSubmit={submit} aria-label="Fiyat sorgusu" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
        {formError ? (
          <FormAlert tone="error" title="Sorgulanamadı">
            {formError}
          </FormAlert>
        ) : null}
        <CustomerPicker
          value={customer}
          error={errors.customerId}
          onChange={(c) => {
            setCustomer(c);
            setResult(null);
          }}
        />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Input label="Tarih" type="date" value={date} error={errors.date} hint="Boş: bugün (İstanbul saati)" onChange={(e) => setDate(e.target.value)} />
          <Select
            label="Para birimi"
            placeholder="Müşterinin / işletmenin para birimi"
            options={currencyOptions(currency)}
            value={currency}
            error={errors.currency}
            onChange={(e) => setCurrency(e.target.value)}
          />
        </div>
        <VariantScanner label="Ürün ekle" hint="Barkod, SKU veya ürün adı; Enter ile ekler" onPick={addLine} pickedText={(v) => `${v.sku} eklendi.`} error={errors.lines} />
        {lines.length > 0 ? (
          <ol aria-label="Sorgulanacak ürünler" className="flex flex-col gap-2">
            {lines.map((l, i) => (
              <li key={l.key} className="grid grid-cols-[1fr_8rem_auto] items-start gap-2 rounded-md border border-line p-3">
                <span className="flex flex-col pt-2">
                  <span className="font-mono text-body text-fg">{l.variant.sku}</span>
                  {l.variant.name ? <span className="text-caption text-fg-muted">{l.variant.name}</span> : null}
                  {errors[`lines[${i}].variantId`] ? <span className="text-caption text-danger-fg">{errors[`lines[${i}].variantId`]}</span> : null}
                </span>
                <Input
                  label={`${l.variant.sku} miktarı`}
                  hideLabel
                  inputMode="decimal"
                  value={l.quantity}
                  error={errors[`lines[${i}].quantity`]}
                  onChange={(e) => {
                    setResult(null);
                    setLines((ls) => ls.map((x) => (x.key === l.key ? { ...x, quantity: e.target.value } : x)));
                  }}
                />
                <Button
                  variant="ghost"
                  icon={<X className="size-4" />}
                  aria-label={`${l.variant.sku} satırını kaldır`}
                  onClick={() => {
                    setResult(null);
                    setLines((ls) => ls.filter((x) => x.key !== l.key));
                  }}
                />
              </li>
            ))}
          </ol>
        ) : null}
        <div className="flex justify-end">
          <Button type="submit" icon={<Calculator className="size-4" />} loading={busy}>
            Fiyatları sorgula
          </Button>
        </div>
      </form>

      {result ? (
        <section aria-labelledby="quote-result" className="flex flex-col gap-3">
          <h2 id="quote-result" ref={resultRef} tabIndex={-1} className="text-h3 text-fg">
            Sonuç — {result.currency}, {new Intl.DateTimeFormat("tr-TR", { dateStyle: "long", timeZone: "UTC" }).format(new Date(`${result.date}T00:00:00Z`))}
          </h2>
          {result.lines.some((l) => l.status !== "priced") ? (
            <FormAlert tone="warning" title="Bazı ürünlerin fiyatı yok">
              “Fiyat yok” satırlarında müşteri listesi, bayi grubu listesi ve varsayılan listede geçerli bir fiyat bulunamadı. Bu ürünler fiyat girilmeden
              siparişte onaylanamaz.
            </FormAlert>
          ) : null}
          {result.dealerGroupDiscountPercent !== null && result.dealerGroupDiscountPercent !== undefined ? (
            <p className="text-body text-fg-secondary">
              Bayi grubunun varsayılan iskontosu: <strong>{formatPercent(result.dealerGroupDiscountPercent)}</strong> (siparişte önerilir; aşağıdaki fiyatlara
              uygulanmamıştır).
            </p>
          ) : null}
          <div className="hidden md:block">
            <Table caption="Çözümlenen fiyatlar" columns={columns} rows={result.lines} getRowId={(r) => `${r.variantId}-${String(r.quantity)}`} />
          </div>
          <ul aria-label="Çözümlenen fiyatlar" className="flex flex-col gap-2 md:hidden">
            {result.lines.map((r) => (
              <li key={`${r.variantId}-${String(r.quantity)}`} className="rounded-lg border border-line bg-card p-3 shadow-e1">
                <div className="flex items-start justify-between gap-2">
                  <span className="font-mono text-body text-fg">{r.sku}</span>
                  <PriceCell line={r} currency={result.currency} />
                </div>
                <p className="text-caption text-fg-muted">Miktar {formatQuantity(r.quantity)}</p>
                <SourceCell line={r} currency={result.currency} />
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </>
  );
}

export function PriceCell({ line, currency }: { line: ResolvedPrice; currency: string }) {
  const money = line.status === "priced" ? formatMoney(line.unitPrice, currency) : null;
  if (money === null) return <Badge tone="danger">Fiyat yok</Badge>;
  return <span className="font-medium tabular-nums">{money}</span>;
}

export function SourceCell({ line, currency }: { line: ResolvedPrice; currency: string }) {
  if (line.status !== "priced") return <span className="text-caption text-fg-muted">Geçerli liste veya kalem bulunamadı</span>;
  const tier = line.minQuantity !== null && line.minQuantity !== undefined ? ` · kademe: en az ${formatQuantity(line.minQuantity)}` : "";
  const base =
    line.rule === "base_discount" && line.baseUnitPrice !== null
      ? ` (taban ${formatMoney(line.baseUnitPrice, currency)} − ${formatPercent(line.discountPercent) ?? "%0"})`
      : "";
  return (
    <span className="flex flex-col text-caption">
      <span className="text-fg">
        {(line.source && priceSourceLabels[line.source]) ?? line.source ?? "—"}
        {line.priceListName ? `: ${line.priceListName}` : ""}
      </span>
      <span className="text-fg-muted">
        {(line.rule && priceRuleLabels[line.rule]) ?? line.rule ?? ""}
        {base}
        {tier}
      </span>
    </span>
  );
}

/** Customer search (name/code contains, exact tax no / phone / WhatsApp); none = default list only. */
function CustomerPicker({ value, error, onChange }: { value: Customer | null; error?: string; onChange: (c: Customer | null) => void }) {
  const { api } = useSession();
  const [term, setTerm] = useState("");
  const [state, setState] = useState<{ kind: "idle" } | { kind: "searching" } | { kind: "results"; items: Customer[] } | { kind: "error"; message: string }>({ kind: "idle" });

  const search = async () => {
    const q = term.trim();
    if (!q) return;
    setState({ kind: "searching" });
    try {
      const page = await listCustomers(api, new URLSearchParams({ q, limit: "10" }).toString());
      setState({ kind: "results", items: page.items });
    } catch (err) {
      setState({ kind: "error", message: customerErrorMessage(err) });
    }
  };

  if (value) {
    return (
      <div className="flex flex-col gap-1.5">
        <span className="text-body font-medium text-fg">Müşteri</span>
        <div className="flex flex-wrap items-center gap-2 rounded-md border border-line-strong p-3">
          <UserRound className="size-4 text-fg-muted" aria-hidden="true" />
          <span className="font-medium text-fg">{value.name}</span>
          <span className="font-mono text-caption text-fg-muted">{value.code}</span>
          <Button variant="ghost" size="sm" className="ml-auto" onClick={() => onChange(null)}>
            Müşteriyi kaldır
          </Button>
        </div>
        {error ? <p className="text-caption text-danger-fg">{error}</p> : null}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-start gap-2">
        <Input
          label="Müşteri (isteğe bağlı)"
          className="flex-1"
          type="search"
          prefix={<Search className="size-4" />}
          value={term}
          error={error}
          hint="Boş bırakılırsa yalnızca varsayılan liste kullanılır."
          autoComplete="off"
          onChange={(e) => setTerm(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void search();
            }
          }}
        />
        <Button variant="secondary" className="mt-[1.625rem]" loading={state.kind === "searching"} disabled={!term.trim()} onClick={() => void search()}>
          Müşteri ara
        </Button>
      </div>
      {state.kind === "error" ? (
        <p role="alert" className="text-body text-danger-fg">
          {state.message}
        </p>
      ) : null}
      {state.kind === "results" ? (
        state.items.length === 0 ? (
          <p role="status" className="text-body text-fg-muted">
            Eşleşen müşteri yok.
          </p>
        ) : (
          <ul aria-label="Bulunan müşteriler" className="flex flex-col gap-2">
            {state.items.map((c) => (
              <li key={c.id}>
                <button
                  type="button"
                  onClick={() => {
                    onChange(c);
                    setState({ kind: "idle" });
                    setTerm("");
                  }}
                  className="flex min-h-12 w-full flex-col items-start justify-center rounded-md border border-line-strong bg-card px-4 py-2 text-left hover:bg-sunken"
                >
                  <span className="text-body text-fg">{c.name}</span>
                  <span className="font-mono text-caption text-fg-muted">{c.code}</span>
                </button>
              </li>
            ))}
          </ul>
        )
      ) : null}
    </div>
  );
}

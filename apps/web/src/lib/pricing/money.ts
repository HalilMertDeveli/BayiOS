/**
 * Money, percent and quantity display + input normalisation. The client never does money arithmetic: amounts are
 * resolved by the API (pricing/quote) and only formatted here. User input is normalised as text ("12,50" → "12.50")
 * and sent to the API as a decimal string, so no binary floating point touches a price.
 */

/** A decimal as the API returns it (JSON number or numeric string). */
export type Decimal = number | string;

const moneyFormats = new Map<string, Intl.NumberFormat>();

function moneyFormat(currency: string): Intl.NumberFormat | null {
  const code = currency.trim().toUpperCase();
  if (!/^[A-Z]{3}$/.test(code)) return null;
  let format = moneyFormats.get(code);
  if (!format) {
    try {
      format = new Intl.NumberFormat("tr-TR", { style: "currency", currency: code, minimumFractionDigits: 2, maximumFractionDigits: 4 });
    } catch {
      return null;
    }
    moneyFormats.set(code, format);
  }
  return format;
}

const decimalFormats = new Map<number, Intl.NumberFormat>();

function decimalFormat(maxFractionDigits: number): Intl.NumberFormat {
  let format = decimalFormats.get(maxFractionDigits);
  if (!format) {
    format = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: maxFractionDigits });
    decimalFormats.set(maxFractionDigits, format);
  }
  return format;
}

/**
 * Intl formats a numeric string exactly (ECMA-402 "StringNumericLiteral"), without converting it to a double first,
 * so "1234.5678" keeps its last digit. Numbers are passed as they are.
 */
function formatExact(format: Intl.NumberFormat, value: Decimal): string {
  return format.format(value as Parameters<Intl.NumberFormat["format"]>[0]);
}

/**
 * "₺1.234,50" for (1234.5, "TRY"). Returns null for a missing amount — callers must show "fiyat yok", never 0.
 * Unknown/invalid currency codes fall back to "1.234,50 XYZ".
 */
export function formatMoney(value: Decimal | null | undefined, currency: string): string | null {
  if (value === null || value === undefined || value === "") return null;
  const format = moneyFormat(currency);
  if (format) return formatExact(format, value);
  return `${formatExact(decimalFormat(4), value)} ${currency}`.trim();
}

/** "%12,5" (Turkish puts the sign first). Null for a missing value. */
export function formatPercent(value: Decimal | null | undefined): string | null {
  if (value === null || value === undefined || value === "") return null;
  return `%${formatExact(decimalFormat(2), value)}`;
}

/** Quantities have at most 3 decimals (numeric(18,3)). */
export function formatQuantity(value: Decimal): string {
  return formatExact(decimalFormat(3), value);
}

/** Value for an input: the API's "12.5" → "12,5" (the Turkish decimal comma). */
export function toDecimalInput(value: Decimal | null | undefined): string {
  if (value === null || value === undefined) return "";
  return String(value).replace(".", ",");
}

export type DecimalParse = { ok: true; value: string } | { ok: false };

/**
 * Normalises typed text to the API's decimal string: one optional "," or "." as the decimal separator, no thousands
 * separators, no sign, at most `maxDecimals` decimals. "007,50" → "7.50". Text only — no Number() round trip.
 */
export function parseDecimalInput(text: string, maxDecimals: number): DecimalParse {
  const trimmed = text.trim().replace(/\s+/g, "");
  const match = /^(\d+)(?:[.,](\d*))?$/.exec(trimmed);
  if (!match) return { ok: false };
  const integer = match[1].replace(/^0+(?=\d)/, "");
  const fraction = match[2] ?? "";
  if (fraction.length > maxDecimals) return { ok: false };
  return { ok: true, value: fraction ? `${integer}.${fraction}` : integer };
}

/** True when a normalised decimal string is zero ("0", "0.00"). */
export function isZeroDecimal(value: string): boolean {
  return /^0(?:\.0*)?$/.test(value);
}

/** Equality of two decimals by value as text ("12.50" equals 12.5), for change detection without arithmetic. */
export function sameDecimal(a: Decimal | null | undefined, b: Decimal | null | undefined): boolean {
  if (a === null || a === undefined || a === "") return b === null || b === undefined || b === "";
  if (b === null || b === undefined || b === "") return false;
  return canonical(String(a)) === canonical(String(b));
}

function canonical(value: string): string {
  const [integer, fraction = ""] = value.split(".");
  const i = integer.replace(/^(-?)0+(?=\d)/, "$1");
  const f = fraction.replace(/0+$/, "");
  return f ? `${i}.${f}` : i;
}

/** Currencies offered in pickers. Other ISO codes already stored on a record stay selectable (see withCurrentOption). */
export const CURRENCIES = ["TRY", "USD", "EUR", "GBP"] as const;

export const currencyLabels: Record<string, string> = {
  TRY: "TRY — Türk lirası",
  USD: "USD — ABD doları",
  EUR: "EUR — Euro",
  GBP: "GBP — İngiliz sterlini",
};

export function currencyOptions(current?: string | null): { value: string; label: string }[] {
  const options: { value: string; label: string }[] = CURRENCIES.map((c) => ({ value: c, label: currencyLabels[c] }));
  if (current && !options.some((o) => o.value === current)) options.push({ value: current, label: current });
  return options;
}

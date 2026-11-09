/** Quantities: the API takes a decimal > 0 with at most 3 decimals (≤ 999 999 999 999 999). */
export const MAX_QUANTITY = 999_999_999_999_999;
export const MAX_QUANTITY_DECIMALS = 3;

export type QuantityParse = { ok: true; value: number } | { ok: false; message: string };

/**
 * Parses a quantity typed the Turkish way ("12,5") or with a dot ("12.5"). Thousands separators are not accepted:
 * "1.250" would be ambiguous, so only one separator is allowed and it is always the decimal one.
 */
export function parseQuantity(input: string, options: { allowFraction?: boolean } = {}): QuantityParse {
  const text = input.trim().replace(/\s+/g, "");
  if (!text) return { ok: false, message: "Miktar girin." };
  if (!/^\d+([.,]\d+)?$/.test(text)) return { ok: false, message: "Geçerli bir miktar girin (ör. 12 veya 12,5)." };
  const normalized = text.replace(",", ".");
  const decimals = normalized.includes(".") ? normalized.split(".")[1].length : 0;
  if (decimals > MAX_QUANTITY_DECIMALS) return { ok: false, message: "En fazla 3 ondalık basamak girin." };
  if (options.allowFraction === false && decimals > 0) return { ok: false, message: "Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin." };
  const value = Number(normalized);
  if (!(value > 0)) return { ok: false, message: "Miktar 0'dan büyük olmalı." };
  if (value > MAX_QUANTITY) return { ok: false, message: "Miktar çok büyük." };
  return { ok: true, value };
}

const formatter = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: MAX_QUANTITY_DECIMALS });

/** Quantity for display ("1.250,5"); the API may send numbers as JSON numbers or numeric strings. */
export function formatQuantity(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === "") return "—";
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? formatter.format(n) : String(value);
}

/** Signed quantity for movement rows ("+5", "−2"). */
export function formatSignedQuantity(value: number | string): string {
  const n = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(n)) return String(value);
  if (n > 0) return `+${formatter.format(n)}`;
  if (n < 0) return `−${formatter.format(-n)}`;
  return formatter.format(0);
}

export function toNumber(value: number | string | null | undefined): number {
  if (value === null || value === undefined) return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
}

/** Adds two quantities without binary floating-point noise (3 decimals max). */
export function addQuantities(a: number, b: number): number {
  return Math.round((a + b) * 1000) / 1000;
}

/** Quantity back into the input's text form ("12,5"). */
export function quantityText(value: number): string {
  return String(value).replace(".", ",");
}

import type { VariantRef } from "@/lib/inventory/receipt-form";
import type { PriceListItem, PriceListItemInput } from "./api";
import { MAX_UPSERT_ITEMS } from "./api";
import { isZeroDecimal, parseDecimalInput, toDecimalInput } from "./money";
import type { RowErrors } from "./errors";

/** One row of the bulk editor: a new tier, or an existing item being repriced. */
export type DraftRow = {
  /** Stable React key; not sent. */
  key: string;
  variant: VariantRef;
  /** As typed (Turkish decimal comma allowed). Fixed for existing items: (variant, minQuantity) is the item's key. */
  minQuantity: string;
  unitPrice: string;
  /** Existing item id when the row reprices an item already in the list. */
  itemId: string | null;
};

let counter = 0;
const nextKey = () => `row-${++counter}`;

export function newDraftRow(variant: VariantRef, minQuantity = "1"): DraftRow {
  return { key: nextKey(), variant, minQuantity, unitPrice: "", itemId: null };
}

export function draftFromItem(item: PriceListItem, variant: VariantRef): DraftRow {
  return { key: nextKey(), variant, minQuantity: toDecimalInput(item.minQuantity), unitPrice: toDecimalInput(item.unitPrice), itemId: item.id };
}

/**
 * Client checks before the bulk upsert (the API repeats them and answers per row with `items[i].field`):
 * minimum quantity > 0 with ≤ 3 decimals, unit price ≥ 0 with ≤ 4 decimals, no duplicate (variant, min quantity),
 * at most 500 rows.
 */
export function validateDraftRows(rows: readonly DraftRow[]): { rows: RowErrors; general: string[] } {
  const result: RowErrors = {};
  const general: string[] = [];
  if (rows.length === 0) general.push("Kaydedilecek satır yok. Ürün okutup fiyat girin.");
  if (rows.length > MAX_UPSERT_ITEMS) general.push(`Tek seferde en fazla ${MAX_UPSERT_ITEMS} satır kaydedilebilir.`);
  const seen = new Map<string, number>();
  rows.forEach((row, i) => {
    const qty = parseDecimalInput(row.minQuantity, 3);
    if (!qty.ok || isZeroDecimal(qty.value)) {
      result[i] = { ...result[i], minQuantity: "0'dan büyük, en fazla 3 ondalıklı bir miktar girin." };
    } else {
      const key = `${row.variant.id}|${canonicalQty(qty.value)}`;
      const first = seen.get(key);
      if (first !== undefined) result[i] = { ...result[i], row: `Bu ürün için bu en az miktar zaten ${first + 1}. satırda var.` };
      else seen.set(key, i);
    }
    if (!row.unitPrice.trim()) result[i] = { ...result[i], unitPrice: "Birim fiyat girin." };
    else if (!parseDecimalInput(row.unitPrice, 4).ok) result[i] = { ...result[i], unitPrice: "0 veya daha büyük, en fazla 4 ondalıklı bir fiyat girin." };
  });
  return { rows: result, general };
}

function canonicalQty(value: string): string {
  return value.includes(".") ? value.replace(/\.?0+$/, "") : value;
}

/** Request rows, in editor order (so a 422 `items[i]` maps back to row i). Decimals are sent as strings. */
export function buildUpsertItems(rows: readonly DraftRow[]): PriceListItemInput[] {
  return rows.map((row) => {
    const qty = parseDecimalInput(row.minQuantity, 3);
    const price = parseDecimalInput(row.unitPrice, 4);
    return {
      variantId: row.variant.id,
      minQuantity: qty.ok ? qty.value : row.minQuantity,
      unitPrice: price.ok ? price.value : row.unitPrice,
    };
  });
}

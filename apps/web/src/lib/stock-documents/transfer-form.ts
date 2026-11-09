import type { VariantRef } from "@/lib/inventory/receipt-form";
import { addQuantities, parseQuantity, quantityText, toNumber } from "@/lib/inventory/quantity";
import type { Balance } from "@/lib/inventory/api";
import type { CreateTransferRequest, ReceiveTransferLineRequest, TransferLine } from "./api";

export const NOTE_MAX = 500;
export const MAX_LINES = 500;

export type TransferLineValues = {
  key: string;
  variant: VariantRef;
  /** "" in a warehouse without locations. */
  sourceLocationId: string;
  /** Planned destination shelf (optional; the receiver may choose another). */
  destinationLocationId: string;
  quantity: string;
};

export type TransferFormValues = {
  sourceWarehouseId: string;
  destinationWarehouseId: string;
  note: string;
  lines: TransferLineValues[];
};

export type TransferErrors = Record<string, string>;

export const emptyTransfer = (sourceWarehouseId = ""): TransferFormValues => ({ sourceWarehouseId, destinationWarehouseId: "", note: "", lines: [] });

/**
 * A scan adds the variant: the same variant on the same (or a not yet chosen) source shelf gets +1, anything else
 * becomes a new line with quantity 1 on `sourceLocationId` (the shelf with the most available stock, if known).
 */
export function addTransferLine(
  lines: readonly TransferLineValues[],
  variant: VariantRef,
  sourceLocationId: string,
  makeKey: () => string,
): { lines: TransferLineValues[]; index: number } {
  const index = lines.findIndex((l) => l.variant.id === variant.id && (l.sourceLocationId === sourceLocationId || l.sourceLocationId === ""));
  if (index >= 0) {
    const current = parseQuantity(lines[index].quantity);
    const next = current.ok ? quantityText(addQuantities(current.value, 1)) : lines[index].quantity;
    return { lines: lines.map((l, i) => (i === index ? { ...l, quantity: next } : l)), index };
  }
  return { lines: [...lines, { key: makeKey(), variant, sourceLocationId, destinationLocationId: "", quantity: "1" }], index: lines.length };
}

/** The source shelf with the most available stock ("" when none / no locations). */
export function bestShelf(availability: Availability | undefined): string {
  let best = "";
  let max = 0;
  for (const [location, qty] of availability ?? []) {
    if (location && qty > max) {
      best = location;
      max = qty;
    }
  }
  return best;
}

/** Available (satılabilir) stock per source shelf of one variant: location id ("" = no location) → quantity. */
export type Availability = Map<string, number>;

/** Balance rows of one variant in the source warehouse → availability per shelf. */
export function availabilityFrom(balances: readonly Balance[], warehouseId: string): Availability {
  const map: Availability = new Map();
  for (const b of balances) {
    if (b.warehouseId !== warehouseId) continue;
    const key = b.locationId ?? "";
    map.set(key, (map.get(key) ?? 0) + toNumber(b.available));
  }
  return map;
}

/**
 * Client-side checks before the draft is saved: warehouses differ, at least one line, quantity > 0 and not more than is
 * available on the source shelf right now (the API re-checks at dispatch — stock can move meanwhile), source shelf in a
 * located warehouse, each (variant, source shelf) once. Keys match the API's field names.
 */
export function validateTransfer(
  values: TransferFormValues,
  options: { sourceUsesLocations: boolean; availability: (variantId: string) => Availability | undefined },
): TransferErrors {
  const errors: TransferErrors = {};
  if (!values.sourceWarehouseId) errors.sourceWarehouseId = "Kaynak depoyu seçin.";
  if (!values.destinationWarehouseId) errors.destinationWarehouseId = "Hedef depoyu seçin.";
  else if (values.destinationWarehouseId === values.sourceWarehouseId)
    errors.destinationWarehouseId = "Kaynak ve hedef depo farklı olmalı. Aynı depoda raf değiştirmek için raf taşıma kullanın.";
  if (values.note.length > NOTE_MAX) errors.note = `En fazla ${NOTE_MAX} karakter yazın.`;
  if (values.lines.length === 0) errors.lines = "En az bir ürün ekleyin.";
  if (values.lines.length > MAX_LINES) errors.lines = `Bir transferde en fazla ${MAX_LINES} satır olabilir.`;
  const seen = new Set<string>();
  values.lines.forEach((line, i) => {
    if (options.sourceUsesLocations && !line.sourceLocationId) errors[`lines[${i}].sourceLocationId`] = "Kaynak rafı seçin.";
    const pair = `${line.variant.id}|${line.sourceLocationId}`;
    if (seen.has(pair)) errors[`lines[${i}].sourceLocationId`] = "Bu ürün ve raf zaten eklendi; miktarı tek satırda toplayın.";
    seen.add(pair);
    const parsed = parseQuantity(line.quantity);
    if (!parsed.ok) {
      errors[`lines[${i}].quantity`] = parsed.message;
      return;
    }
    // Not loaded yet (or failed): the API checks at dispatch anyway.
    const map = options.availability(line.variant.id);
    if (!map || (options.sourceUsesLocations && !line.sourceLocationId)) return;
    const available = map.get(options.sourceUsesLocations ? line.sourceLocationId : "") ?? 0;
    if (available <= 0) errors[`lines[${i}].quantity`] = "Kaynakta bu ürünün satılabilir stoğu yok.";
    else if (parsed.value > available) errors[`lines[${i}].quantity`] = `Kaynakta satılabilir yalnızca ${formatPlain(available)}. Miktarı azaltın.`;
  });
  return errors;
}

const plain = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 3 });
const formatPlain = (n: number) => plain.format(n);

export function buildTransferRequest(values: TransferFormValues, options: { sourceUsesLocations: boolean; destinationUsesLocations: boolean }): CreateTransferRequest {
  return {
    sourceWarehouseId: values.sourceWarehouseId,
    destinationWarehouseId: values.destinationWarehouseId,
    note: values.note.trim() || null,
    lines: values.lines.map((line) => {
      const parsed = parseQuantity(line.quantity);
      return {
        variantId: line.variant.id,
        quantity: parsed.ok ? parsed.value : 0,
        sourceLocationId: options.sourceUsesLocations ? line.sourceLocationId || null : null,
        destinationLocationId: options.destinationUsesLocations ? line.destinationLocationId || null : null,
      };
    }),
  };
}

export function transferTotal(lines: readonly TransferLineValues[]): number {
  return lines.reduce((sum, l) => {
    const parsed = parseQuantity(l.quantity);
    return parsed.ok ? Math.round((sum + parsed.value) * 1000) / 1000 : sum;
  }, 0);
}

// ---- Receipt at the destination ----

/** Per transfer line: the quantity typed ("" = not received now) and the destination shelf. */
export type ReceiveRow = { quantity: string; locationId: string };

/** Pre-fills what is still in transit and the planned shelf, so "receive all" is one click. */
export function initialReceiveRows(lines: readonly TransferLine[]): Record<string, ReceiveRow> {
  const rows: Record<string, ReceiveRow> = {};
  for (const line of lines) {
    const open = toNumber(line.inTransitQuantity);
    if (open > 0) rows[line.id] = { quantity: String(open).replace(".", ","), locationId: line.destinationLocationId ?? "" };
  }
  return rows;
}

/**
 * Builds the receive request from the rows; errors keyed by line id. Empty quantities are skipped (partial receipt);
 * more than is in transit is refused here already (the API answers 422 transfer.over_receipt).
 */
export function buildReceive(
  lines: readonly TransferLine[],
  rows: Record<string, ReceiveRow>,
  options: { destinationUsesLocations: boolean },
): { ok: true; lines: ReceiveTransferLineRequest[] } | { ok: false; errors: Record<string, string> } {
  const errors: Record<string, string> = {};
  const result: ReceiveTransferLineRequest[] = [];
  for (const line of lines) {
    const row = rows[line.id];
    if (!row || !row.quantity.trim()) continue;
    const parsed = parseQuantity(row.quantity);
    if (!parsed.ok) {
      errors[line.id] = parsed.message;
      continue;
    }
    const open = toNumber(line.inTransitQuantity);
    if (parsed.value > open) {
      errors[line.id] = `Yolda yalnızca ${formatPlain(open)} var. Fazla gelen ürünü ayrıca mal kabul olarak kaydedin.`;
      continue;
    }
    if (options.destinationUsesLocations && !row.locationId) {
      errors[line.id] = "Hedef rafı seçin.";
      continue;
    }
    result.push({
      lineId: line.id,
      variantId: null,
      code: null,
      quantity: parsed.value,
      locationId: options.destinationUsesLocations ? row.locationId : null,
      locationCode: null,
    });
  }
  if (Object.keys(errors).length > 0) return { ok: false, errors };
  if (result.length === 0) return { ok: false, errors: { _form: "Teslim alınan en az bir satır için miktar girin." } };
  return { ok: true, lines: result };
}

/** Totals of the discrepancy report (fark raporu). */
export function transferTotals(lines: readonly TransferLine[]) {
  const sum = (pick: (l: TransferLine) => number | string) => Math.round(lines.reduce((s, l) => s + toNumber(pick(l)), 0) * 1000) / 1000;
  return {
    sent: sum((l) => l.quantity),
    received: sum((l) => l.receivedQuantity),
    inTransit: sum((l) => l.inTransitQuantity),
    returned: sum((l) => l.returnedQuantity),
    lost: sum((l) => l.lostQuantity),
  };
}

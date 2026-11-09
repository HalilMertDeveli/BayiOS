import { MAX_QUANTITY, MAX_QUANTITY_DECIMALS, parseQuantity, toNumber, type QuantityParse } from "@/lib/inventory/quantity";
import type { CountLine, CountEntryRequest } from "./api";

export type EntryMode = "set" | "add";

/**
 * A counted quantity: `set` accepts 0 ("the shelf is empty"), `add` needs more than 0 (one scan adds to the line).
 * Turkish decimal comma or a dot, at most 3 decimals.
 */
export function parseCountQuantity(input: string, mode: EntryMode): QuantityParse {
  const text = input.trim().replace(/\s+/g, "");
  if (mode === "set" && /^0+([.,]0+)?$/.test(text)) return { ok: true, value: 0 };
  const parsed = parseQuantity(input);
  if (!parsed.ok) return parsed;
  if (parsed.value > MAX_QUANTITY) return { ok: false, message: "Miktar çok büyük." };
  return parsed;
}

/** One line entry: absolute quantity on a line of the count. */
export function lineEntry(lineId: string, quantity: number): CountEntryRequest {
  return { lineId, variantId: null, code: null, locationId: null, locationCode: null, quantity, mode: "set" };
}

/** One scan entry: product code + shelf code (shelf only in located warehouses). */
export function scanEntry(code: string, locationCode: string | null, quantity: number, mode: EntryMode): CountEntryRequest {
  return { lineId: null, variantId: null, code: code.trim(), locationId: null, locationCode: locationCode?.trim() || null, quantity, mode };
}

export type CountLineFilter = "all" | "pending" | "counted" | "difference" | "moved";

/** Lines shown in the entry / report list: by text (SKU or shelf code) and by state. */
export function filterCountLines(lines: readonly CountLine[], text: string, filter: CountLineFilter): CountLine[] {
  const term = text.trim().toLocaleLowerCase("tr");
  return lines.filter((line) => {
    if (term && !line.sku.toLocaleLowerCase("tr").includes(term) && !(line.locationCode ?? "").toLocaleLowerCase("tr").includes(term)) return false;
    if (filter === "pending") return line.countedQuantity === null || line.countedQuantity === undefined;
    if (filter === "counted") return line.countedQuantity !== null && line.countedQuantity !== undefined;
    if (filter === "difference") return line.difference !== null && line.difference !== undefined && toNumber(line.difference) !== 0;
    if (filter === "moved") return line.movedSinceSnapshot;
    return true;
  });
}

/** Totals for the difference report (only where the system quantity is visible). */
export function countReportTotals(lines: readonly CountLine[]) {
  let expected = 0;
  let counted = 0;
  let plus = 0;
  let minus = 0;
  for (const line of lines) {
    if (line.countedQuantity === null || line.countedQuantity === undefined) continue;
    counted += toNumber(line.countedQuantity);
    if (line.expectedQuantity !== null && line.expectedQuantity !== undefined) expected += toNumber(line.expectedQuantity);
    const diff = line.difference === null || line.difference === undefined ? 0 : toNumber(line.difference);
    if (diff > 0) plus += diff;
    if (diff < 0) minus += diff;
  }
  const round = (n: number) => Math.round(n * 10 ** MAX_QUANTITY_DECIMALS) / 10 ** MAX_QUANTITY_DECIMALS;
  return { expected: round(expected), counted: round(counted), plus: round(plus), minus: round(minus) };
}

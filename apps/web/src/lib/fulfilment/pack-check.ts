import type { PackShipmentRequest, ShipmentLine, TaskLine } from "./api";
import { toNumber } from "@/lib/inventory/quantity";

/**
 * Local pack check: the packer re-scans every item into packages (koli). Each scan is matched to a shipment line by the
 * product barcode (from the picking task) or the SKU; the packed total per line must equal what ships. Nothing is sent
 * until "Paket kontrolünü kaydet": the API checks the same rule again (packing.over_pack / quantity_mismatch).
 */

export type PackState = {
  /** Packages in order; each maps shipment line id → packed quantity. */
  packages: Record<string, number>[];
  /** Index of the package receiving scans. */
  current: number;
};

export const emptyPack = (): PackState => ({ packages: [{}], current: 0 });

const add = (a: number, b: number) => Math.round((a + b) * 1000) / 1000;

export const shipsQuantity = (line: ShipmentLine) => toNumber(line.quantity);

export function packedOf(state: PackState, lineId: string): number {
  return state.packages.reduce((sum, p) => add(sum, p[lineId] ?? 0), 0);
}

/** Barcode per variant from the picking task (shipment lines carry no barcode). */
export function barcodeMap(taskLines: readonly Pick<TaskLine, "variantId" | "barcode">[]): Map<string, string> {
  const map = new Map<string, string>();
  for (const l of taskLines) if (l.barcode) map.set(l.variantId, l.barcode);
  return map;
}

/** Lines matching a scanned code: barcode exact, SKU case-insensitive. Lines that ship nothing are skipped. */
export function matchShipmentLines(code: string, lines: readonly ShipmentLine[], barcodes: Map<string, string>): ShipmentLine[] {
  const c = code.trim();
  if (!c) return [];
  return lines.filter((l) => shipsQuantity(l) > 0 && (barcodes.get(l.variantId) === c || l.sku.toUpperCase() === c.toUpperCase()));
}

export type PackScanResult =
  | { ok: true; state: PackState; line: ShipmentLine; packed: number }
  | { ok: false; reason: "unknown" | "over"; line?: ShipmentLine };

/** One scan of `quantity` units into the current package; rejects an unknown item or packing more than ships. */
export function packScan(
  state: PackState,
  code: string,
  quantity: number,
  lines: readonly ShipmentLine[],
  barcodes: Map<string, string>,
): PackScanResult {
  const candidates = matchShipmentLines(code, lines, barcodes);
  if (candidates.length === 0) return { ok: false, reason: "unknown" };
  const line = candidates.find((l) => add(packedOf(state, l.id), quantity) <= shipsQuantity(l));
  if (!line) return { ok: false, reason: "over", line: candidates[0] };
  const packages = state.packages.map((p, i) => (i === state.current ? { ...p, [line.id]: add(p[line.id] ?? 0, quantity) } : p));
  const next = { ...state, packages };
  return { ok: true, state: next, line, packed: packedOf(next, line.id) };
}

/** Opens a new package (the previous one must hold something). */
export function newPackage(state: PackState): PackState {
  const current = state.packages[state.current];
  if (!current || Object.keys(current).length === 0) return state;
  return { packages: [...state.packages, {}], current: state.packages.length };
}

/** Removes one unit of a line from the current package (a mis-scan). */
export function undoOne(state: PackState, lineId: string): PackState {
  const packages = state.packages.map((p, i) => {
    if (i !== state.current || !p[lineId]) return p;
    const left = add(p[lineId], -1);
    const copy = { ...p };
    if (left > 0) copy[lineId] = left;
    else delete copy[lineId];
    return copy;
  });
  return { ...state, packages };
}

export type PackProgress = { complete: boolean; lines: { line: ShipmentLine; packed: number; ships: number }[]; packageCount: number };

export function packProgress(state: PackState, lines: readonly ShipmentLine[]): PackProgress {
  const rows = lines.map((line) => ({ line, packed: packedOf(state, line.id), ships: shipsQuantity(line) }));
  const packageCount = state.packages.filter((p) => Object.keys(p).length > 0).length;
  return { complete: rows.every((r) => r.packed === r.ships) && packageCount > 0, lines: rows, packageCount };
}

/** The request body: non-empty packages with shipmentLineId items. */
export function packRequest(state: PackState): PackShipmentRequest {
  return {
    packages: state.packages
      .filter((p) => Object.keys(p).length > 0)
      .map((p) => ({ items: Object.entries(p).map(([shipmentLineId, quantity]) => ({ code: null, shipmentLineId, quantity })) })),
  };
}

import type { PickLineProgress, PickScanResponse, TaskDetail, TaskLine } from "./api";
import { toNumber } from "@/lib/inventory/quantity";

/**
 * Pure state of the pick screen (scan-first, one hand, keyboard-wedge scanner): which line is current, which shelf was
 * scanned, the quantity stepper, and how a scanned code is read. The API stays the judge of every scan (wrong item,
 * wrong shelf, over-pick); this only decides what to send and what to show.
 */

export const remaining = (line: Pick<TaskLine, "quantityRemaining">) => toNumber(line.quantityRemaining);
export const isOpenLine = (line: Pick<TaskLine, "quantityRemaining">) => remaining(line) > 0;

/** Lines in shelf pick order (the API already sorts by location pick_sequence; `sequence` keeps it stable). */
export function sortLines(lines: readonly TaskLine[]): TaskLine[] {
  return [...lines].sort((a, b) => toNumber(a.sequence) - toNumber(b.sequence));
}

/** The line to work on: the preferred one while it is still open, else the first open line in pick order. */
export function currentLine(lines: readonly TaskLine[], preferredId: string | null = null): TaskLine | null {
  const sorted = sortLines(lines);
  const preferred = preferredId ? sorted.find((l) => l.id === preferredId) : undefined;
  if (preferred && isOpenLine(preferred)) return preferred;
  return sorted.find(isOpenLine) ?? null;
}

/** The warehouse tracks shelves when any line carries a location (lokasyonlu depo → shelf barcode required). */
export const usesLocations = (lines: readonly TaskLine[]) => lines.some((l) => l.locationId !== null);

export type ScannedLocation = {
  /** What is sent as `location` (barcode or code exactly as scanned). */
  code: string;
  /** The task line location it matches; null when the shelf is not on this task (the API will decide). */
  locationId: string | null;
  /** Human label: the location code when known. */
  label: string;
};

const same = (a: string | null | undefined, b: string) => !!a && a.toUpperCase() === b.toUpperCase();

/** A scanned shelf barcode / code matched against the task's lines. */
export function matchLocation(code: string, lines: readonly TaskLine[]): ScannedLocation | null {
  for (const line of lines) {
    if (line.locationId && (line.locationBarcode === code || same(line.locationCode, code))) {
      return { code, locationId: line.locationId, label: line.locationCode ?? code };
    }
  }
  return null;
}

/** A scanned product barcode / SKU matched against the task's lines (barcode exact, SKU case-insensitive like the API). */
export function matchItem(code: string, lines: readonly TaskLine[]): TaskLine[] {
  return sortLines(lines).filter((l) => l.barcode === code || same(l.sku, code));
}

export type ScanReading = { kind: "location"; location: ScannedLocation } | { kind: "item"; code: string };

/**
 * How a scan is read:
 * 1. a product of this task → item;
 * 2. a shelf of this task → location;
 * 3. unknown code in a shelf-tracked warehouse: while no shelf is scanned it is taken as the shelf (the API answers
 *    picking.unknown_location / wrong_location on the item scan), once a shelf is set it is sent as an item (the API
 *    answers picking.unknown_code / wrong_item);
 * 4. otherwise → item.
 */
export function readScan(raw: string, lines: readonly TaskLine[], location: ScannedLocation | null): ScanReading | null {
  const code = raw.trim();
  if (!code) return null;
  if (matchItem(code, lines).length > 0) return { kind: "item", code };
  const shelf = matchLocation(code, lines);
  if (shelf) return { kind: "location", location: shelf };
  if (usesLocations(lines) && location === null) return { kind: "location", location: { code, locationId: null, label: code } };
  return { kind: "item", code };
}

/** What the screen asks for next. */
export type PickStep = "done" | "location" | "item";

export function pickStep(lines: readonly TaskLine[], current: TaskLine | null, location: ScannedLocation | null): PickStep {
  if (!current) return "done";
  if (current.locationId && location === null) return "location";
  return "item";
}

/**
 * Keep the scanned shelf only while it is still useful: when the next line is on another (known) shelf the worker has
 * to walk there and scan it.
 */
export function keepLocation(location: ScannedLocation | null, next: TaskLine | null): ScannedLocation | null {
  if (!location || !next) return null;
  if (!next.locationId) return null;
  if (location.locationId === null) return location;
  return location.locationId === next.locationId ? location : null;
}

/** After a rejection: a wrong or unknown shelf is forgotten (scan the right one); everything else stays as it was. */
export function locationAfterRejection(code: string | null, location: ScannedLocation | null): ScannedLocation | null {
  return code === "picking.wrong_location" || code === "picking.unknown_location" ? null : location;
}

/** Merges a scan's per-line progress into the loaded task (only changed lines come back). */
export function applyScan(detail: TaskDetail, response: PickScanResponse): TaskDetail {
  const byId = new Map<string, PickLineProgress>(response.lines.map((l) => [l.id, l]));
  const picked = (n: number | string) => toNumber(n);
  const lines = detail.lines.map((line) => {
    const p = byId.get(line.id);
    return p
      ? { ...line, quantityPicked: p.quantityPicked, quantityShort: p.quantityShort, quantityRemaining: p.quantityRemaining }
      : line;
  });
  const sum = (key: "quantityPicked" | "quantityShort") => lines.reduce((acc, l) => Math.round((acc + picked(l[key])) * 1000) / 1000, 0);
  return {
    ...detail,
    task: { ...detail.task, status: response.status, version: response.version, quantityPicked: sum("quantityPicked"), quantityShort: sum("quantityShort") },
    lines,
  };
}

export const MIN_STEP = 1;
export const MAX_STEP = 9999;

/** Stepper bounds: 1 … what is still open on the line (never invites an over-pick). */
export function clampStep(value: number, line: TaskLine | null): number {
  const max = line ? Math.max(MIN_STEP, Math.min(MAX_STEP, Math.floor(remaining(line)) || MIN_STEP)) : MAX_STEP;
  if (!Number.isFinite(value)) return MIN_STEP;
  return Math.min(max, Math.max(MIN_STEP, Math.floor(value)));
}

export type TaskTotals = { toPick: number; picked: number; short: number; open: number; shortLines: number; openLines: number };

export function taskTotals(lines: readonly TaskLine[]): TaskTotals {
  const totals: TaskTotals = { toPick: 0, picked: 0, short: 0, open: 0, shortLines: 0, openLines: 0 };
  const add = (a: number, b: number) => Math.round((a + b) * 1000) / 1000;
  for (const l of lines) {
    totals.toPick = add(totals.toPick, toNumber(l.quantityToPick));
    totals.picked = add(totals.picked, toNumber(l.quantityPicked));
    totals.short = add(totals.short, toNumber(l.quantityShort));
    totals.open = add(totals.open, remaining(l));
    if (toNumber(l.quantityShort) > 0) totals.shortLines++;
    if (isOpenLine(l)) totals.openLines++;
  }
  return totals;
}

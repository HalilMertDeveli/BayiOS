import { describe, expect, it } from "vitest";
import type { CountLine } from "./api";
import { countReportTotals, filterCountLines, lineEntry, parseCountQuantity, scanEntry } from "./count-form";

// MOCK count lines (fixtures, not real data).
const line = (patch: Partial<CountLine>): CountLine => ({
  id: "l-1",
  variantId: "v-1",
  sku: "SKU-1",
  locationId: "loc-a",
  locationCode: "A-01",
  expectedQuantity: 10,
  countedQuantity: null,
  difference: null,
  snapshotAt: "2026-11-09T08:00:00Z",
  countedAt: null,
  status: "pending",
  movedSinceSnapshot: false,
  appliedDelta: null,
  ...patch,
});

describe("count quantities", () => {
  it("set accepts 0, add needs more than 0", () => {
    expect(parseCountQuantity("0", "set")).toEqual({ ok: true, value: 0 });
    expect(parseCountQuantity("0,0", "set")).toEqual({ ok: true, value: 0 });
    expect(parseCountQuantity("0", "add").ok).toBe(false);
    expect(parseCountQuantity("2,5", "add")).toEqual({ ok: true, value: 2.5 });
    expect(parseCountQuantity("abc", "set").ok).toBe(false);
  });

  it("entries address a line or a scanned code + shelf", () => {
    expect(lineEntry("l-1", 4)).toEqual({ lineId: "l-1", variantId: null, code: null, locationId: null, locationCode: null, quantity: 4, mode: "set" });
    expect(scanEntry(" 869000 ", " A-01 ", 1, "add")).toEqual({ lineId: null, variantId: null, code: "869000", locationId: null, locationCode: "A-01", quantity: 1, mode: "add" });
    expect(scanEntry("SKU-1", null, 3, "set").locationCode).toBeNull();
  });
});

describe("count lines", () => {
  const lines = [
    line({}),
    line({ id: "l-2", sku: "SKU-2", locationCode: "B-02", countedQuantity: 8, difference: -2, status: "counted" }),
    line({ id: "l-3", sku: "SKU-3", countedQuantity: 5, difference: 0, status: "counted", movedSinceSnapshot: true }),
    line({ id: "l-4", sku: "SKU-4", countedQuantity: 13, difference: 3, expectedQuantity: 10, status: "counted" }),
  ];

  it("filters by text and state", () => {
    expect(filterCountLines(lines, "b-02", "all").map((l) => l.id)).toEqual(["l-2"]);
    expect(filterCountLines(lines, "", "pending").map((l) => l.id)).toEqual(["l-1"]);
    expect(filterCountLines(lines, "", "difference").map((l) => l.id)).toEqual(["l-2", "l-4"]);
    expect(filterCountLines(lines, "", "moved").map((l) => l.id)).toEqual(["l-3"]);
  });

  it("report totals over the counted lines", () => {
    expect(countReportTotals(lines)).toEqual({ expected: 30, counted: 26, plus: 3, minus: -2 });
  });

  it("blind for counters: no expected quantities, totals still count", () => {
    const blind = lines.map((l) => ({ ...l, expectedQuantity: null, difference: null }));
    expect(countReportTotals(blind)).toEqual({ expected: 0, counted: 26, plus: 0, minus: 0 });
    expect(filterCountLines(blind, "", "difference")).toEqual([]);
  });
});

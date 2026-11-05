import { describe, expect, it } from "vitest";
import {
  applyScan,
  clampStep,
  currentLine,
  keepLocation,
  locationAfterRejection,
  pickStep,
  readScan,
  sortLines,
  taskTotals,
  usesLocations,
} from "./pick-session";
import { taskLine, twoShelfTask } from "./test-fixtures";

describe("pick session", () => {
  it("orders lines by shelf pick sequence and starts at the first open line", () => {
    const { lines } = twoShelfTask();
    expect(sortLines(lines).map((l) => l.sku)).toEqual(["MOCK-001", "MOCK-002"]);
    expect(currentLine(lines)?.sku).toBe("MOCK-001");
  });

  it("keeps a chosen line while it is open and skips closed lines", () => {
    const lines = [taskLine({ quantityRemaining: 0, quantityPicked: 3 }), taskLine({ id: "l2", sequence: 2, sku: "MOCK-002" })];
    expect(currentLine(lines)?.id).toBe("l2");
    expect(currentLine(lines, "l1")?.id).toBe("l2");
    expect(currentLine([taskLine({ quantityRemaining: 0 })])).toBeNull();
  });

  it("reads a shelf barcode or code as a location, product barcode or SKU as an item", () => {
    const { lines } = twoShelfTask();
    expect(readScan("LOC-A01", lines, null)).toEqual({ kind: "location", location: { code: "LOC-A01", locationId: "loc-a", label: "A-01" } });
    expect(readScan("b-02", lines, null)).toMatchObject({ kind: "location", location: { locationId: "loc-b" } });
    expect(readScan("8690000000011", lines, null)).toEqual({ kind: "item", code: "8690000000011" });
    expect(readScan("mock-002", lines, null)).toEqual({ kind: "item", code: "mock-002" });
    expect(readScan("   ", lines, null)).toBeNull();
  });

  it("takes an unknown code as the shelf until one is scanned, then as an item (the API judges both)", () => {
    const { lines } = twoShelfTask();
    expect(readScan("LOC-Z99", lines, null)).toEqual({ kind: "location", location: { code: "LOC-Z99", locationId: null, label: "LOC-Z99" } });
    const shelf = { code: "LOC-A01", locationId: "loc-a", label: "A-01" };
    expect(readScan("XYZ", lines, shelf)).toEqual({ kind: "item", code: "XYZ" });
    // Without shelf tracking every code is an item.
    const flat = [taskLine({ locationId: null, locationCode: null, locationBarcode: null })];
    expect(usesLocations(flat)).toBe(false);
    expect(readScan("XYZ", flat, null)).toEqual({ kind: "item", code: "XYZ" });
  });

  it("asks for the shelf first, then the item, and is done when nothing is open", () => {
    const { lines } = twoShelfTask();
    const current = currentLine(lines);
    expect(pickStep(lines, current, null)).toBe("location");
    expect(pickStep(lines, current, { code: "LOC-A01", locationId: "loc-a", label: "A-01" })).toBe("item");
    expect(pickStep(lines, null, null)).toBe("done");
    const flat = [taskLine({ locationId: null })];
    expect(pickStep(flat, flat[0], null)).toBe("item");
  });

  it("keeps the scanned shelf only while the next line is on it", () => {
    const { lines } = twoShelfTask();
    const a = { code: "LOC-A01", locationId: "loc-a", label: "A-01" };
    expect(keepLocation(a, lines.find((l) => l.id === "l1")!)).toBe(a);
    expect(keepLocation(a, lines.find((l) => l.id === "l2")!)).toBeNull();
    expect(keepLocation(a, null)).toBeNull();
  });

  it("forgets a wrong or unknown shelf after a rejection, keeps it otherwise", () => {
    const a = { code: "LOC-A01", locationId: "loc-a", label: "A-01" };
    expect(locationAfterRejection("picking.wrong_location", a)).toBeNull();
    expect(locationAfterRejection("picking.unknown_location", a)).toBeNull();
    expect(locationAfterRejection("picking.wrong_item", a)).toBe(a);
    expect(locationAfterRejection("picking.over_pick", a)).toBe(a);
  });

  it("merges scan progress into the task and recomputes totals", () => {
    const detail = twoShelfTask();
    const next = applyScan(detail, {
      taskId: "t1",
      status: "in_progress",
      version: 2,
      openLineCount: 2,
      lines: [{ id: "l1", variantId: "v1", sku: "MOCK-001", locationId: "loc-a", quantityToPick: 3, quantityPicked: 1, quantityShort: 0, quantityRemaining: 2 }],
    });
    expect(next.task.status).toBe("in_progress");
    expect(next.task.version).toBe(2);
    expect(next.task.quantityPicked).toBe(1);
    expect(next.lines.find((l) => l.id === "l1")?.quantityRemaining).toBe(2);
    expect(next.lines.find((l) => l.id === "l2")).toBe(detail.lines[0]);
    expect(taskTotals(next.lines)).toMatchObject({ toPick: 5, picked: 1, open: 4, openLines: 2, shortLines: 0 });
  });

  it("clamps the quantity stepper to 1 … what is still open", () => {
    const line = taskLine({ quantityRemaining: 3 });
    expect(clampStep(0, line)).toBe(1);
    expect(clampStep(2, line)).toBe(2);
    expect(clampStep(9, line)).toBe(3);
    expect(clampStep(Number.NaN, line)).toBe(1);
    expect(clampStep(5, null)).toBe(5);
  });
});

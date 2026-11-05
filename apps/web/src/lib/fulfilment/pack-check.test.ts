import { describe, expect, it } from "vitest";
import { barcodeMap, emptyPack, newPackage, packProgress, packRequest, packScan, undoOne } from "./pack-check";
import { shipmentLine, taskLine } from "./test-fixtures";

const lines = [shipmentLine(), shipmentLine({ id: "s2", variantId: "v2", sku: "MOCK-002", quantity: 1, pickedQuantity: 1 })];
const barcodes = barcodeMap([taskLine(), taskLine({ variantId: "v2", barcode: null })]);

describe("pack check", () => {
  it("matches scans by task barcode or SKU and counts per package", () => {
    let state = emptyPack();
    const a = packScan(state, "8690000000011", 1, lines, barcodes);
    expect(a.ok).toBe(true);
    if (!a.ok) return;
    state = a.state;
    const b = packScan(state, "mock-002", 1, lines, barcodes);
    expect(b.ok && b.line.id).toBe("s2");
    if (!b.ok) return;
    state = newPackage(b.state);
    expect(state.current).toBe(1);
    const c = packScan(state, "MOCK-001", 1, lines, barcodes);
    expect(c.ok).toBe(true);
    if (!c.ok) return;
    const progress = packProgress(c.state, lines);
    expect(progress.complete).toBe(true);
    expect(progress.packageCount).toBe(2);
    expect(packRequest(c.state)).toEqual({
      packages: [
        { items: [{ code: null, shipmentLineId: "s1", quantity: 1 }, { code: null, shipmentLineId: "s2", quantity: 1 }] },
        { items: [{ code: null, shipmentLineId: "s1", quantity: 1 }] },
      ],
    });
  });

  it("rejects an item that is not in the shipment and packing more than ships", () => {
    expect(packScan(emptyPack(), "NOPE", 1, lines, barcodes)).toEqual({ ok: false, reason: "unknown" });
    const one = packScan(emptyPack(), "MOCK-002", 1, lines, barcodes);
    if (!one.ok) throw new Error("expected ok");
    expect(packScan(one.state, "MOCK-002", 1, lines, barcodes)).toMatchObject({ ok: false, reason: "over" });
  });

  it("ignores lines that ship nothing (fully short) and allows undo of a mis-scan", () => {
    const short = [shipmentLine({ quantity: 0, shortQuantity: 2 })];
    expect(packScan(emptyPack(), "MOCK-001", 1, short, barcodes)).toEqual({ ok: false, reason: "unknown" });
    const one = packScan(emptyPack(), "MOCK-001", 1, lines, barcodes);
    if (!one.ok) throw new Error("expected ok");
    const undone = undoOne(one.state, "s1");
    expect(undone.packages[0]).toEqual({});
    expect(packProgress(undone, lines).complete).toBe(false);
  });

  it("does not open an empty extra package", () => {
    const state = emptyPack();
    expect(newPackage(state)).toBe(state);
  });
});

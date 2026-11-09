import { describe, expect, it } from "vitest";
import type { Balance } from "@/lib/inventory/api";
import type { TransferLine } from "./api";
import {
  addTransferLine,
  availabilityFrom,
  bestShelf,
  buildReceive,
  buildTransferRequest,
  initialReceiveRows,
  transferTotals,
  validateTransfer,
  type TransferFormValues,
} from "./transfer-form";

// MOCK ids and quantities (fixtures, not real data).
const variant = { id: "v-1", sku: "SKU-1", name: null, barcode: null };
const other = { id: "v-2", sku: "SKU-2", name: null, barcode: null };
const balance = (locationId: string | null, available: number, warehouseId = "w-1"): Balance =>
  ({ id: `b-${locationId}`, variantId: "v-1", warehouseId, locationId, onHand: available + 1, reserved: 1, available, version: 1, updatedAt: null, minQuantity: null, belowMinimum: false }) as Balance;

const values = (patch: Partial<TransferFormValues> = {}): TransferFormValues => ({
  sourceWarehouseId: "w-1",
  destinationWarehouseId: "w-2",
  note: "",
  lines: [{ key: "l1", variant, sourceLocationId: "loc-a", destinationLocationId: "", quantity: "3" }],
  ...patch,
});

describe("availability", () => {
  it("sums available stock per source shelf of the source warehouse only", () => {
    const map = availabilityFrom([balance("loc-a", 4), balance("loc-b", 7), balance("loc-a", 9, "w-other")], "w-1");
    expect(map.get("loc-a")).toBe(4);
    expect(map.get("loc-b")).toBe(7);
    expect(bestShelf(map)).toBe("loc-b");
    expect(bestShelf(availabilityFrom([balance(null, 5)], "w-1"))).toBe("");
  });
});

describe("validateTransfer", () => {
  const availability = () => new Map([["loc-a", 5]]);

  it("accepts a valid draft", () => {
    expect(validateTransfer(values(), { sourceUsesLocations: true, availability })).toEqual({});
  });

  it("refuses the same warehouse, missing shelf and more than available", () => {
    const errors = validateTransfer(
      values({ destinationWarehouseId: "w-1", lines: [{ key: "l1", variant, sourceLocationId: "", destinationLocationId: "", quantity: "3" }] }),
      { sourceUsesLocations: true, availability },
    );
    expect(errors.destinationWarehouseId).toContain("farklı olmalı");
    expect(errors["lines[0].sourceLocationId"]).toBe("Kaynak rafı seçin.");
    expect(validateTransfer(values({ lines: [{ ...values().lines[0], quantity: "6" }] }), { sourceUsesLocations: true, availability })["lines[0].quantity"]).toBe(
      "Kaynakta satılabilir yalnızca 5. Miktarı azaltın.",
    );
    expect(validateTransfer(values({ lines: [{ ...values().lines[0], sourceLocationId: "loc-z" }] }), { sourceUsesLocations: true, availability })["lines[0].quantity"]).toBe(
      "Kaynakta bu ürünün satılabilir stoğu yok.",
    );
  });

  it("skips the stock check while availability is unknown, and flags duplicates", () => {
    const line = values().lines[0];
    const errors = validateTransfer(values({ lines: [line, { ...line, key: "l2" }] }), { sourceUsesLocations: true, availability: () => undefined });
    expect(errors["lines[0].quantity"]).toBeUndefined();
    expect(errors["lines[1].sourceLocationId"]).toContain("zaten eklendi");
    expect(validateTransfer(values({ lines: [] }), { sourceUsesLocations: false, availability }).lines).toBe("En az bir ürün ekleyin.");
  });

  it("builds the request with shelves only where the warehouse uses them", () => {
    const body = buildTransferRequest(values({ note: "  plaka 34 X  " }), { sourceUsesLocations: false, destinationUsesLocations: false });
    expect(body).toEqual({
      sourceWarehouseId: "w-1",
      destinationWarehouseId: "w-2",
      note: "plaka 34 X",
      lines: [{ variantId: "v-1", quantity: 3, sourceLocationId: null, destinationLocationId: null }],
    });
  });

  it("a scan adds +1 to the same variant and shelf, otherwise a new line", () => {
    let next = addTransferLine([], variant, "loc-a", () => "k1");
    next = addTransferLine(next.lines, variant, "loc-a", () => "k2");
    expect(next.lines).toHaveLength(1);
    expect(next.lines[0].quantity).toBe("2");
    next = addTransferLine(next.lines, other, "", () => "k3");
    expect(next.lines).toHaveLength(2);
  });
});

const line = (patch: Partial<TransferLine>): TransferLine => ({
  id: "line-1",
  lineNo: 1,
  variantId: "v-1",
  sku: "SKU-1",
  sourceLocationId: null,
  sourceLocationCode: null,
  destinationLocationId: "dest-1",
  destinationLocationCode: "B-01",
  quantity: 10,
  receivedQuantity: 4,
  inTransitQuantity: 6,
  returnedQuantity: 0,
  lostQuantity: 0,
  discrepancyQuantity: 6,
  ...patch,
});

describe("receipt", () => {
  it("pre-fills what is still in transit and the planned shelf", () => {
    expect(initialReceiveRows([line({}), line({ id: "line-2", inTransitQuantity: 0 })])).toEqual({ "line-1": { quantity: "6", locationId: "dest-1" } });
  });

  it("partial receipt: empty rows are skipped, more than in transit is refused", () => {
    const lines = [line({}), line({ id: "line-2", sku: "SKU-2" })];
    expect(buildReceive(lines, { "line-1": { quantity: "2,5", locationId: "dest-1" }, "line-2": { quantity: "", locationId: "" } }, { destinationUsesLocations: true })).toEqual({
      ok: true,
      lines: [{ lineId: "line-1", variantId: null, code: null, quantity: 2.5, locationId: "dest-1", locationCode: null }],
    });
    const over = buildReceive(lines, { "line-1": { quantity: "7", locationId: "dest-1" } }, { destinationUsesLocations: true });
    expect(over.ok).toBe(false);
    expect(!over.ok && over.errors["line-1"]).toContain("Yolda yalnızca 6 var");
    const none = buildReceive(lines, {}, { destinationUsesLocations: false });
    expect(!none.ok && none.errors._form).toBeTruthy();
    const noShelf = buildReceive(lines, { "line-1": { quantity: "1", locationId: "" } }, { destinationUsesLocations: true });
    expect(!noShelf.ok && noShelf.errors["line-1"]).toBe("Hedef rafı seçin.");
  });

  it("totals of the discrepancy report", () => {
    expect(transferTotals([line({ receivedQuantity: 7, inTransitQuantity: 0, lostQuantity: 3 }), line({ quantity: 5, receivedQuantity: 5, inTransitQuantity: 0 })])).toEqual({
      sent: 15,
      received: 12,
      inTransit: 0,
      returned: 0,
      lost: 3,
    });
  });
});

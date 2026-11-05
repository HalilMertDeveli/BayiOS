import type { ShipmentLine, TaskDetail, TaskLine } from "./api";

/** Synthetic picking task lines (invented SKUs/barcodes) for unit tests. */
export function taskLine(overrides: Partial<TaskLine> = {}): TaskLine {
  return {
    id: "l1",
    sequence: 1,
    orderLineId: "ol1",
    variantId: "v1",
    sku: "MOCK-001",
    name: "Mock ürün 1",
    unit: "ADET",
    barcode: "8690000000011",
    locationId: "loc-a",
    locationCode: "A-01",
    locationBarcode: "LOC-A01",
    quantityToPick: 3,
    quantityPicked: 0,
    quantityShort: 0,
    quantityRemaining: 3,
    shortageReason: null,
    ...overrides,
  };
}

export function twoShelfTask(): TaskDetail {
  return {
    task: {
      id: "t1",
      type: "pick",
      status: "open",
      orderId: "o1",
      orderNumber: "SIP-2026-000001",
      warehouseId: "w1",
      assignedTo: "u1",
      assigneeName: null,
      lineCount: 2,
      quantityToPick: 5,
      quantityPicked: 0,
      quantityShort: 0,
      createdAt: "2026-11-05T08:00:00Z",
      startedAt: null,
      completedAt: null,
      version: 1,
    },
    // Deliberately out of order: the screen sorts by sequence.
    lines: [
      taskLine({ id: "l2", sequence: 2, variantId: "v2", sku: "MOCK-002", name: "Mock ürün 2", barcode: "8690000000028", locationId: "loc-b", locationCode: "B-02", locationBarcode: "LOC-B02", quantityToPick: 2, quantityRemaining: 2 }),
      taskLine(),
    ],
    shipmentId: null,
  };
}

export function shipmentLine(overrides: Partial<ShipmentLine> = {}): ShipmentLine {
  return {
    id: "s1",
    orderLineId: "ol1",
    variantId: "v1",
    sku: "MOCK-001",
    name: "Mock ürün 1",
    unit: "ADET",
    barcode: null,
    locationId: null,
    locationCode: null,
    pickedQuantity: 2,
    quantity: 2,
    packedQuantity: null,
    shortQuantity: 0,
    shortageReason: null,
    ...overrides,
  };
}

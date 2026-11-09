import type { Movement } from "@/lib/inventory/api";

/**
 * In-transit legs of a two-stage transfer (OD-01) are booked on the transfer's own ledger point: the movement's
 * `warehouseId` is the transfer id (= `sourceId`), not a warehouse. Returns that transfer id, or null for a normal row.
 */
export function transitTransferId(movement: Pick<Movement, "warehouseId" | "sourceId">): string | null {
  return movement.sourceId && movement.warehouseId === movement.sourceId ? movement.sourceId : null;
}

/**
 * The transfer's document number as the API derives it ("TRF-" + the id's last 8 hex digits, upper case — see
 * StockTransfer.Create), so a movement row can name its transfer without one extra request per row.
 */
export function transferNumberFromId(id: string): string {
  const hex = id.replace(/-/g, "");
  return `TRF-${hex.slice(-8).toUpperCase()}`;
}

/** "Yolda (Transfer TRF-1A2B3C4D)". */
export function transitLabel(transferId: string): string {
  return `Yolda (Transfer ${transferNumberFromId(transferId)})`;
}

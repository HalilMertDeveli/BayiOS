import type { Movement } from "@/lib/inventory/api";

/**
 * In-transit legs of a two-stage transfer (OD-01) are booked on the transfer's own ledger point. The API flags them
 * (`isTransit`, #98) and names the document (`transferNumber`); the transfer id is the movement's `sourceId`.
 * Returns that transfer id, or null for a normal row.
 */
export function transitTransferId(movement: Pick<Movement, "isTransit" | "sourceId">): string | null {
  return movement.isTransit && movement.sourceId ? movement.sourceId : null;
}

/**
 * The movement's description / reference column: its reason (note), and the transfer document number the API sends on
 * every two-stage transfer leg ("Transfer TRF-0042"); null when there is neither. Shelf moves carry no number.
 */
export function movementReference(movement: Pick<Movement, "reason" | "transferNumber">): string | null {
  const parts = [movement.reason, movement.transferNumber ? `Transfer ${movement.transferNumber}` : null].filter((p): p is string => !!p);
  return parts.length > 0 ? parts.join(" · ") : null;
}

/** "Yolda (Transfer TRF-1A2B3C4D)"; just "Yolda (transfer)" when the API sent no number. */
export function transitLabel(movement: Pick<Movement, "transferNumber">): string {
  return movement.transferNumber ? `Yolda (Transfer ${movement.transferNumber})` : "Yolda (transfer)";
}

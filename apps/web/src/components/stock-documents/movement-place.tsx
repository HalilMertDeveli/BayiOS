"use client";

import Link from "next/link";
import { Badge } from "@/components/ui/badge";
import type { Movement } from "@/lib/inventory/api";
import { transferHref } from "@/lib/stock-documents/api";
import { transferPermissions } from "@/lib/stock-documents/permissions";
import { transitLabel, transitTransferId } from "@/lib/stock-documents/transit";
import { useSession } from "@/lib/session/session-context";

type Place = Pick<Movement, "warehouseId" | "sourceId" | "isTransit" | "transferNumber">;

/**
 * Where a movement happened: the warehouse name, or — for the in-transit leg of a two-stage transfer (`isTransit`) —
 * "Yolda (Transfer TRF-…)" with the number the API sends, linking to the transfer when the user may open it.
 */
export function MovementPlace({ movement, warehouseLabel }: { movement: Place; warehouseLabel: (id: string) => string }) {
  const { access } = useSession();
  const transferId = transitTransferId(movement);
  if (!transferId) return <>{warehouseLabel(movement.warehouseId)}</>;
  const label = transitLabel(movement);
  return transferPermissions(access).read ? (
    <Link href={transferHref(transferId)} className="inline-flex items-center rounded-sm underline-offset-2 hover:underline">
      <Badge tone="warning">{label}</Badge>
    </Link>
  ) : (
    <Badge tone="warning">{label}</Badge>
  );
}

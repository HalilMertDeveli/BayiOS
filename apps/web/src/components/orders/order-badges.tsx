import { Badge, OrderStatusBadge } from "@/components/ui/badge";
import { fulfillmentMetaFor, orderBadgeKey, orderStatusLabel } from "@/lib/orders/labels";

/** Order status as text + color + icon (design-system-plan §2.2). Unknown values are shown as neutral text. */
export function OrderStatus({ status }: { status: string }) {
  const key = orderBadgeKey(status);
  return key ? <OrderStatusBadge status={key} /> : <Badge>{orderStatusLabel(status)}</Badge>;
}

/**
 * fulfillment_status next to the status; "none" is shown only when asked (it is the normal case before shipping).
 * With the order status, a short-closed order (shipped/completed + partial) reads "Kalan kapatıldı" (#133).
 */
export function FulfillmentBadge({ status, orderStatus, showNone = false }: { status: string; orderStatus?: string; showNone?: boolean }) {
  if (status === "none" && !showNone) return null;
  const meta = fulfillmentMetaFor(status, orderStatus);
  return <Badge tone={meta.tone}>{meta.label}</Badge>;
}

export function OrderStatusCell({ status, fulfillmentStatus }: { status: string; fulfillmentStatus: string }) {
  return (
    <span className="flex flex-wrap items-center gap-1">
      <OrderStatus status={status} />
      <FulfillmentBadge status={fulfillmentStatus} orderStatus={status} />
    </span>
  );
}

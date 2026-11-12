import type { ReactNode } from "react";
import {
  Check,
  CheckCheck,
  CircleX,
  Clock,
  Package,
  Pencil,
  ShoppingCart,
  Truck,
  type IconComponent,
} from "@/components/ui/icons";
import { cn } from "@/lib/cn";

export type BadgeTone = "neutral" | "info" | "success" | "warning" | "danger" | "purple" | "teal" | "brand" | "ai";

const toneClasses: Record<BadgeTone, string> = {
  neutral: "bg-neutral-bg text-neutral-fg border-neutral-border",
  info: "bg-info-bg text-info-fg border-info-border",
  success: "bg-success-bg text-success-fg border-success-border",
  warning: "bg-warning-bg text-warning-fg border-warning-border",
  danger: "bg-danger-bg text-danger-fg border-danger-border",
  purple: "bg-purple-bg text-purple-fg border-purple-border",
  teal: "bg-teal-bg text-teal-fg border-teal-border",
  brand: "bg-brand-bg text-brand-fg border-brand-border",
  ai: "bg-ai-bg text-ai-fg border-ai-border",
};

export type BadgeProps = {
  tone?: BadgeTone;
  /** Decorative icon; the text must carry the meaning on its own (never color/icon only). */
  icon?: ReactNode;
  className?: string;
  children: ReactNode;
};

export function Badge({ tone = "neutral", icon, className, children }: BadgeProps) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-caption font-medium whitespace-nowrap",
        toneClasses[tone],
        className,
      )}
    >
      {icon}
      {children}
    </span>
  );
}

/** Order states as the API names them (docs/design/design-system-plan.md §2.2). */
export type OrderStatus =
  | "Draft"
  | "PendingApproval"
  | "Approved"
  | "Picking"
  | "Packed"
  | "Shipped"
  | "PartiallyShipped"
  | "Completed"
  | "Cancelled";

export const orderStatusMeta: Record<OrderStatus, { label: string; tone: BadgeTone; Icon: IconComponent }> = {
  Draft: { label: "Taslak", tone: "neutral", Icon: Pencil },
  PendingApproval: { label: "Onay Bekliyor", tone: "warning", Icon: Clock },
  Approved: { label: "Onaylandı", tone: "info", Icon: Check },
  Picking: { label: "Toplanıyor", tone: "purple", Icon: ShoppingCart },
  Packed: { label: "Paketlendi", tone: "teal", Icon: Package },
  Shipped: { label: "Sevk Edildi", tone: "brand", Icon: Truck },
  PartiallyShipped: { label: "Kısmi Sevk", tone: "info", Icon: Truck },
  Completed: { label: "Tamamlandı", tone: "success", Icon: CheckCheck },
  Cancelled: { label: "İptal", tone: "danger", Icon: CircleX },
};

export function OrderStatusBadge({ status, className }: { status: OrderStatus; className?: string }) {
  const { label, tone, Icon } = orderStatusMeta[status];
  return (
    <Badge tone={tone} icon={<Icon className="size-3.5" />} className={className}>
      {label}
    </Badge>
  );
}

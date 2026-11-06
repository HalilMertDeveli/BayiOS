import type { Entitlements } from "@/lib/billing/entitlements";
import { FEATURES, featureGate } from "@/lib/billing/entitlements";
import type { Decimal } from "@/lib/pricing/money";
import { reportAccess, reportDefinition } from "@/lib/reports/definitions";
import type { Access } from "@/lib/session/access";

/**
 * Pano (W03, #129): which overview tiles the signed-in user sees. Built only from existing endpoints, each tile gated on
 * the permission its endpoint needs (GET /me) — a tile the user may not load is not rendered, so no request is made
 * for it. Visibility only: the API checks every request again.
 */
export type DashboardTiles = {
  /** GET /orders?status=pending_approval — orders.read (unconditional; task-linked readers are warehouse staff). */
  pendingApproval: boolean;
  /** GET /reports/orders for today — reports.read incl. the order-report condition (sales). */
  todayOrders: boolean;
  /** GET /reports/critical-stock — reports.read without the order-only condition. */
  criticalStock: boolean;
  /** GET /picking-tasks/mine — tasks.read.own. */
  myTasks: boolean;
  /** GET /ai/order-parses?status=needs_review — whatsapp.parse, and the plan includes AI order parsing. */
  whatsappReview: boolean;
  /** Plan / trial from GET /me entitlements (no request) — billing.read. */
  plan: boolean;
};

export function dashboardTiles(access: Access, entitlements: Entitlements): DashboardTiles {
  return {
    pendingApproval: access.can("orders.read"),
    todayOrders: reportAccess(access, reportDefinition("orders")).read,
    criticalStock: reportAccess(access, reportDefinition("critical-stock")).read,
    myTasks: access.canSome("tasks.read.own"),
    whatsappReview: access.canSome("whatsapp.parse") && !featureGate(entitlements, FEATURES.aiOrderParsing).locked,
    plan: access.can("billing.read") && entitlements.known,
  };
}

export function anyTile(tiles: DashboardTiles): boolean {
  return Object.values(tiles).some(Boolean);
}

/** Shortcuts on the dashboard, each behind the permission of the screen's main action. */
export type QuickLink = { href: string; label: string };

export function quickLinks(access: Access, entitlements: Entitlements): QuickLink[] {
  const links: QuickLink[] = [];
  if (access.can("orders.create")) links.push({ href: "/siparisler/yeni", label: "Yeni sipariş" });
  if (access.canSome("whatsapp.parse") && !featureGate(entitlements, FEATURES.aiOrderParsing).locked) {
    links.push({ href: "/siparisler/whatsapp", label: "WhatsApp'tan sipariş" });
  }
  if (access.can("catalog.write")) links.push({ href: "/urunler/yeni", label: "Yeni ürün" });
  if (access.can("inventory.receive")) links.push({ href: "/depo/mal-kabul", label: "Mal kabul" });
  return links;
}

/** "12", or "50+" when the page was full and more exist (lists are paged; the API sends no total). */
export function countText(count: number, hasMore: boolean): string {
  return hasMore ? `${count}+` : String(count);
}

export type StatusCount = { status: string; currency: string; orderCount: Decimal; grandTotal: Decimal | null };

/** Today's order totals from the orders report: count of every status; amounts per currency without cancelled orders. */
export function summarizeOrders(byStatus: readonly StatusCount[]): {
  total: number;
  byStatus: { status: string; count: number }[];
  amounts: { currency: string; amount: number }[];
} {
  const counts = new Map<string, number>();
  const amounts = new Map<string, number>();
  for (const row of byStatus) {
    const n = Number(row.orderCount) || 0;
    counts.set(row.status, (counts.get(row.status) ?? 0) + n);
    if (row.status !== "cancelled" && row.grandTotal !== null && row.grandTotal !== undefined) {
      amounts.set(row.currency, (amounts.get(row.currency) ?? 0) + Number(row.grandTotal));
    }
  }
  const list = [...counts.entries()].filter(([, n]) => n > 0).map(([status, count]) => ({ status, count }));
  return {
    total: list.reduce((s, x) => s + x.count, 0),
    byStatus: list,
    amounts: [...amounts.entries()].map(([currency, amount]) => ({ currency, amount: Math.round(amount * 100) / 100 })),
  };
}

/** Page sizes: one page per tile (count shown as "50+" when more exist), the newest rows listed. */
export const PENDING_PAGE = 50;
export const TILE_ROWS = 5;
export const PARSES_PAGE = 20;

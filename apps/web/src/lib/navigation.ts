import {
  ArrowLeftRight,
  ChartColumn,
  ClipboardCheck,
  ClipboardList,
  FileUp,
  LayoutDashboard,
  Package,
  Boxes,
  Settings,
  ShoppingCart,
  Tags,
  Truck,
  Users,
  Warehouse,
  type LucideIcon,
} from "lucide-react";
import type { Access } from "@/lib/session/access";

/** Fixed MVP role set (docs/product/roles-permissions.md §2) — for labels only; what a role may do comes from GET /me. */
export const ROLES = ["owner", "admin", "manager", "sales", "warehouse_staff", "viewer"] as const;
export type Role = (typeof ROLES)[number];

export const roleLabels: Record<Role, string> = {
  owner: "İşletme sahibi",
  admin: "Yönetici",
  manager: "Operasyon müdürü",
  sales: "Satış",
  warehouse_staff: "Depo personeli",
  viewer: "İzleyici",
};

export type NavItem = {
  href: string;
  label: string;
  /** One-line description shown on the placeholder page. */
  description: string;
  Icon: LucideIcon;
  /** Whether the effective permissions (GET /me) show the item. Visibility only — the API enforces permissions. */
  visible: (access: Access) => boolean;
};

export type NavSection = { id: string; label: string; items: readonly NavItem[] };

const tasks = (a: Access) => a.canSome("tasks.read.all") || a.canSome("tasks.read.own");

/**
 * Menu derived from the page inventory (design-system-plan §4.1) and the effective permissions (any scope: a restricted
 * grant still opens the screen, the API filters the records):
 * Pano → orders.read (unconditional) or reports.read; Siparişler → orders.read; Ürünler → catalog.read;
 * Stok → inventory.read; Depo → inventory.read or tasks; Görevler and Sevkiyatlar → tasks.read.all | tasks.read.own
 * (x-permission-any-of); Transferler → inventory.transfer | inventory.transfer.receive; Sayımlar →
 * inventory.count.manage | inventory.count.perform; Müşteriler → customers.read; Fiyat listeleri → prices.read.sell; Raporlar → reports.read;
 * İçe aktarma → imports.run | imports.run.stock; Ayarlar → tenant.settings.manage | members.read | billing.read.
 */
export const navSections: readonly NavSection[] = [
  {
    id: "operasyon",
    label: "Operasyon",
    items: [
      {
        href: "/pano",
        label: "Pano",
        description: "Onay bekleyen siparişler, bugünkü siparişler, kritik stok ve görev özeti.",
        Icon: LayoutDashboard,
        visible: (a) => a.can("orders.read") || a.canSome("reports.read"),
      },
      {
        href: "/siparisler",
        label: "Siparişler",
        description: "Sipariş listesi, WhatsApp'tan sipariş ve onay akışı burada olacak.",
        Icon: ShoppingCart,
        visible: (a) => a.canSome("orders.read"),
      },
      {
        href: "/urunler",
        label: "Ürünler",
        description: "Ürün kataloğu, barkodlar ve fiyatlar burada olacak.",
        Icon: Package,
        visible: (a) => a.canSome("catalog.read"),
      },
      {
        href: "/stok",
        label: "Stok",
        description: "Depo bazında satılabilir, rafta ve ayrılmış stok burada olacak.",
        Icon: Boxes,
        visible: (a) => a.canSome("inventory.read"),
      },
      {
        href: "/depo",
        label: "Depo",
        description: "Toplama ve paketleme görevleri, mal kabul ve sayım burada olacak.",
        Icon: Warehouse,
        visible: (a) => a.canSome("inventory.read") || tasks(a),
      },
      {
        href: "/depo/gorevler",
        label: "Görevler",
        description: "Toplama görevleri: görevlerim, havuz ve raf sırasıyla barkodlu toplama.",
        Icon: ClipboardList,
        visible: tasks,
      },
      {
        href: "/depo/sevkiyatlar",
        label: "Sevkiyatlar",
        description: "Taslak sevkiyatlar, paket kontrolü ve sevk onayı.",
        Icon: Truck,
        visible: tasks,
      },
      {
        href: "/depo/transferler",
        label: "Transferler",
        description: "Depolar arası iki aşamalı transfer: gönder, yolda, teslim al ve fark raporu.",
        Icon: ArrowLeftRight,
        visible: (a) => a.canSome("inventory.transfer") || a.canSome("inventory.transfer.receive"),
      },
      {
        href: "/depo/sayimlar",
        label: "Sayımlar",
        description: "Stok sayımı: kapsam, kör sayım, okutarak sayım, fark raporu ve onay.",
        Icon: ClipboardCheck,
        visible: (a) => a.canSome("inventory.count.manage") || a.canSome("inventory.count.perform"),
      },
      {
        href: "/musteriler",
        label: "Müşteriler",
        description: "Müşteri kayıtları, adresler, bayi grupları ve fiyat listesi ataması.",
        Icon: Users,
        visible: (a) => a.canSome("customers.read"),
      },
    ],
  },
  {
    id: "yonetim",
    label: "Yönetim",
    items: [
      {
        href: "/fiyat-listeleri",
        label: "Fiyat listeleri",
        description: "Satış fiyat listeleri, miktar kademeleri ve fiyat sorgulama.",
        Icon: Tags,
        visible: (a) => a.canSome("prices.read.sell"),
      },
      {
        href: "/raporlar",
        label: "Raporlar",
        description: "Sipariş, stok, depo ve AI kullanım raporları; CSV/Excel dışa aktarma.",
        Icon: ChartColumn,
        visible: (a) => a.canSome("reports.read"),
      },
      {
        href: "/ice-aktarma",
        label: "İçe aktarma",
        description: "Excel/CSV ile ürün ve fiyat listesi içe aktarma: kolon eşleme, önizleme, hata raporu ve geri alma.",
        Icon: FileUp,
        visible: (a) => a.canSome("imports.run") || a.canSome("imports.run.stock"),
      },
      {
        href: "/ayarlar",
        label: "Ayarlar",
        description: "İşletme, ticari ayarlar, ekip ve roller burada olacak.",
        Icon: Settings,
        visible: (a) => a.canSome("tenant.settings.manage") || a.canSome("members.read") || a.canSome("billing.read"),
      },
    ],
  },
];

/** Sections and items the effective permissions show; empty sections are dropped. */
export function navFor(access: Access): NavSection[] {
  return navSections
    .map((section) => ({ ...section, items: section.items.filter((item) => item.visible(access)) }))
    .filter((section) => section.items.length > 0);
}

export function findNavItem(href: string): NavItem {
  for (const section of navSections) {
    const item = section.items.find((i) => i.href === href);
    if (item) return item;
  }
  throw new Error(`Unknown nav item: ${href}`);
}

/** True when `pathname` is the item's page or one of its sub-pages. */
export function isActive(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

/**
 * The menu item to mark as current: the longest href matching `pathname` (so /depo/gorevler/42 marks "Görevler",
 * not also "Depo").
 */
export function activeHref(pathname: string, hrefs: readonly string[]): string | null {
  let best: string | null = null;
  for (const href of hrefs) if (isActive(pathname, href) && (best === null || href.length > best.length)) best = href;
  return best;
}

/**
 * Where the user starts after sign-in / from the logo: the dashboard for order-desk users; users whose work is warehouse
 * tasks (no unconditional orders.read, but tasks.read.own — warehouse staff, W12) go straight to their tasks.
 */
export function homeFor(access: Access): string {
  return !access.can("orders.read") && !access.canSome("reports.read") && access.canSome("tasks.read.own") ? "/depo/gorevler" : "/pano";
}

import {
  ChartColumn,
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

/** Fixed MVP role set (docs/product/roles-permissions.md §2). `dealer_user` is reserved and not assignable. */
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
  /** Roles that see the item. Visibility only — the API enforces permissions. */
  roles: readonly Role[];
};

export type NavSection = { id: string; label: string; items: readonly NavItem[] };

const ALL: readonly Role[] = ROLES;
/** tasks.read.all (owner/admin/manager/viewer) or tasks.read.own (owner/admin/manager/warehouse_staff); sales K¹² fail-closed. */
const TASK_READERS: readonly Role[] = ["owner", "admin", "manager", "warehouse_staff", "viewer"];
/** tasks.read.own — shipments list/detail. */
const WAREHOUSE_WORKERS: readonly Role[] = ["owner", "admin", "manager", "warehouse_staff"];

/**
 * Menu derived from the page inventory (design-system-plan §4.1) and the permission matrix:
 * Pano → W03 roles; Siparişler → orders.read; Ürünler → catalog.read; Stok → inventory.read;
 * Depo → tasks.read.*; Görevler → tasks.read.all | tasks.read.own; Sevkiyatlar → tasks.read.own; Müşteriler → customers.read; Raporlar → reports.read;
 * Fiyat listeleri → prices.read.sell (W18; dealer groups live under Müşteriler at /musteriler/gruplar);
 * İçe aktarma → imports.run / imports.run.stock; Ayarlar → tenant.settings.manage / members.read.
 */
export const navSections: readonly NavSection[] = [
  {
    id: "operasyon",
    label: "Operasyon",
    items: [
      {
        href: "/pano",
        label: "Pano",
        description: "Bekleyen onaylar, kritik stok ve görev özeti burada olacak.",
        Icon: LayoutDashboard,
        roles: ["owner", "admin", "manager", "sales", "viewer"],
      },
      {
        href: "/siparisler",
        label: "Siparişler",
        description: "Sipariş listesi, WhatsApp'tan sipariş ve onay akışı burada olacak.",
        Icon: ShoppingCart,
        roles: ALL,
      },
      {
        href: "/urunler",
        label: "Ürünler",
        description: "Ürün kataloğu, barkodlar ve fiyatlar burada olacak.",
        Icon: Package,
        roles: ALL,
      },
      {
        href: "/stok",
        label: "Stok",
        description: "Depo bazında satılabilir, rafta ve ayrılmış stok burada olacak.",
        Icon: Boxes,
        roles: ALL,
      },
      {
        href: "/depo",
        label: "Depo",
        description: "Toplama ve paketleme görevleri, mal kabul ve sayım burada olacak.",
        Icon: Warehouse,
        roles: ALL,
      },
      {
        href: "/depo/gorevler",
        label: "Görevler",
        description: "Toplama görevleri: görevlerim, havuz ve raf sırasıyla barkodlu toplama.",
        Icon: ClipboardList,
        roles: TASK_READERS,
      },
      {
        href: "/depo/sevkiyatlar",
        label: "Sevkiyatlar",
        description: "Taslak sevkiyatlar, paket kontrolü ve sevk onayı.",
        Icon: Truck,
        roles: WAREHOUSE_WORKERS,
      },
      {
        href: "/musteriler",
        label: "Müşteriler",
        description: "Müşteri kayıtları, adresler, bayi grupları ve fiyat listesi ataması.",
        Icon: Users,
        roles: ["owner", "admin", "manager", "sales", "viewer"],
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
        roles: ["owner", "admin", "manager", "sales", "viewer"],
      },
      {
        href: "/raporlar",
        label: "Raporlar",
        description: "Satış, stok ve sipariş raporları burada olacak.",
        Icon: ChartColumn,
        roles: ["owner", "admin", "manager", "sales", "viewer"],
      },
      {
        href: "/ice-aktarma",
        label: "İçe aktarma",
        description: "Excel/CSV ile ürün, fiyat ve stok içe aktarma burada olacak.",
        Icon: FileUp,
        roles: ["owner", "admin", "manager"],
      },
      {
        href: "/ayarlar",
        label: "Ayarlar",
        description: "İşletme, ticari ayarlar, ekip ve roller burada olacak.",
        Icon: Settings,
        roles: ["owner", "admin", "manager"],
      },
    ],
  },
];

/** Sections and items visible to a role; empty sections are dropped. */
export function navForRole(role: Role): NavSection[] {
  return navSections
    .map((section) => ({ ...section, items: section.items.filter((item) => item.roles.includes(role)) }))
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

/** Where a role starts after sign-in / from the logo: warehouse staff go straight to their tasks (W12). */
export function homeFor(role: Role | null): string {
  return role === "warehouse_staff" ? "/depo/gorevler" : "/pano";
}
